"""Repack the pinned Proxmark3 tables without changing any state bits.

Development tool only: pip install lz4. No Python or LZ4 runtime dependency.
Usage: python import_tables.py PATH_TO_PROXMARK3
"""
from pathlib import Path
import hashlib
import json
import struct
import sys
import lz4.frame

COMMIT = "1b71dfbbd4c156c34c02cfdd9140470d97d1336c"
source = Path(sys.argv[1]) / "client/resources/hardnested_tables"
destination = Path(__file__).resolve().parents[2] / "ChameLab/Crypto/Hardnested"
destination.mkdir(parents=True, exist_ok=True)
pages = {bytes(256): 0}
tables = []
manifest = []
for path in sorted(source.glob("*.bin.lz4")):
    compressed = path.read_bytes()
    raw = lz4.frame.decompress(compressed)
    assert len(raw) == 4 + (1 << 21), path
    count, = struct.unpack_from("<I", raw)
    manifest.append(dict(file=path.name, sha256=hashlib.sha256(compressed).hexdigest(),
                         raw_sha256=hashlib.sha256(raw).hexdigest(), count=count))
    if count >= .9901 * (1 << 24):  # same threshold as Proxmark3
        continue
    _, half, flip, _ = path.name.split("_")
    ids = []
    for offset in range(4, len(raw), 256):
        page = raw[offset:offset + 256]
        ids.append(pages.setdefault(page, len(pages)))
    tables.append((int(half), int(flip, 16), count, ids))
assert len(manifest) == 351 and len(tables) == 241
assert len(pages) < 65536
with (destination / "tables.bin").open("wb") as out:
    out.write(struct.pack("<4sII", b"HNT1", len(pages), len(tables)))
    for page in pages:
        out.write(page)
    for half, flip, count, ids in tables:
        out.write(struct.pack("<HHI", half, flip, count))
        out.write(struct.pack("<8192H", *ids))
(destination / "tables.manifest.json").write_text(json.dumps(dict(
    repository="https://github.com/RfidResearchGroup/proxmark3", commit=COMMIT,
    packed_sha256=hashlib.sha256((destination / "tables.bin").read_bytes()).hexdigest(),
    tables=manifest), indent=2) + "\n")
print(f"{len(tables)} tables, {len(pages)} shared pages, {(destination / 'tables.bin').stat().st_size} bytes")
