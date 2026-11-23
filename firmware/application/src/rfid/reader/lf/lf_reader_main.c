#include "lf_reader_main.h"

#include <stdbool.h>
#include <string.h>
#include "nordic_common.h"
#include "bsp_delay.h"
#include "bsp_time.h"
#include "hex_utils.h"
#include "lf_125khz_radio.h"
#include "lf_reader_data.h"
#include "protocols/em410x.h"
#include "protocols/ioprox.h"
#include "protocols/hidprox.h"
#include "protocols/idteck.h"
#include "protocols/t55xx.h"
#include "protocols/jablotron.h"
#include "protocols/pac.h"
#include "protocols/viking.h"
#include "protocols/pyramid.h"

#define NRF_LOG_MODULE_NAME lf_main
#include "nrf_log.h"
#include "nrf_log_ctrl.h"
#include "nrf_log_default_backends.h"
NRF_LOG_MODULE_REGISTER();

// The default card search is available N Millisecond timeout
static uint32_t g_timeout_readem_ms = 500;

/**
 * Search EM410X tag
 */
uint8_t scan_em410x(uint8_t *uid) {
    if (em410x_read(uid, g_timeout_readem_ms)) {
        return STATUS_LF_TAG_OK;
    }
    return STATUS_LF_TAG_NO_FOUND;
}

/**
 * Search HID Prox tag
 */
uint8_t scan_hidprox(uint8_t *data, uint8_t format_hint) {
    if (hidprox_read(data, format_hint, g_timeout_readem_ms)) {
        return STATUS_LF_TAG_OK;
    }
    return STATUS_LF_TAG_NO_FOUND;
}

/**
 * @brief Search ioProx tag
 * @param output 16 bytes ioprox_codec_t->data layout: version, facility code, card number, raw8
 * @return STATUS_LF_TAG_OK on success
 */
uint8_t scan_ioprox(uint8_t *data, uint8_t format_hint) {
    if (ioprox_read(data, format_hint, g_timeout_readem_ms)) {
        return STATUS_LF_TAG_OK;
    }
    return STATUS_LF_TAG_NO_FOUND;
}

/**
 * @brief Decode raw8 data to structured ioProx format
 * @param raw8 Input 8 bytes
 * @param output 16 bytes ioprox_codec_t->data layout: version, facility code, card number, raw8
 * @return STATUS_SUCCESS on success
 */
uint8_t decode_ioprox_raw(uint8_t *raw8, uint8_t *output) {
    if (ioprox_decode_raw_to_data(raw8, output)) {
        return STATUS_SUCCESS;
    }
    return STATUS_CMD_ERR;
}

/**
 * @brief Encode ioProx parameters to structured ioProx format
 * @param ver Version byte
 * @param fc Facility code byte
 * @param cn Card number (16-bit)
 * @param out 16 bytes ioprox_codec_t->data layout: version, facility code, card number, raw8
 * @return STATUS_SUCCESS on success
 */
uint8_t encode_ioprox_params(uint8_t ver, uint8_t fc, uint16_t cn, uint8_t *out) {
    if (ioprox_encode_params_to_data(ver, fc, cn, out)) {
        return STATUS_SUCCESS;
    }
    return STATUS_CMD_ERR;
}

/**
 * Search PAC/Stanley tag
 */
uint8_t scan_pac(uint8_t *card_id) {
    if (pac_read(card_id, g_timeout_readem_ms)) {
        return STATUS_LF_TAG_OK;
    }
    return STATUS_LF_TAG_NO_FOUND;
}

/**
 * Search Viking tag
 */
uint8_t scan_viking(uint8_t *uid) {
    if (viking_read(uid, g_timeout_readem_ms)) {
        return STATUS_LF_TAG_OK;
    }
    return STATUS_LF_TAG_NO_FOUND;
}

/**
 * Search Jablotron tag
 */
uint8_t scan_jablotron(uint8_t *uid) {
    if (jablotron_read(uid, g_timeout_readem_ms)) {
        return STATUS_LF_TAG_OK;
    }
    return STATUS_LF_TAG_NO_FOUND;
}

/**
 * Search Farpointe/Pyramid tag
 */
uint8_t scan_pyramid(uint8_t *data) {
    if (pyramid_read(data, g_timeout_readem_ms)) {
        return STATUS_LF_TAG_OK;
    }
    return STATUS_LF_TAG_NO_FOUND;
}

uint8_t scan_idteck(uint8_t *data) {
    return idteck_read(data, g_timeout_readem_ms) ? STATUS_LF_TAG_OK : STATUS_LF_TAG_NO_FOUND;
}

static bool read_hidprox_any(uint8_t *data, uint32_t timeout_ms) { return hidprox_read(data, 0, timeout_ms); }
static bool read_ioprox_any(uint8_t *data, uint32_t timeout_ms) { return ioprox_read(data, 0, timeout_ms); }

typedef struct {
    tag_specific_type_t type;  // TAG_TYPE_UNDEFINED: the reader writes the type itself (EM410x)
    uint8_t data_size;
    uint16_t window_ms;
    bool (*read)(uint8_t *data, uint32_t timeout_ms);
} lf_auto_reader_t;

