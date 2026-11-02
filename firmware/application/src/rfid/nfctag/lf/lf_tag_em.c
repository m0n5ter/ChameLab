#include "lf_tag_em.h"

#include <stdint.h>
#include <string.h>

#include "bsp_delay.h"
#include "fds_util.h"
#include "nrf_gpio.h"
#include "nrf_soc.h"
#include "nrfx_lpcomp.h"
#include "nrfx_pwm.h"
#include "protocols/em410x.h"
#include "protocols/hidprox.h"
#include "protocols/idteck.h"
#include "protocols/ioprox.h"
#include "protocols/jablotron.h"
#include "protocols/pac.h"
#include "protocols/pyramid.h"
#include "protocols/viking.h"
#include "syssleep.h"
#include "settings.h"
#include "tag_emulation.h"
#include "tag_persistence.h"

#define NRF_LOG_MODULE_NAME tag_em410x
#include "nrf_log.h"
#include "nrf_log_ctrl.h"
#include "nrf_log_default_backends.h"
NRF_LOG_MODULE_REGISTER();

#define ANT_NO_MOD() nrf_gpio_pin_clear(LF_MOD)

// Whether the USB light effect is allowed to enable
extern bool g_usb_led_marquee_enable;

// Whether it is currently in the low -frequency card number of broadcasting
static volatile bool m_is_lf_emulating = false;
// Cache tag type
static tag_specific_type_t m_tag_type = TAG_TYPE_UNDEFINED;

// The pwm to broadcast modulated card id
const nrfx_pwm_t m_broadcast = NRFX_PWM_INSTANCE(0);
const nrf_pwm_sequence_t *m_pwm_seq = NULL;

typedef struct {
    tag_specific_type_t type;
    const protocol *proto;
    uint16_t id_size;
} lf_tag_protocol_t;

// The emulated LF types. Their modulators don't use a codec, so they are called with NULL.
static const lf_tag_protocol_t m_lf_protocols[] = {
    {TAG_TYPE_EM410X,            &em410x_64,      LF_EM410X_TAG_ID_SIZE},
    {TAG_TYPE_EM410X_ELECTRA,    &em410x_electra, LF_EM410X_ELECTRA_TAG_ID_SIZE},
    {TAG_TYPE_HID_PROX,          &hidprox,        LF_HIDPROX_TAG_ID_SIZE},
    {TAG_TYPE_IOPROX,            &ioprox,         LF_IOPROX_TAG_ID_SIZE},
    {TAG_TYPE_VIKING,            &viking,         LF_VIKING_TAG_ID_SIZE},
    {TAG_TYPE_PAC,               &pac,            LF_PAC_TAG_ID_SIZE},
    {TAG_TYPE_JABLOTRON,         &jablotron,      LF_JABLOTRON_TAG_ID_SIZE},
    {TAG_TYPE_IDTECK,            &idteck,         LF_IDTECK_TAG_ID_SIZE},
    {TAG_TYPE_FARPOINTE_PYRAMID, &pyramid,        LF_PYRAMID_TAG_ID_SIZE},
};

static const lf_tag_protocol_t *lf_protocol(tag_specific_type_t type) {
    for (uint8_t i = 0; i < ARRAY_SIZE(m_lf_protocols); i++) {
        if (m_lf_protocols[i].type == type) return &m_lf_protocols[i];
    }
    return NULL;
}

uint16_t lf_tag_id_size(tag_specific_type_t type) {
    const lf_tag_protocol_t *p = lf_protocol(type);
    return p ? p->id_size : 0;
}

// The loaded LF buffer while it holds a cycle; the interrupts read the cards from it.
static const uint8_t *m_cycle_record = NULL;
static uint8_t m_cycle_count = 1, m_cycle_index = 0;

