"""Run the actual firmware LF reader/codecs on a host compiler, without Nordic hardware.

Usage: python3 firmware/tests/test_lf_em410x_read.py (requires gcc).
"""
from pathlib import Path
import subprocess
import tempfile


def main():
    firmware = Path(__file__).resolve().parents[1]
    src = firmware / "application/src"
    stubs = {
        "nrf_pwm.h": """
#pragma once
#include <stdint.h>
typedef struct { uint16_t channel_0, channel_1, channel_2, counter_top; } nrf_pwm_values_wave_form_t;
typedef struct { union { const nrf_pwm_values_wave_form_t *p_wave_form; } values; uint16_t length; uint32_t repeats, end_delay; } nrf_pwm_sequence_t;
#define NRF_PWM_VALUES_LENGTH(a) (sizeof(a) / (sizeof(uint16_t)))
""",
        "nordic_common.h": "#pragma once\n#define ARRAY_SIZE(a) (sizeof(a) / sizeof((a)[0]))\n#define IS_SET(x, b) (((x) >> (b)) & 1)\n",
        "nrf_log.h": "#pragma once\n#define NRF_LOG_MODULE_REGISTER()\n",
        "nrf_log_ctrl.h": "#pragma once\n",
        "nrf_log_default_backends.h": "#pragma once\n",
        "bsp_delay.h": "#pragma once\n#include <stdint.h>\nvoid bsp_delay_ms(uint16_t);\nvoid bsp_delay_us(uint32_t);\n",
        "nrf_gpio.h": "#pragma once\n",
        "lf_125khz_radio.h": """
#pragma once
void start_lf_125khz_radio(void);
void stop_lf_125khz_radio(void);
void lf_125khz_radio_gpiote_enable(void);
void lf_125khz_radio_gpiote_disable(void);
""",
    }
    with tempfile.TemporaryDirectory(prefix="chamelab-em410x-") as temp:
        temp = Path(temp)
        for name, contents in stubs.items():
            (temp / name).write_text(contents)
        # Copy the reader verbatim so its quoted RF-header include resolves to
        # the host stub instead of the Nordic header beside the original file.
        reader = temp / "lf_em410x_data.c"
        reader.write_bytes((src / "rfid/reader/lf/lf_em410x_data.c").read_bytes())
        binary = temp / "test-em410x"
        includes = [temp, src / "bsp", src / "rfid", src / "rfid/nfctag",
                    src / "rfid/nfctag/lf", src / "rfid/nfctag/lf/utils", src / "rfid/reader/lf", src / "utils"]
        sources = [firmware / "tests/test_lf_em410x_read.c", reader,
                   src / "rfid/nfctag/lf/protocols/em410x.c", src / "rfid/nfctag/lf/utils/manchester.c",
                   src / "rfid/parity.c"]
        subprocess.run(["gcc", "-std=gnu11", "-Wall", "-Wextra", "-Werror", "-Wno-unused-parameter",
                        "-O2", *[f"-I{p}" for p in includes], *map(str, sources), "-o", str(binary)], check=True)
        subprocess.run([str(binary)], check=True)
        writer = temp / "lf_t55xx_data.c"
        writer.write_bytes((src / "rfid/reader/lf/lf_t55xx_data.c").read_bytes())
        subprocess.run(["gcc", "-std=gnu11", "-Wall", "-Wextra", "-Werror", "-O2",
                        "-DPROJECT_CHAMELEON_ULTRA", *[f"-I{p}" for p in includes],
                        str(firmware / "tests/test_lf_t55xx_write.c"), str(writer), "-o", str(binary)], check=True)
        subprocess.run([str(binary)], check=True)


if __name__ == "__main__":
    main()