// Each window covers two frames or more plus the decoder's settling time.
// Edge-timed (ASK, biphase) readers go first, they settle fastest; the
// SAADC (FSK, NRZ) readers each run a demodulator per sample, too heavy to
// run side by side at 125 kS/s, so every protocol gets a window of its own.
// Their frame checks (EM410x parity, checksums, CRC, Manchester validity)
// keep one protocol's reader from decoding another protocol's tag.
static const lf_auto_reader_t lf_auto_readers[] = {
    { TAG_TYPE_UNDEFINED,          2 + 13, 150, em410x_read      },  // + Electra wait once a frame is in
    { TAG_TYPE_VIKING,             4,      100, viking_read      },
    { TAG_TYPE_JABLOTRON,          5,      120, jablotron_read   },
    { TAG_TYPE_HID_PROX,           16,     200, read_hidprox_any },
    { TAG_TYPE_IOPROX,             16,     200, read_ioprox_any  },  // + 60 ms power cycle in the reader
    { TAG_TYPE_FARPOINTE_PYRAMID,  16,     200, pyramid_read     },  // + 60 ms power cycle in the reader
    { TAG_TYPE_PAC,                8,      160, pac_read         },  // + 10 ms power-up in the reader
    { TAG_TYPE_IDTECK,             8,      160, idteck_read      },  // two matching RF/32 PSK1 frames
};

/**
 * Search for a tag of any protocol the reader decodes, trying each in turn.
 * @param out tag type (2 bytes, big endian) followed by that protocol's scan data
 * @param out_len bytes written to out
 */
uint8_t scan_lf_auto(uint8_t *out, uint16_t *out_len) {
    *out_len = 0;
    for (size_t i = 0; i < ARRAY_SIZE(lf_auto_readers); i++) {
        const lf_auto_reader_t *r = &lf_auto_readers[i];
        if (r->type == TAG_TYPE_UNDEFINED) {
            uint8_t em[2 + 13] = {0};
            if (!r->read(em, r->window_ms)) continue;
            tag_specific_type_t type = (em[0] << 8) | em[1];
            *out_len = 2 + (type == TAG_TYPE_EM410X_ELECTRA ? 13 : 5);
            memcpy(out, em, *out_len);
            return STATUS_LF_TAG_OK;
        }
        uint8_t data[16] = {0};
        if (!r->read(data, r->window_ms)) continue;
        out[0] = r->type >> 8;
        out[1] = r->type;
        memcpy(out + 2, data, r->data_size);
        *out_len = 2 + r->data_size;
        return STATUS_LF_TAG_OK;
    }
    return STATUS_LF_TAG_NO_FOUND;
}

/**
 * Try reset t55XX tag passwords by enumerating old passwords.
 */
static void try_reset_t55xx_passwd(uint32_t new_passwd, uint8_t *old_passwds, uint8_t old_passwd_count) {
    for (uint8_t i = 0; i < old_passwd_count; i++) {
        uint32_t old_passwd = bytes_to_num(old_passwds + i * 4, 4);
        t55xx_reset_passwd(old_passwd, new_passwd);
    }
    t55xx_reset_passwd(new_passwd, new_passwd);
}

/**
 * Write card data to t55xx
 */
static uint8_t write_t55xx(uint32_t *blks, uint8_t blk_count, uint8_t *new_passwd, uint8_t *old_passwds, uint8_t old_passwd_count) {
    uint32_t passwd = bytes_to_num(new_passwd, 4);

    start_lf_125khz_radio();
    bsp_delay_ms(1);  // Delays for a while after starting the field

    try_reset_t55xx_passwd(passwd, old_passwds, old_passwd_count);
    t55xx_write_data(passwd, blks, blk_count);

    stop_lf_125khz_radio();

    // writing results should be verified by upper computer
    return STATUS_LF_TAG_OK;
}

/**
 * Write em410x card data to t55xx
 */
uint8_t write_em410x_to_t55xx(uint8_t *uid, uint8_t *new_passwd, uint8_t *old_passwds, uint8_t old_passwd_count) {
    uint32_t blks[7] = {0x00};
    uint8_t blk_count = em410x_t55xx_writer(uid, blks);
    if (blk_count == 0) {
        return STATUS_PAR_ERR;
    }
    return write_t55xx(blks, blk_count, new_passwd, old_passwds, old_passwd_count);
}

uint8_t write_em410x_electra_to_t55xx(uint8_t *uid, uint8_t *new_passwd, uint8_t *old_passwds, uint8_t old_passwd_count) {
    uint32_t blks[7] = {0x00};
    uint8_t blk_count = em410x_electra_t55xx_writer(uid, blks);
    if (blk_count == 0) {
        return STATUS_PAR_ERR;
    }
    return write_t55xx(blks, blk_count, new_passwd, old_passwds, old_passwd_count);
}

/**
 * Write hidprox card data to t55xx
 */
