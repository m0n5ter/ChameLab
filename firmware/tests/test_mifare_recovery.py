"""Exercise production MIFARE collection/check loops with a synthetic RF peer.

Usage: python3 firmware/tests/test_mifare_recovery.py (requires gcc).
Only the hardware-facing calls are replaced; crypto, PRNG and protocol structs
come from the firmware. This does not validate physical RF timing.
"""
from pathlib import Path
import subprocess
import tempfile


def function(source, signature):
    start = source.index(signature)
    brace = source.index("{", start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


def main():
    firmware = Path(__file__).resolve().parents[1]
    src = firmware / "application/src/rfid"
    toolbox = (src / "reader/hf/mf1_toolbox.c").read_text()
    signatures = ["static void nonce_distance(", "static bool check_lfsr_prng(",
                  "uint16_t mf1_toolbox_check_keys_of_sectors(",
                  "uint8_t mf1_static_encrypted_nonces_with_key(",
                  "uint16_t mf1_toolbox_check_keys_on_block("]
    with tempfile.TemporaryDirectory(prefix="chamelab-mifare-") as temporary:
        temp = Path(temporary)
        (temp / "rc522.h").write_text("#pragma once\n")
        (temp / "netdata.h").write_text("#pragma once\n#define PACKED __attribute__((packed))\n")
        (temp / "cmsis_gcc.h").write_text("#pragma once\n#define __REV(x) __builtin_bswap32(x)\n")
        (temp / "recovery_under_test.c").write_text("\n\n".join(function(toolbox, s) for s in signatures))
        binary = temp / "test-mifare-recovery"
        subprocess.run(["gcc", "-std=gnu11", "-Wall", "-Wextra", "-Werror", "-Wno-unused-parameter",
                        "-Wno-old-style-declaration", "-O2",
                        f"-I{temp}", f"-I{src}", f"-I{src / 'reader/hf'}",
                        str(firmware / "tests/test_mifare_recovery.c"),
                        str(src / "mf1_crapto1.c"), str(src / "parity.c"), "-o", str(binary)], check=True)
        subprocess.run([str(binary)], check=True)


if __name__ == "__main__":
    main()
