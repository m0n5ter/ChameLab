"""Enable offline file input in the pinned Proxmark3 CLI, leaving the solver intact.

Usage: python prepare_reference.py /path/to/proxmark3
Only use a disposable benchmark checkout: this edits client/src/cmdhfmf.c.
"""
from pathlib import Path
import subprocess
import sys

root = Path(sys.argv[1]).resolve()
expected = "1b71dfbbd4c156c34c02cfdd9140470d97d1336c"
assert subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip() == expected
path = root / "client/src/cmdhfmf.c"
text = path.read_text()
for before, after in [
    ("if ((g_session.pm3_present == false) && (tests == false)) {",
     "if ((g_session.pm3_present == false) && (tests == false) && !nonce_file_read) {"),
    ("if (nonce_file_read) {\n        char *fptr = GenerateFilename",
     "if (nonce_file_read && fnlen == 0) {\n        char *fptr = GenerateFilename")]:
    if after in text:
        continue
    assert text.count(before) == 1, before
    text = text.replace(before, after)
path.write_text(text)
print("Patched only CLI device check and explicit filename preservation; rebuild client.")