uint8_t write_hidprox_to_t55xx(uint8_t format, uint32_t fc, uint64_t cn, uint32_t il, uint32_t oem, uint8_t *new_passwd, uint8_t *old_passwds, uint8_t old_passwd_count) {
    wiegand_card_t card = {
        .format = format,
        .card_number = cn,
        .facility_code = fc,
        .issue_level = il,
        .oem = oem,
    };
    uint32_t blks[7] = {0x00};
    uint8_t blk_count = hidprox_t55xx_writer(&card, blks);
    if (blk_count == 0) {
        return STATUS_PAR_ERR;
    }
    return write_t55xx(blks, blk_count, new_passwd, old_passwds, old_passwd_count);
}

/**
 * Write ioprox card data to t55xx
 */
uint8_t write_ioprox_to_t55xx(uint8_t *card_data, uint8_t *new_passwd, uint8_t *old_passwds, uint8_t old_passwd_count) {
    // Prepare T5577 block array: index 0 = config word, 1-2 = data blocks
    uint32_t blks[3] = {0x00};

    uint8_t blk_count = ioprox_t55xx_writer(card_data, blks);

    if (blk_count == 0) {
        return STATUS_PAR_ERR;
    }

    return write_t55xx(blks, blk_count, new_passwd, old_passwds, old_passwd_count);
}

/**
 * Write viking card data to t55xx
 */
uint8_t write_viking_to_t55xx(uint8_t *uid, uint8_t *new_passwd, uint8_t *old_passwds, uint8_t old_passwd_count) {
    uint32_t blks[7] = {0x00};
    uint8_t blk_count = viking_t55xx_writer(uid, blks);
    if (blk_count == 0) {
        return STATUS_PAR_ERR;
    }
    return write_t55xx(blks, blk_count, new_passwd, old_passwds, old_passwd_count);
}

uint8_t write_pac_to_t55xx(uint8_t *data, uint8_t *new_passwd, uint8_t *old_passwds, uint8_t old_passwd_count) {
    uint32_t blks[7] = {0x00};
    uint8_t blk_count = pac_t55xx_writer(data, blks);
    if (blk_count == 0) return STATUS_PAR_ERR;
    return write_t55xx(blks, blk_count, new_passwd, old_passwds, old_passwd_count);
}

/**
 * Write jablotron card data to t55xx
 */
uint8_t write_jablotron_to_t55xx(uint8_t *uid, uint8_t *new_passwd, uint8_t *old_passwds, uint8_t old_passwd_count) {
    uint32_t blks[7] = {0x00};
    uint8_t blk_count = jablotron_t55xx_writer(uid, blks);
    if (blk_count == 0) {
        return STATUS_PAR_ERR;
    }
    return write_t55xx(blks, blk_count, new_passwd, old_passwds, old_passwd_count);
}

/**
 * Write IDTECK card data to t55xx (PSK1 RF/32, 64-bit frame).
 */
uint8_t write_idteck_to_t55xx(uint8_t *data, uint8_t *new_passwd, uint8_t *old_passwds, uint8_t old_passwd_count) {
    uint32_t blks[7] = {0x00};
    uint8_t blk_count = idteck_t55xx_writer(data, blks);
    if (blk_count == 0) return STATUS_PAR_ERR;
    return write_t55xx(blks, blk_count, new_passwd, old_passwds, old_passwd_count);
}

/**
 * Set the LF card scanning timeout value (in milliseconds).
 */
void set_scan_tag_timeout(uint32_t ms) { g_timeout_readem_ms = ms; }

#if defined(PROJECT_CHAMELEON_ULTRA)
/**
 * Write a single raw 32-bit word to a T55xx block.
 *
 * Unlike write_em410x_to_t55xx() and friends, this writes the exact word
 * supplied with no protocol encoding — useful for custom configuration
 * words, recovery of locked tags, or scripted programming.
 *
 * Only available on Chameleon Ultra (Lite has no LF writer hardware).
 *
 * @param block      Block number (0-7 for page 0, 0-3 for page 1)
 * @param word       32-bit data word to write
 * @param passwd     Password for password-protected write (ignored when use_passwd is false)
 * @param use_passwd true = password-protected write, false = open write
 * @param page1      true = target page 1, false = page 0
 * @return           STATUS_LF_TAG_OK always (T55xx gives no ACK; verify by reading back)
 */
uint8_t lf_t55xx_write_block(uint8_t block, uint32_t word, uint32_t passwd, bool use_passwd, bool page1) {
    uint8_t opcode = page1 ? T5577_OPCODE_PAGE1 : T5577_OPCODE_PAGE0;
    uint32_t *pwd_ptr = use_passwd ? &passwd : NULL;

    start_lf_125khz_radio();
    // Each raw block write starts with the field off. BLE (or a slow USB host)
    // leaves enough time between commands for the tag to discharge; 1 ms is
    // insufficient to power it up reliably before the first command gap.
    bsp_delay_ms(10);

    t55xx_send_cmd(opcode, pwd_ptr, 0, &word, block);
    t55xx_send_cmd(T5577_OPCODE_RESET, NULL, 0, NULL, 0);

    stop_lf_125khz_radio();
    return STATUS_LF_TAG_OK;
}
#endif