// Cycle position persists across sleep in retention register GPREGRET(0): the LF field wakes
// the chip from System OFF through a reset, so RAM cannot carry it. Bits 1-3 hold the slot the
// position belongs to, so switching slots restarts at the first card; bits 4-6 hold the next
// card index. Bit 0 is the bootloader's DFU-enter flag and must stay clear. Power-on reset
// clears the register, so a battery pull also restarts at the first card.
#define LF_POS_SLOT_Pos 1
#define LF_POS_SLOT_Msk (7UL << LF_POS_SLOT_Pos)
#define LF_POS_INDEX_Pos 4
#define LF_POS_INDEX_Msk (7UL << LF_POS_INDEX_Pos)

static void lf_cycle_pos_get(uint8_t *slot, uint8_t *index) {
    uint32_t v = 0;
    sd_power_gpregret_get(0, &v);
    *slot = (v & LF_POS_SLOT_Msk) >> LF_POS_SLOT_Pos;
    *index = (v & LF_POS_INDEX_Msk) >> LF_POS_INDEX_Pos;
}

static void lf_cycle_pos_set(uint8_t slot, uint8_t index) {
    sd_power_gpregret_clr(0, LF_POS_SLOT_Msk | LF_POS_INDEX_Msk);
    sd_power_gpregret_set(0, (((uint32_t)slot << LF_POS_SLOT_Pos) & LF_POS_SLOT_Msk) |
                                 (((uint32_t)index << LF_POS_INDEX_Pos) & LF_POS_INDEX_Msk));
}

static tag_specific_type_t lf_cycle_entry_type(const uint8_t *data, uint8_t index) {
    const uint8_t *entry = data + LF_CYCLE_EXTRAS + (index - 1) * LF_CYCLE_ENTRY_SIZE;
    return (tag_specific_type_t)((entry[0] << 8) | entry[1]);
}

/**
 * @brief Makes card `index` of the cycle the one to send: builds its waveform and sets its PWM base clock.
 * Waveforms are built in their protocol's buffer, shared by the cards of that protocol, so call
 * only while PWM is stopped (EasyDMA no longer reads the previous card's waveform).
 */
static void cycle_select(uint8_t index) {
    tag_specific_type_t type = index == 0 ? m_tag_type : lf_cycle_entry_type(m_cycle_record, index);
    const uint8_t *data = index == 0 ? m_cycle_record : m_cycle_record + LF_CYCLE_EXTRAS + (index - 1) * LF_CYCLE_ENTRY_SIZE + 2;
    m_cycle_index = index;
    m_pwm_seq = lf_protocol(type)->proto->modulator(NULL, (uint8_t *)data);
    // PSK1 cards need the 1 MHz base clock, the others 125 kHz (see pwm_init).
    m_broadcast.p_registers->PRESCALER = IS_PSK1_TYPE(type) ? NRF_PWM_CLK_1MHz : NRF_PWM_CLK_125kHz;
}

static void lf_field_lost(void) {
    // Open the incident interruption, so that the next event can be in and out normally
    g_is_tag_emulating = false;  // Reset the flag in the emulation
    m_is_lf_emulating = false;
    TAG_FIELD_LED_OFF()  // Make sure the indicator light of the LF field status
    // Re-arm LPCOMP so the next field appearance triggers lpcomp_event_handler.
    NRF_LPCOMP->INTENSET = LPCOMP_INTENSET_UP_Msk;
    // call sleep_timer_start *after* unsetting g_is_tag_emulating
    sleep_timer_start(settings_get_sleep_timeout());  // Start the timer to enter the sleep
    NRF_LOG_INFO("LF FIELD LOST");
}

/**
 * @brief Judge field status
 */
bool is_lf_field_exists(void) {
    nrfx_lpcomp_enable();
    bsp_delay_us(30);  // Display for a period of time and sampling to avoid misjudgment
    nrf_lpcomp_task_trigger(NRF_LPCOMP_TASK_SAMPLE);
    return nrf_lpcomp_result_get() == 1;  // Determine the sampling results of the LF field status
}

