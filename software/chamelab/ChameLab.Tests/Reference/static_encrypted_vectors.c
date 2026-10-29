// Reference fingerprints for the managed static encrypted nested regression tests.
#define main original_main
#include "../../../src/staticnested_1nt.c"
#undef main
#define main original_seed_main
#include "../../../src/staticnested_2x1nt_rf08s.c"
#undef main
static int cmp(const void *a,const void *b) { uint64_t x=*(const uint64_t*)a,y=*(const uint64_t*)b;return (x>y)-(x<y); }
int main(void) {
 uint32_t count=0;
 uint64_t *keys=generate_keys(0xa13e4902,0xd14191b3,0x2e9e49fc,0,&count);
 qsort(keys,count,sizeof(uint64_t),cmp);
 uint64_t hash=14695981039346656037ULL;
 for(uint32_t i=0;i<count;i++) for(int j=0;j<6;j++) { hash^=(keys[i]>>(j*8))&255; hash*=1099511628211ULL; }
 printf("count=%u hash=%016llx\n",count,hash);
 init_lfsr16_table();
 printf("seeds=%04x %04x %04x %04x\n",compute_seednt16_nt32(0xd14191b3,0x112233445566ULL),compute_seednt16_nt32(0x12345678,0xffffffffffffULL),compute_seednt16_nt32(0x00010000,0),compute_seednt16_nt32(0,0));
 free(keys);return 0;
}
