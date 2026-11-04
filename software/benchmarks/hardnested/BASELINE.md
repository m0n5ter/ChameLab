# Managed hardnested vs Proxmark3 - 2026-11-04

Eight deterministic captures; **48/48 recoveries correct**. i9-13900H, Ubuntu/WSL2;
.NET 10 Release vs Proxmark3 `1b71dfb` (GCC 13.3, AVX2).

**Method:** identical nonce/parity data generated before timing; three fresh-process
runs per solver, sequentially with alternating order. Medians include input, startup,
table loading and the native startup benchmark; file caches are not flushed.

| Case | Nonces | C# (s) | C (s) | Speedup | Peak MiB (C# / C) |
| --- | ---: | ---: | ---: | ---: | ---: |
| reference | 12,000 | 5.35 | 10.17 | 1.90x | 117 / 1,738 |
| sparse | 4,096 | 4.23 | 10.14 | 2.40x | 104 / 1,785 |
| ones-sparse | 4,096 | 26.04 | 71.22 | 2.74x | 197 / 1,715 |
| zero | 12,000 | 2.17 | 10.78 | 4.97x | 99 / 1,714 |
| transport | 12,000 | 4.71 | 11.02 | 2.34x | 107 / 1,714 |
| mixed | 12,000 | 5.87 | 11.58 | 1.97x | 121 / 1,734 |
| new-seed | 8,192 | 5.39 | 10.96 | 2.03x | 116 / 1,738 |
| ones-dense | 24,000 | 29.88 | 56.54 | 1.89x | 216 / 1,715 |

Synthetic captures on this host only. [Cases](CASES.json) |
[Raw runs](RESULTS.json) | [Reproduce](README.md).