/**
 * @brief LPCOMP event handler is called when LPCOMP detects voltage drop.
 *
 * This function is called from interrupt context so it is very important
 * to return quickly. Don't put busy loops or any other CPU intensive actions here.
 * It is also not allowed to call soft device functions from it (if LPCOMP IRQ
 * priority is set to APP_IRQ_PRIORITY_HIGH).
 */
static void lpcomp_event_handler(nrf_lpcomp_event_t event) {
    // Only when the lf-frequency emulation is not launched, and the analog card is started
    if (m_is_lf_emulating || event != NRF_LPCOMP_EVENT_UP) {
        return;
    }

    sleep_timer_stop();  // turn off dormant delay
    // Disable LPCOMP during emulation — LF_RSSI fluctuates during load
    // modulation and would trigger spurious DOWN events with DETECT_CROSS.
    // Field-loss is checked periodically via EVT_END_SEQ0 in pwm_handler.
    nrfx_lpcomp_disable();

    // set the emulation status logo bit
    m_is_lf_emulating = true;
    g_is_tag_emulating = true;
    // turn off USB light effect when emulating cards
    g_usb_led_marquee_enable = false;

    // LED status update
    set_slot_light_color(RGB_BLUE);
    TAG_FIELD_LED_ON()

    // Each field appearance emulates the next card of the cycle, so a reader reads one steady
    // card per presentation (the cleanest "card present" transition) and advancing is a fresh
    // tap. The position is kept in GPREGRET across the reset that an LF-field wake causes.
    if (m_cycle_count > 1) {
        uint8_t slot = tag_emulation_get_slot(), index;
        uint8_t stored_slot;
        lf_cycle_pos_get(&stored_slot, &index);
        if (stored_slot != slot || index >= m_cycle_count) index = 0;
        cycle_select(index);
        lf_cycle_pos_set(slot, (index + 1) % m_cycle_count);
    } else {
        // Single card: m_pwm_seq is already the slot's own card. Still record the slot, so a
        // later return to a cycle slot restarts at its first card.
        lf_cycle_pos_set(tag_emulation_get_slot(), 0);
    }
    // Play a finite burst then stop — field check happens in EVT_STOPPED after
    // PWM has fully released LF_MOD, so ANT_NO_MOD() and the settle delay are
    // effective. NRFX_PWM_FLAG_LOOP kept the pin owned by the peripheral,
    // making the field check always read "present" due to self-drive on LF_RSSI.
    nrfx_pwm_simple_playback(&m_broadcast, m_pwm_seq, 10, NRFX_PWM_FLAG_STOP);

    NRF_LOG_INFO("LF FIELD DETECTED");
}

static void lpcomp_init(void) {
    nrfx_lpcomp_config_t cfg = NRFX_LPCOMP_DEFAULT_CONFIG;
    cfg.input = LF_RSSI;
    cfg.hal.reference = NRF_LPCOMP_REF_SUPPLY_1_16;
    cfg.hal.detection = NRF_LPCOMP_DETECT_UP;
    cfg.hal.hyst = NRF_LPCOMP_HYST_50mV;

    ret_code_t err_code = nrfx_lpcomp_init(&cfg, lpcomp_event_handler);
    APP_ERROR_CHECK(err_code);
}

static void pwm_handler(nrfx_pwm_evt_type_t event_type) {
    if (event_type != NRFX_PWM_EVT_STOPPED) {
        return;
    }
    // PWM has fully stopped — LF_MOD is released back to GPIO.
    // Now ANT_NO_MOD() and the settle delay are effective.
    ANT_NO_MOD();
    bsp_delay_ms(2);  // let peak detector drain: ~2 ms time constant on LF_RSSI
    if (is_lf_field_exists()) {
        // Field still present — replay the same card (the one chosen at field onset) and
        // check again. Advancing to the next card happens on the next field appearance.
        nrfx_pwm_simple_playback(&m_broadcast, m_pwm_seq, 10, NRFX_PWM_FLAG_STOP);
    } else {
        // Field gone — clean up.
        lf_field_lost();
    }
}

