# Host-build harness for crypto1 correctness.
# DOCVERSION=CR1TEST_20260823

RFID_DIR := ../../application/src/rfid
NFCTAG_HF_DIR := $(RFID_DIR)/nfctag/hf
CMSIS_DIR := ../../nrf52_sdk/components/toolchain/cmsis/include/

CC      ?= gcc
CFLAGS  ?= -O2 -g -Wall -Wextra -Wno-unused-parameter -Wno-sign-compare
CPPFLAGS := -I. -Ishim -I$(RFID_DIR) -I$(NFCTAG_HF_DIR)

OBJS := \
  build/mf1_crypto1.o \
  build/mf1_crapto1.o \
  build/crypto1_helper.o \
  build/parity.o \
  build/crc_utils.o \
  build/byte_mirror.o \
  build/nfc_14a_wrap.o \
  build/test_crypto1.o

TARGET := build/test_crypto1

.PHONY: all clean run
all: $(TARGET)

$(TARGET): $(OBJS)
	$(CC) $(CFLAGS) -o $@ $(OBJS)

build/%.o: $(RFID_DIR)/%.c | build
	$(CC) $(CFLAGS) $(CPPFLAGS) -c -o $@ $<

build/%.o: $(NFCTAG_HF_DIR)/%.c | build
	$(CC) $(CFLAGS) $(CPPFLAGS) -c -o $@ $<

build/%.o: %.c | build
	$(CC) $(CFLAGS) $(CPPFLAGS) -c -o $@ $<

# nfc_14a.c cannot be host-compiled (nrfx / NRF_LOG), but the harness needs the
# REAL nfc_tag_14a_wrap_frame as its packing oracle — so extract just that
# function into its own translation unit.
build/nfc_14a_wrap.c: $(NFCTAG_HF_DIR)/nfc_14a.c | build
	{ echo '#include "nfc_14a_wrap.h"'; echo '#include "byte_mirror.h"'; \
	  sed -n '/^uint8_t nfc_tag_14a_wrap_frame/,/^}/p' $<; \
	  sed -n '/^uint16_t nfc_tag_14a_unwrap_frame/,/^}/p' $<; } > $@

build/nfc_14a_wrap.o: build/nfc_14a_wrap.c
	$(CC) $(CFLAGS) $(CPPFLAGS) -c -o $@ $<

build:
	mkdir -p build

run: $(TARGET)
	./$(TARGET)

clean:
	rm -rf build
