param(
    [string]$Port = 'COM8',
    [string]$Repo = 'C:\_Work\ChameLab',
    [ValidatePattern('^[0-9a-fA-F]{12}$')]
    [string]$Key = 'FFFFFFFFFFFF',
    [string]$Dictionary = '',
    [string]$Python = '',
    [string]$OutputDirectory = '',
    [ValidateRange(1,10)][int]$Attempts = 3
)

$ErrorActionPreference = 'Stop'
$cliDirectory = Join-Path $Repo 'software\script'
if (-not (Test-Path -LiteralPath (Join-Path $cliDirectory 'chameleon_cli_unit.py'))) {
    throw "CLI not found: $cliDirectory. Set -Repo to the ChameLab repository."
}
if (-not $Python) {
    $venvPython = Join-Path $Repo 'software\.venv\Scripts\python.exe'
    if (Test-Path -LiteralPath $venvPython) { $Python = $venvPython }
    else { $Python = (Get-Command python -ErrorAction Stop).Source }
}
if ($Dictionary) { $Dictionary = (Resolve-Path -LiteralPath $Dictionary).Path }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PWD 'mfc4k-results' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$runDirectory = Join-Path $OutputDirectory ([DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
[void][IO.Directory]::CreateDirectory($runDirectory)
$workerPath = Join-Path $runDirectory 'recover_mfc4k.py'

# A Python worker uses this checkout's CLI classes and the same nested tools.
$worker = @'
import argparse
import json
import os
from pathlib import Path
import shutil
import sys
import time
import traceback

started = time.perf_counter()
reading_started = None
reading_finished = None

p = argparse.ArgumentParser()
p.add_argument('--cli', required=True)
p.add_argument('--port', required=True)
p.add_argument('--key', required=True)
p.add_argument('--dictionary', default='')
p.add_argument('--output', required=True)
p.add_argument('--attempts', type=int, default=3)
a = p.parse_args()
sys.path.insert(0, a.cli)
os.chdir(a.cli)
os.environ['PATH'] = a.cli + os.pathsep + os.environ.get('PATH', '')
try:
    import colorama
    from chameleon_com import ChameleonCom
    from chameleon_cmd import ChameleonCMD
    from chameleon_enum import MfcKeyType, Status
    from chameleon_cli_unit import HFMFFCHK, HFMFNested
    from chameleon_utils import default_cwd
except ImportError as exc:
    print(f'Missing CLI dependency: {exc}', file=sys.stderr)
    print('Install software/script/requirements.txt into the selected Python environment.', file=sys.stderr)
    sys.exit(1)

colorama.init()
os.environ['PATH'] = str(default_cwd) + os.pathsep + os.environ['PATH']
out = Path(a.output)
keys = {}
uid = None
report = {'port': a.port, 'uid': None, 'unreadable_blocks': [],
          'unreadable_errors': {},
          'dump_complete': False}

def first_block(sector):
    return sector * 4 if sector < 32 else 128 + (sector - 32) * 16

def key_type(index):
    return MfcKeyType.A if index % 2 == 0 else MfcKeyType.B

def save():
    now = time.perf_counter()
    report['total_seconds'] = round(now - started, 3)
    report['reading_seconds'] = (
        round((reading_finished if reading_finished is not None else now) - reading_started, 3)
        if reading_started is not None else None)
    report['known_keys'] = len(keys)
    report['missing_keys'] = [f'{i//2}{"A" if i%2 == 0 else "B"}' for i in range(80) if i not in keys]
    (out / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')

def check_card():
    tags = cmd.hf14a_scan()
    if not tags or len(tags) != 1:
        raise RuntimeError('Place exactly one card on the HF antenna.')
    tag = tags[0]
    if bytes(tag['sak']) != b'\x18':
        raise RuntimeError(f'Expected MFC4K SAK 18, received {bytes(tag["sak"]).hex()}.')
    current = bytes(tag['uid']).hex().upper()
    if uid is not None and current != uid:
        raise RuntimeError('Card UID changed. Put the original card back and start a new run.')
    return current

def try_keys(candidates):
    for start in range(0, len(candidates), 20):
        mask = bytearray(10)
        for index in keys:
            mask[index // 8] |= 1 << (7 - index % 8)
        result = cmd.mf1_check_keys_of_sectors(bytes(mask), candidates[start:start+20])
        if result['status'] != Status.HF_TAG_OK:
            raise RuntimeError(f'Key check failed: {result["status"]}')
        keys.update(result.get('sectorKeys', {}))
        save()
        print(f'Keys verified: {len(keys)}/80', flush=True)

com = ChameleonCom()
exit_code = 1
try:
    if not default_cwd.is_dir():
        raise RuntimeError(f'CLI tool directory is missing: {default_cwd}. Put nested.exe and staticnested.exe there.')
    com.open(a.port)
    cmd = ChameleonCMD(com)
    cmd.set_device_reader_mode(True)
    uid = check_card()
    report['uid'] = uid
    print(f'MFC4K UID: {uid}', flush=True)
    candidates = [a.key, 'FFFFFFFFFFFF', 'A0A1A2A3A4A5', 'D3F7D3F7D3F7', '000000000000']
    if a.dictionary:
        for line in Path(a.dictionary).read_text(encoding='utf-8-sig').splitlines():
            line = line.strip()
            if not line or line.startswith(('#', ';')):
                continue
            if len(line) != 12 or any(c not in '0123456789abcdefABCDEF' for c in line):
                raise ValueError(f'Invalid dictionary key: {line!r}')
            candidates.append(line)
    try_keys(list(dict.fromkeys(bytes.fromhex(k) for k in candidates)))
    if not keys:
        raise RuntimeError('No working key found. Nested requires at least one authenticated sector key.')

    if len(keys) < 80:
        level = cmd.mf1_detect_prng()
        report['prng_level'] = level
        if level == 2:
            print('Hard PRNG: ordinary nested is unavailable. Reading with the keys already found.', flush=True)
        elif level in (0, 1):
            tool = 'staticnested.exe' if level == 0 else 'nested.exe'
            if not shutil.which(tool):
                raise RuntimeError(f'{tool} not found. Put it in {default_cwd} or on PATH.')
            nested = HFMFNested()
            nested.device_com = com
            source = min(keys)
            for target in range(80):
                if target in keys:
                    continue
                check_card()
                block = first_block(target // 2)
                print(f'Recover sector {target//2} key {key_type(target).name}, block {block}', flush=True)
                for attempt in range(1, a.attempts + 1):
                    try:
                        recovered = nested.recover_a_key(first_block(source//2), key_type(source), keys[source], block, key_type(target))
                        if recovered:
                            candidate = bytes.fromhex(recovered)
                            if cmd.mf1_auth_one_key_block(block, key_type(target), candidate):
                                keys[target] = candidate
                                save()
                                try_keys([candidate])
                                break
                    except Exception as exc:
                        print(f'Attempt {attempt}: {exc}', flush=True)
                else:
                    print('Key not recovered; continuing.', flush=True)
        else:
            raise RuntimeError(f'Unsupported PRNG level: {level}')

    print('Reading 256 blocks...', flush=True)
    reading_started = time.perf_counter()
    report['preparation_and_recovery_seconds'] = round(reading_started - started, 3)
    raw = bytearray(4096)
    # Write partial dumps after every sector so an interrupted read is recoverable.
    for sector in range(40):
        check_card()
        start = first_block(sector)
        count = 4 if sector < 32 else 16
        for block in range(start, start + count):
            data = None
            errors = []
            for index in (2*sector, 2*sector+1):
                if index not in keys:
                    continue
                try:
                    data = bytes(cmd.mf1_read_one_block(block, key_type(index), keys[index]))
                    if len(data) != 16:
                        raise RuntimeError('Read returned a block other than 16 bytes.')
                    break
                except Exception as exc:
                    data = None
                    errors.append(str(exc))
            if data is None:
                report['unreadable_blocks'].append(block)
                report['unreadable_errors'][str(block)] = errors or ['No known sector key']
                continue
            offset = block * 16
            raw[offset:offset+16] = data
        (out / 'card-raw.partial.bin').write_bytes(raw)
        report['last_sector_read'] = sector
        save()
        print(f'Sectors read: {sector+1}/40; reading elapsed: {time.perf_counter()-reading_started:.1f} s', flush=True)
    reading_finished = time.perf_counter()
    report['dump_complete'] = not report['unreadable_blocks']
    if report['dump_complete']:
        (out / 'card-raw.partial.bin').rename(out / 'card-raw.bin')
    save()
    print(f'Keys: {len(keys)}/80; unreadable blocks: {len(report["unreadable_blocks"])}')
    print(f'Results: {out}')
    exit_code = 0 if report['dump_complete'] and len(keys) == 80 else 2
except KeyboardInterrupt:
    report['error'] = 'Interrupted by user'
    save()
    print('Interrupted. Completed sectors and the report are saved; keys were kept only in memory.')
    exit_code = 130
except Exception as exc:
    report['error'] = str(exc)
    save()
    traceback.print_exc()
finally:
    try:
        if com.isOpen():
            com.close()
    finally:
        save()
        print(f'Total elapsed: {report["total_seconds"]:.3f} s', flush=True)
        if report['reading_seconds'] is not None:
            print(f'Card reading elapsed: {report["reading_seconds"]:.3f} s', flush=True)
sys.exit(exit_code)
'@
[IO.File]::WriteAllText($workerPath, $worker, [Text.UTF8Encoding]::new($false))
Write-Host "Place the MFC4K card on the HF antenna. Close other apps using $Port."
Write-Host "Results: $runDirectory"
$workerArgs = @('-u', $workerPath, '--cli', $cliDirectory, '--port', $Port, '--key', $Key, '--output', $runDirectory, '--attempts', "$Attempts")
if ($Dictionary) { $workerArgs += @('--dictionary', $Dictionary) }
& $Python @workerArgs
$workerExitCode = $LASTEXITCODE
if ($workerExitCode -eq 2) {
    Write-Warning 'Incomplete recovery/read. See report.json; partial dumps contain zeros for unreadable blocks.'
} elseif ($workerExitCode -ne 0) {
    throw "Worker exited with code $workerExitCode. See the console and report.json in $runDirectory."
} else {
    Write-Host 'Done: all keys recovered and all blocks read.' -ForegroundColor Green
}