static void pwm_init(void) {
    nrfx_pwm_config_t cfg = NRFX_PWM_DEFAULT_CONFIG;
    cfg.output_pins[0] = LF_MOD;
    for (uint8_t i = 1; i < NRF_PWM_CHANNEL_COUNT; i++) {
        cfg.output_pins[i] = NRFX_PWM_PIN_NOT_USED;
    }
    cfg.irq_priority = APP_IRQ_PRIORITY_LOW;
    // Base clock depends on the currently-loaded tag type. Legacy ASK/FSK
    // protocols (EM410x, HID, ioProx, Viking, PAC) use 125kHz base so that
    // their hardcoded counter_top values (8-64 range) produce the correct
    // absolute timing. PSK1 protocols need finer resolution for the 16us
    // subcarrier period, so pwm_init uses 1MHz base with counter_top=16.
    // See tag_base_type.h IS_PSK1_TYPE for the list of qualifying types.
    cfg.base_clock = IS_PSK1_TYPE(m_tag_type) ? NRF_PWM_CLK_1MHz : NRF_PWM_CLK_125kHz;
    cfg.count_mode = NRF_PWM_MODE_UP;
    cfg.load_mode = NRF_PWM_LOAD_WAVE_FORM;
    cfg.step_mode = NRF_PWM_STEP_AUTO;

    nrfx_err_t err_code = nrfx_pwm_init(&m_broadcast, &cfg, pwm_handler);
    APP_ERROR_CHECK(err_code);
}

static void lf_sense_enable(void) {
    // PWM bit timing divides HFCLK by a fixed ratio. On HFINT (64 MHz RC,
    // ±1.5% at 25°C after factory trim, wider over temperature) this gives a
    // chip-to-chip spread that NRZ readers — which see cumulative error across
    // runs of same-polarity bits with no intra-run resync — reject even when
    // Manchester/FSK readers don't. Holding HFXO brings the PWM clock to
    // ±40 ppm, which is also tight enough for differential PSK encodings
    // (e.g. IDTECK) where what the reader decodes are bit-to-bit phase
    // transitions, so absolute phase lock to the reader's carrier is not
    // required. The tag-mode antenna taps on this board are envelope-only,
    // which rules out coherent demodulation or phase-lock-based approaches,
    // but does not preclude the differential-phase encodings supported here.
    //
    // Paired release in lf_sense_disable(). SD reference-counts HFXO requests,
    // so this coexists with BLE. Both functions run from thread context
    // (tag_mode_enter/tag_emulation_sense_end) where SVCs are safe.
    sd_clock_hfclk_request();
    uint32_t hfclk_running = 0;
    while (!hfclk_running) {
        sd_clock_hfclk_is_running(&hfclk_running);
    }

    lpcomp_init();
    pwm_init();  // use precise hardware pwm to broadcast card id
    if (is_lf_field_exists()) {
        lpcomp_event_handler(NRF_LPCOMP_EVENT_UP);
    }
}

static void lf_sense_disable(void) {
    m_is_lf_emulating = false;
    nrfx_pwm_uninit(&m_broadcast);
    nrfx_lpcomp_uninit();
    m_pwm_seq = NULL;
    m_is_lf_emulating = false;
    sd_clock_hfclk_release();
}

static enum {
    LF_SENSE_STATE_NONE,
    LF_SENSE_STATE_DISABLE,
    LF_SENSE_STATE_ENABLE,
} m_lf_sense_state = LF_SENSE_STATE_NONE;

/**
 * @brief switchLfFieldInductionToEnableTheState
 */
void lf_tag_125khz_sense_switch(bool enable) {
    // init modulation PIN as output PIN
    nrf_gpio_cfg_output(LF_MOD);
    // turn off mod, otherwise its hard to judge RSSI
    ANT_NO_MOD();

    if ((m_lf_sense_state == LF_SENSE_STATE_NONE || m_lf_sense_state == LF_SENSE_STATE_DISABLE) && enable) {
        // switch from disable -> enable
        m_lf_sense_state = LF_SENSE_STATE_ENABLE;
        lf_sense_enable();
    } else if (m_lf_sense_state == LF_SENSE_STATE_ENABLE && !enable) {
        // switch from enable -> disable
        m_lf_sense_state = LF_SENSE_STATE_DISABLE;
        lf_sense_disable();
    }
}

