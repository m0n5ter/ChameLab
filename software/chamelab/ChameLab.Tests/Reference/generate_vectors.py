"""Refresh independent C fixtures using previously compiled reference executables."""
import argparse
import json
from pathlib import Path
import re
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("--generator", required=True, type=Path)
parser.add_argument("--nested", required=True, type=Path)
parser.add_argument("--staticnested", required=True, type=Path)
parser.add_argument("--mfkey32v2", required=True, type=Path)
parser.add_argument("--output", type=Path, default=Path(__file__).with_name("crypto_vectors.json"))
args = parser.parse_args()


def run(executable, values=()):
    return subprocess.check_output([str(executable.resolve()), *map(str, values)], text=True)


vectors = [json.loads(line) for line in run(args.generator).splitlines()]
for vector in vectors:
    kind = vector["kind"]
    if kind == "nested":
        values = [vector[k] for k in ("uid", "distance", "nt0", "enc0", "par0", "nt1", "enc1", "par1")]
        output = run(args.nested, values)
        vector["cKeys"] = re.findall(r"\.\.\.\s*([a-fA-F0-9]{12})", output)
        assert vector["key"].lower() in [key.lower() for key in vector["cKeys"]]
    elif kind == "mfkey":
        values = [f"{vector[k]:x}" for k in ("uid", "nt0", "nr0", "ar0", "nt1", "nr1", "ar1")]
        assert f"[{vector['key']}]".lower() in run(args.mfkey32v2, values).lower()
    elif kind == "static":
        values = [vector[k] for k in ("uid", "type", "nonce", "enc0", "nonce", "enc1")]
        assert vector["key"].lower() in run(args.staticnested, values).lower()

args.output.write_text(json.dumps(vectors, indent=2) + "\n", encoding="utf-8")
print(f"Wrote {len(vectors)} C reference vectors to {args.output}")
