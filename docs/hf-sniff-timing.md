# HF sniff timing

`HF14A_SNIFF` (2020) accepts a big-endian 16-bit capture timeout in milliseconds,
up to 30000. An optional third byte of `1` requests timed format v1. A two-byte
request retains the original `[bits:be16][data...]` response. Firmware predating
v2.3.15-cl ignores the third byte and returns the original response; clients also
retry a rejected extended request with two bytes for firmware that validates its
request length.

The timed response starts with `00 00 48 46 01` (`00 00 HF`, version 1). Zero bits
cannot begin a legacy frame. Each subsequent record is:

| Field | Encoding | Meaning |
| --- | --- | --- |
| Header | be16 | Bit 15: card answer; bit 14: data includes interleaved parity; bits 0–13: stored bit count |
| Start | be32 | Timer ticks since capture start, including the wait for the first frame |
| Duration | be32 | Timer ticks from frame start to frame end |
| Data | bytes | `ceil(stored bit count / 8)` bytes, in the original capture order |

TIMER3 runs at 16 MHz with a 32-bit counter (TIMER4 is used by the NFCT driver).
Four dynamically allocated PPI
channels capture the NFCT RXFRAMESTART, RXFRAMEEND, TXFRAMESTART and TXFRAMEEND
events directly into its capture registers. Software receives the reader frame
before parity removal and the card frame at TX_FRAMEEND. Interrupt latency does
not enter the timestamps. The timer covers the complete 30-second capture
without wrapping. A frame already in progress when capture starts is ignored.
Timer and PPI resources are released after the capture; if they cannot be
allocated, the response falls back to the original format without timing.

The existing 3800-byte capture buffer includes the prefix and record headers;
timed records use eight more bytes per frame. A full buffer discards further
frames just as a legacy capture does.

ChameLab converts ticks to ISO14443-A carrier periods using
`round(ticks * 339 / 400)`, independently for each timestamp. The UI and CLI show
time since capture start and the gap from the previous frame's end. Legacy
captures have no measured timing and do not show these values.

## Proxmark3 files

Export stays in the standard binary `.trace` format: little-endian 32-bit start,
16-bit duration, 15-bit byte count and one direction bit, followed by data and
parity. For ISO14443-A, start and duration are in 13.56 MHz carrier periods.
There is no ChameLab header or calendar date in the exported file.

Captured start times replace the estimates used by legacy captures. Durations
above 65535 carrier periods are saturated to 65535, matching Proxmark3's writer;
the in-memory capture retains the full duration for gap calculations. Imported
timestamps, durations and parity bytes survive saving without being regenerated.
Legacy captures still export with estimated timing because their actual pauses
were never recorded.

See the upstream [trace format](https://github.com/RfidResearchGroup/proxmark3/blob/master/doc/trace_notes.md).
