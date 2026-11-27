/* Host-side prototype for the one nfc_14a.c function the crypto1 harness needs
 * as a packing oracle. nfc_14a.c itself cannot be host-compiled (nrfx / NRF_LOG
 * deps), so the Makefile extracts nfc_tag_14a_wrap_frame into its own TU and
 * includes this header there — a prototype drift then fails the build instead
 * of silently going wrong. */
#ifndef HOST_SHIM_NFC_14A_WRAP_H
#define HOST_SHIM_NFC_14A_WRAP_H

#include <stdint.h>
#include <stddef.h>

uint8_t nfc_tag_14a_wrap_frame(const uint8_t *pbtTx, const size_t szTxBits, const uint8_t *pbtTxPar, uint8_t *pbtFrame);
uint16_t nfc_tag_14a_unwrap_frame(const uint8_t *pbtFrame, const size_t szFrameBits, uint8_t *pbtRx, uint8_t *pbtRxPar);

#endif
