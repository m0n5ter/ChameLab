#ifndef CRYPTO1_H
#define CRYPTO1_H

#include <stdint.h>
#include <stdbool.h>

// debug to use
void Crypto1GetState(uint8_t *pEven, uint8_t *pOdd);

/* Gets the current keystream-bit, without shifting the internal LFSR */
uint8_t Crypto1FilterOutput(void);

/* Set up Crypto1 cipher using the given Key, Uid and CardNonce. Also encrypts
 * the CardNonce in-place while in non-linear mode. */
void Crypto1Setup(uint8_t Key[6], uint8_t Uid[4], uint8_t CardNonce[4]);
/* Same for nested auth. NonceParity[0]..[3] will contain the parity bits after return */
void Crypto1SetupNested(uint8_t Key[6], uint8_t Uid[4], uint8_t CardNonce[4], uint8_t NonceParity[4], bool Decrypt);

/* Load the decrypted ReaderNonce into the Crypto1 state LFSR */
void Crypto1Auth(uint8_t EncryptedReaderNonce[4]);

/* Encrypt/Decrypt array */
void Crypto1ByteArray(uint8_t *Buffer, uint8_t Count);

/* Generate 4 Bits of key stream */
uint8_t Crypto1Nibble(void);

/* Execute 'ClockCount' cycles on the PRNG state 'State' */
void Crypto1PRNG(uint8_t State[4], uint8_t ClockCount);
uint32_t Crypto1FreePRNG(uint32_t x, uint32_t n);

/* Encrypts buffer with consideration of parity bits */
void Crypto1EncryptWithParity(uint8_t *Buffer, uint8_t BitCount);

/* Encrypts buffer with LFSR feedback within reader nonce and considers parity bits */
void Crypto1ReaderAuthWithParity(uint8_t PlainReaderAnswerWithParityBits[9]);

/* Fused encrypt + parity + wire-frame packer. */
typedef void (*Crypto1StreamCb)(uint32_t bits);
uint16_t Crypto1EncryptToFrame(uint8_t *Frame, uint8_t *Src, uint8_t Count, uint8_t CrcLen,
                                       uint8_t TriggerBytes, Crypto1StreamCb Cb);

#endif //CRYPTO1_H