/** @brief lf card data loader
 * @param type     Refined tag type
 * @param buffer   Data buffer
 */
int lf_tag_data_loadcb(tag_specific_type_t type, tag_data_buffer_t *buffer) {
    m_cycle_count = 1;
    m_cycle_index = 0;
    m_cycle_record = NULL;
    // ensure buffer size is large enough for specific tag type,
    // so that tag data (e.g., card numbers) can be converted to corresponding pwm sequence here.
    const lf_tag_protocol_t *p = lf_protocol(type);
    if (p == NULL || buffer->length < p->id_size) {
        NRF_LOG_ERROR("no valid data exists in buffer for tag type: %d.", type);
        return 0;
    }
    m_tag_type = type;
    m_pwm_seq = p->proto->modulator(NULL, buffer->buffer);
    if (lf_cycle_valid(buffer->buffer, buffer->length)) {
        // The other cards are modulated when their turn comes (cycle_select): a waveform can
        // take kilobytes, too much to keep one per card.
        m_cycle_record = buffer->buffer;
        m_cycle_count = buffer->buffer[LF_CYCLE_COUNT];
        NRF_LOG_INFO("load lf data of type %d with %d cards finish.", type, m_cycle_count);
        return LF_CYCLE_SIZE;
    }
    NRF_LOG_INFO("load lf data of type %d finish.", type);
    return p->id_size;
}

/** @brief LF card data save callback
 * @param type      Refined tag type
 * @param buffer    Data buffer
 * @return The length of the data that needs to be saved is that it does not save when 0
 */
int lf_tag_data_savecb(tag_specific_type_t type, tag_data_buffer_t *buffer) {
    // Make sure to load this tag before allowing saving
    if (m_tag_type != type) return 0;
    if (lf_cycle_prepare_save(buffer->buffer, buffer->length)) return LF_CYCLE_SIZE;
    return lf_tag_id_size(type);
}

bool lf_tag_data_factory(uint8_t slot, tag_specific_type_t tag_type, uint8_t *tag_id, uint16_t length) {
    // write data to flash
    tag_sense_type_t sense_type = get_sense_type_from_tag_type(tag_type);
    fds_slot_record_map_t map_info;  // Get the special card slot FDS record information
    get_fds_map_by_slot_sense_type_for_dump(slot, sense_type, &map_info);
    // Call the blocked FDS to write the function, and write the data of the specified field type of the card slot into the Flash
    bool ret = fds_write_sync(map_info.id, map_info.key, length, (uint8_t *)tag_id);
    if (ret) {
        NRF_LOG_INFO("Factory slot data success.");
    } else {
        NRF_LOG_ERROR("Factory slot data error.");
    }
    return ret;
}

/** @brief Id card deposit card number before callback
 * @param slot      Card slot number
 * @param tag_type  Refined tag type
 * @return Whether the format is successful, if the formatting is successful, it will return to True, otherwise False will be returned
 */
bool lf_tag_em410x_data_factory(uint8_t slot, tag_specific_type_t tag_type) {
    static const uint8_t tag_id_base[LF_EM410X_TAG_ID_SIZE] = {0xDE, 0xAD, 0xBE, 0xEF, 0x88};
    static const uint8_t tag_id_electra[LF_EM410X_ELECTRA_TAG_ID_SIZE] = {0xDE, 0xAD, 0xBE, 0xEF, 0x88,
                                                                          0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
                                                                         };

    switch (tag_type) {
        case TAG_TYPE_EM410X_ELECTRA:
            return lf_tag_data_factory(slot, tag_type, (uint8_t *)tag_id_electra, sizeof(tag_id_electra));
        case TAG_TYPE_EM410X:
            return lf_tag_data_factory(slot, tag_type, (uint8_t *)tag_id_base, sizeof(tag_id_base));
        default:
            return false;
    }
}

