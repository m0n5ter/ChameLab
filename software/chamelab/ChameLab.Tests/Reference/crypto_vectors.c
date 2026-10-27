#include <stdio.h>
#include <stdlib.h>
#include <inttypes.h>
#include "crapto1.h"
#include "parity.h"
#include "mfkey.h"
static uint32_t encrypted_nonce(uint64_t key,uint32_t uid,uint32_t nt) {
 struct Crypto1State s;crypto1_init(&s,key);return nt^crypto1_word(&s,uid^nt,0);
}
static void auth(uint64_t key,uint32_t uid,uint32_t nt,uint32_t plain,uint32_t *nr,uint32_t *ar) {
 struct Crypto1State s;crypto1_init(&s,key);crypto1_word(&s,uid^nt,0);
 *nr=plain^crypto1_word(&s,plain,0);*ar=prng_successor(nt,64)^crypto1_word(&s,0,0);
}
static uint8_t parity(uint32_t nt,uint32_t enc) {
 uint32_t ks=nt^enc;
 return (oddparity8(nt>>24)^oddparity8(enc>>24)^BIT(ks,16))
 |((oddparity8(nt>>16)^oddparity8(enc>>16)^BIT(ks,8))<<1)
 |((oddparity8(nt>>8)^oddparity8(enc>>8)^BIT(ks,0))<<2);
}
static void dark(uint64_t key,uint32_t uid,uint32_t nt,uint32_t prefix,uint32_t ar,uint64_t *par,uint64_t *ks) {
 *par=0;*ks=0;
 for(uint32_t c=0;c<8;c++) {
  struct Crypto1State s;crypto1_init(&s,key);crypto1_word(&s,uid^nt,0);
  uint32_t nrenc=prefix|(c<<5),ks1=crypto1_word(&s,nrenc,1),nr=ks1^nrenc;
  uint32_t ks2=crypto1_word(&s,0,0),rr=ks2^ar;
  uint8_t nack=0;for(int b=0;b<4;b++)nack|=crypto1_bit(&s,0,0)<<b;
  uint8_t pb=0;
  pb|=(oddparity8(nr)^BIT(ks2,24))<<3;
  pb|=(oddparity8(rr>>24)^BIT(ks2,16))<<4;
  pb|=(oddparity8(rr>>16)^BIT(ks2,8))<<5;
  pb|=(oddparity8(rr>>8)^BIT(ks2,0))<<6;
  pb|=(oddparity8(rr)^BIT(nack,0))<<7;
  *par=(*par<<8)|pb;*ks=(*ks<<8)|nack;
 }
}
static int cmp_key(const void *a,const void *b) {
 uint64_t x=*(const uint64_t*)a,y=*(const uint64_t*)b;return (x>y)-(x<y);
}
int main(void) {
 uint64_t keys[]={0,0xffffffffffffULL,0xa0a1a2a3a4a5ULL,0x112233445566ULL};
 for(int i=0;i<4;i++) {
  uint64_t key=keys[i],after,back;uint32_t uid=0x12345678u+i*0x23456789u,input=0xabcdef01u+i*0x13579bdu;int encrypted=i&1;
  struct Crypto1State s;crypto1_init(&s,key);
  uint8_t bit=crypto1_bit(&s,input&1,encrypted),byte=crypto1_byte(&s,(uint8_t)(input>>4),encrypted);
  uint32_t word=crypto1_word(&s,input,encrypted);crypto1_get_lfsr(&s,&after);
  lfsr_rollback_word(&s,input,encrypted);lfsr_rollback_byte(&s,(uint8_t)(input>>4),encrypted);lfsr_rollback_bit(&s,input&1,encrypted);crypto1_get_lfsr(&s,&back);
  printf("{\"kind\":\"primitive\",\"key\":\"%012" PRIX64 "\",\"input\":%u,\"encrypted\":%d,\"bit\":%u,\"byte\":%u,\"word\":%u,\"after\":\"%012" PRIX64 "\",\"back\":\"%012" PRIX64 "\",\"prng\":%u}\n",key,input,encrypted,bit,byte,word,after,back,prng_successor(input,42));
  uint32_t nt0=0x87654321u+i,nt1=i==0?nt0:0x10203040u+i,nr0,ar0,nr1,ar1;
  auth(key,uid,nt0,0x33445566u+i,&nr0,&ar0);auth(key,uid,nt1,0x89abcdefu+i,&nr1,&ar1);
  printf("{\"kind\":\"mfkey\",\"key\":\"%012" PRIX64 "\",\"uid\":%u,\"nt0\":%u,\"nr0\":%u,\"ar0\":%u,\"nt1\":%u,\"nr1\":%u,\"ar1\":%u}\n",key,uid,nt0,nr0,ar0,nt1,nr1,ar1);
  uint32_t dist=42+i*16,base0=0xabcdef01u+i,base1=0x55aa33ccu+i;
  uint32_t actual0=dist+(i==0?-14:i==1?14:0),actual1=dist+7;
  uint32_t ntp0=prng_successor(base0,actual0),ntp1=prng_successor(base1,actual1),enc0=encrypted_nonce(key,uid,ntp0),enc1=encrypted_nonce(key,uid,ntp1);
  printf("{\"kind\":\"nested\",\"key\":\"%012" PRIX64 "\",\"uid\":%u,\"distance\":%u,\"nt0\":%u,\"enc0\":%u,\"par0\":%u,\"nt1\":%u,\"enc1\":%u,\"par1\":%u}\n",key,uid,dist,base0,enc0,parity(ntp0,enc0),base1,enc1,parity(ntp1,enc1));
  uint32_t nonce=i<2?0x01200145:0x009080a2;int sd=i==3?161:160;
  printf("{\"kind\":\"static\",\"key\":\"%012" PRIX64 "\",\"uid\":%u,\"knownType\":%u,\"nonce\":%u,\"enc0\":%u,\"enc1\":%u}\n",key,uid,i==3?0x61:0x60,nonce,encrypted_nonce(key,uid,prng_successor(nonce,sd)),encrypted_nonce(key,uid,prng_successor(nonce,sd+160)));
  if(i>0) {
   uint32_t prefix=0xabcdef01&0xffffff1f,ar=0x11223344;uint64_t par,ks,*found=0;dark(key,uid,nt0,prefix,ar,&par,&ks);
   uint32_t count=nonce2key(uid,nt0,prefix,ar,par,ks,&found);int has=0;for(uint32_t j=0;j<count;j++)has|=found[j]==key;
   printf("{\"kind\":\"darkside\",\"key\":\"%012" PRIX64 "\",\"uid\":%u,\"nt\":%u,\"nr\":%u,\"ar\":%u,\"par\":\"%016" PRIX64 "\",\"ks\":\"%016" PRIX64 "\",\"cCount\":%u,\"cContains\":%s}\n",key,uid,nt0,prefix,ar,par,ks,count,has?"true":"false");free(found);
  }
 }
 {
  uint64_t key=0xa0a1a2a3a4a5ULL,par,ks[2],*lists[2]={0};uint32_t uid=0x12345678,nt=0x87654321,ar=0x11223344,prefix[2],counts[2];int picked=0;
  for(uint32_t attempt=0;attempt<32&&picked<2;attempt++) {
   uint32_t nr=(0xabcdef01+attempt*0x12345)&0xffffff1f;uint64_t *list=0,kstream;
   dark(key,uid,nt,nr,ar,&par,&kstream);uint32_t count=nonce2key(uid,nt,nr,ar,0,kstream,&list);int has=0;
   for(uint32_t j=0;j<count;j++)has|=list[j]==key;
   if(!has){free(list);continue;}
   lists[picked]=list;counts[picked]=count;prefix[picked]=nr;ks[picked]=kstream;picked++;
  }
  if(picked!=2){fprintf(stderr,"Could not construct two compatible no-parity captures\n");return 1;}
  for(int i=0;i<2;i++)qsort(lists[i],counts[i],sizeof(uint64_t),cmp_key);
  printf("{\"kind\":\"darkside-zero\",\"key\":\"%012" PRIX64 "\",\"uid\":%u,\"nt\":%u,\"ar\":%u,\"nr0\":%u,\"ks0\":\"%016" PRIX64 "\",\"nr1\":%u,\"ks1\":\"%016" PRIX64 "\",\"cKeys\":[",key,uid,nt,ar,prefix[0],ks[0],prefix[1],ks[1]);
  uint32_t i=0,j=0,count=0;while(i<counts[0]&&j<counts[1]) {
   if(lists[0][i]<lists[1][j])i++;else if(lists[0][i]>lists[1][j])j++;else {printf("%s\"%012" PRIX64 "\"",count?",":"",lists[0][i]);count++;i++;j++;}
  }
  printf("]}\n");free(lists[0]);free(lists[1]);
 }
 return 0;
}
