"""Compare fresh managed/native processes on identical, pregenerated captures."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import statistics
import struct
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("managed", type=Path)
parser.add_argument("native", type=Path)
parser.add_argument("output", type=Path)
parser.add_argument("repetitions", type=int, nargs="?", default=3)
parser.add_argument("--cases", type=Path, default=Path(__file__).with_name("CASES.json"))
parser.add_argument("--only", nargs="+", help="Case IDs to run (default: all)")
args = parser.parse_args()
if args.repetitions < 1:
    parser.error("repetitions must be positive")
cases = json.loads(args.cases.read_text())
if args.only:
    missing = set(args.only) - {case["id"] for case in cases}
    if missing:
        parser.error(f"unknown cases: {sorted(missing)}")
    cases = [case for case in cases if case["id"] in args.only]
managed, native = str(args.managed.resolve()), str(args.native.resolve())
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=True)
environment = {**os.environ, "LC_ALL": "C"}
results = []
fixtures = []
for case in cases:
    count, key, uid, seed = (case[field] for field in ["samples", "key", "uid", "seed"])
    if count <= 0 or count % 2:
        raise ValueError("Proxmark3 paired-record format requires a positive even sample count")
    capture = output / (case["id"] + ".txt")
    binary = capture.with_suffix(".bin")
    subprocess.run([managed, str(count), key, str(capture), uid, seed, "--generate-only"],
                   check=True, env=environment, timeout=60)
    samples = [tuple(map(int, line.split("|"))) for line in capture.read_text().splitlines()]
    if len(samples) != count or len({nonce >> 24 for nonce, _ in samples}) != 256:
        raise ValueError(f"Incomplete capture: {case['id']}")
    # Independently generated C fixture guards the generator and wire format.
    if case["id"] == "reference":
        reference = Path(__file__).parents[2] / "chamelab/ChameLab.Tests/Reference/hardnested_capture.txt"
        if capture.read_text().splitlines() != reference.read_text().splitlines():
            raise ValueError("Reference capture differs from the independent C fixture")
    with binary.open("wb") as stream:
        stream.write(struct.pack(">IBB", int(uid, 16), 3, 0))
        for a, b in zip(samples[::2], samples[1::2]):
            stream.write(struct.pack(">IIB", a[0], b[0], a[1] * 16 + b[1]))
    fixtures.append({**case, "capture_sha256": hashlib.sha256(binary.read_bytes()).hexdigest()})
(output / "captures.json").write_text(json.dumps(fixtures, indent=2) + "\n")

for case_index, case in enumerate(fixtures):
    capture = output / (case["id"] + ".txt")
    binary = capture.with_suffix(".bin")
    for iteration in range(args.repetitions):
        engines = ["managed", "native"] if (case_index + iteration) % 2 == 0 else ["native", "managed"]
        for order, engine in enumerate(engines):
            name = f"{case['id']}-{iteration}-{engine}"
            command = ([managed, "--capture", str(capture), case["uid"], case["key"]]
                       if engine == "managed" else
                       [native, "-c", f"hf mf hardnested -r -f {binary}"])
            measured = subprocess.run(["/usr/bin/time", "-f", "BENCH elapsed=%e peak_kib=%M", *command],
                                      stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                      timeout=1200, env=environment)
            (output / (name + ".log")).write_text(measured.stdout)
            pattern = (r"Key=" if engine == "managed" else r"Key found:\s*") + case["key"] + r"\b"
            if measured.returncode != 0 or not re.search(pattern, measured.stdout, re.IGNORECASE):
                raise RuntimeError(f"Failed recovery: {name}; see log")
            match = re.search(r"BENCH elapsed=([\d.]+) peak_kib=(\d+)", measured.stdout)
            if not match:
                raise RuntimeError(f"Missing process measurements: {name}")
            row = {**case, "engine": engine, "iteration": iteration, "order": order,
                   "seconds": float(match[1]), "peak_mib": int(match[2]) / 1024}
            results.append(row)
            print(json.dumps(row), flush=True)
            (output / "results.json").write_text(json.dumps(results, indent=2) + "\n")
print("Medians:")
for case in fixtures:
    values = {engine: statistics.median(r["seconds"] for r in results
              if r["id"] == case["id"] and r["engine"] == engine) for engine in ["managed", "native"]}
    print(case["id"], values, "speedup", values["native"] / values["managed"])