/** @brief Id card deposit card number before callback
 * @param slot      Card slot number
 * @param tag_type  Refined tag type
 * @return Whether the format is successful, if the formatting is successful, it will return to True, otherwise False will be returned
 */
bool lf_tag_hidprox_data_factory(uint8_t slot, tag_specific_type_t tag_type) {
    // default id, must to align(4), more word...
    uint8_t tag_id[13] = {0x01, 0x00, 0x00, 0x00, 0x64, 0x00, 0x00, 0x00, 0x51, 0x45, 0x00, 0x00, 0x00};
    return lf_tag_data_factory(slot, tag_type, tag_id, sizeof(tag_id));
}

/** @brief Id card deposit card number before callback
 * @param slot      Card slot number
 * @param tag_type  Refined tag type
 * @return Whether the format is successful, if the formatting is successful, it will return to True, otherwise False will be returned
 */
bool lf_tag_ioprox_data_factory(uint8_t slot, tag_specific_type_t tag_type) {
    uint8_t tag_id[16] = {
        0x01, 0xAA, 0x30, 0x39, 0x00, 0x78, 0x6A, 0xA0, 0x33, 0x09, 0xCF, 0xEF, 0x00, 0x00, 0x00, 0x00
    };
    return lf_tag_data_factory(slot, tag_type, tag_id, sizeof(tag_id));
}

/** @brief Id card deposit card number before callback
 * @param slot      Card slot number
 * @param tag_type  Refined tag type
 * @return Whether the format is successful, if the formatting is successful, it will return to True, otherwise False will be returned
 */
bool lf_tag_viking_data_factory(uint8_t slot, tag_specific_type_t tag_type) {
    // default id
    uint8_t tag_id[4] = {0xDE, 0xAD, 0xBE, 0xEF};
    return lf_tag_data_factory(slot, tag_type, tag_id, sizeof(tag_id));
}

bool lf_tag_pac_data_factory(uint8_t slot, tag_specific_type_t tag_type) {
    // default id: 8 ASCII bytes
    uint8_t tag_id[8] = {'C', 'A', 'R', 'D', '0', '0', '0', '1'};
    return lf_tag_data_factory(slot, tag_type, tag_id, sizeof(tag_id));
}

bool lf_tag_jablotron_data_factory(uint8_t slot, tag_specific_type_t tag_type) {
    // default id: 5 bytes (top bit must be 0)
    uint8_t tag_id[5] = {0x01, 0xB6, 0x69, 0x00, 0x00};
    return lf_tag_data_factory(slot, tag_type, tag_id, sizeof(tag_id));
}

/** @brief IDTECK default frame: preamble "IDTK" + 32-bit placeholder card data. */
bool lf_tag_idteck_data_factory(uint8_t slot, tag_specific_type_t tag_type) {
    uint8_t tag_id[LF_IDTECK_TAG_ID_SIZE] = {
        0x49, 0x44, 0x54, 0x4B,   // "IDTK" preamble (MSB first)
        0xDE, 0xAD, 0xBE, 0xEF,   // default card data
    };
    return lf_tag_data_factory(slot, tag_type, tag_id, sizeof(tag_id));
}

/** @brief Pyramid default frame: full 128-bit on-air frame, replayed verbatim. */
bool lf_tag_pyramid_data_factory(uint8_t slot, tag_specific_type_t tag_type) {
    // A valid 26-bit Pyramid frame (preamble + Wiegand + parity + CRC-8/Maxim).
    uint8_t tag_id[LF_PYRAMID_TAG_ID_SIZE] = {
        0x00, 0x01, 0x01, 0x4A, 0x3E, 0x01, 0x01, 0x01,
        0x01, 0x01, 0x01, 0x4A, 0xE3, 0x5E, 0x7F, 0x4F,
    };
    return lf_tag_data_factory(slot, tag_type, tag_id, sizeof(tag_id));
}
