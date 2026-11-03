"""Generate identical scalar and portable SIMD circuits. No runtime code generation."""
from pathlib import Path

root = Path(__file__).resolve().parents[2] / "ChameLab/Crypto/Hardnested"
template = (Path(__file__).parent / "core.cs.txt").read_text()
for name, bits, lanes, zero, ones, load, lane in [
    ("Scalar", "System.UInt64", "1", "0UL", "ulong.MaxValue", "packed[0]", "results"),
    ("Vector", "System.Numerics.Vector<ulong>", "Vector<ulong>.Count", "Vector<ulong>.Zero",
     "new Vector<ulong>(ulong.MaxValue)", "new Vector<ulong>(packed)", "results[lane]")]:
    text = template
    for key, value in dict(NAME=name, BITS=bits, LANES=lanes, ZERO=zero, ONES=ones, LOAD=load, LANE=lane).items():
        text = text.replace("@" + key + "@", value)
    (root / f"Hardnested{name}.g.cs").write_text(text)
