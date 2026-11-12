"""Regenerate SEN ordering vectors from the pinned Proxmark3 C implementation.

python generate_sen_vectors.py /path/to/proxmark3/client/src/cmdhfmfsen.c
Requires gcc. Upstream: RfidResearchGroup/proxmark3, GPLv3,
commit c7b80e1bc89ed03983832f93b1d2b5f980c8bc9b.
The C functions are extracted verbatim; only allocation/seed-loop scaffolding
and synthetic input generation are supplied here. No card captures are used.
"""
from pathlib import Path
import subprocess
import sys
import tempfile


def function(source, signature):
    start = source.index(signature)
    brace = source.index('{', start)
    depth, end = 1, brace + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


source = Path(sys.argv[1]).read_text()
header = r'''
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <stdbool.h>
#include <string.h>
#include <limits.h>
#include <assert.h>
#define PM3_SUCCESS 0
#define PM3_EINVARG -1
#define PM3_EMALLOC -2
#define PM3_EOPABORTED -3
#define FM11RF08S_SECTORS 17
typedef struct { uint64_t key; uint16_t seed; bool seed_valid; } fm11_candidate_t;
typedef struct { fm11_candidate_t *data; uint32_t count, cap; } fm11_keylist_t;
typedef struct { fm11_candidate_t candidate; uint32_t score, ordinal; } fm11_scored_candidate_t;
typedef struct { uint16_t seed; uint32_t a_count, b_count; } fm11_seed_bucket_t;
typedef struct { uint8_t nt[17][2][4]; } iso14a_fm11rf08s_nonces_with_data_t;
static uint16_t fm11_prev8_lfsr16[65536], fm11_prev14_lfsr16[65536];
static bool fm11_lfsr16_ready;
static bool kbd_enter_pressed(void) { return false; }
'''
functions = [
    'static void fm11_keylist_free(', 'static int fm11_keylist_push(',
    'static int fm11_keylist_push_seeded(', 'static int fm11_keylist_reserve(',
    'static bool fm11_keylist_promote_existing(', 'static int fm11_keylist_promote_or_prepend(',
    'static uint32_t fm11_bytes_to_u32(', 'static int fm11_cmp_scored_candidate(',
    'static void fm11_init_lfsr16_table(', 'static uint16_t fm11_compute_seednt16_nt32(',
]
body = header + '\n'.join(function(source, s) for s in functions)
body += r'''
static void fm11_compute_list_seeds(fm11_keylist_t *list, uint32_t nt) {
    fm11_init_lfsr16_table();
    for (uint32_t i=0; i<list->count; ++i) {
        list->data[i].seed = fm11_compute_seednt16_nt32(nt, list->data[i].key);
        list->data[i].seed_valid = true;
    }
}
'''
functions = [
    'static int fm11_prioritize_by_paired_seed(', 'static int fm11_intersect_pair(',
    'static int fm11_build_seed_buckets(', 'static int fm11_cmp_seed_bucket(',
    'static int fm11_sort_candidates_by_seed_bucket(', 'static int fm11_prioritize_defaults(',
    'static uint32_t fm11_estimate_candidate_path_cost(',
]
body += '\n'.join(function(source, s) for s in functions)
body += r'''
static uint64_t random_key(uint64_t *state) {
    *state = *state * UINT64_C(6364136223846793005) + UINT64_C(1442695040888963407);
    return (*state >> 16) & UINT64_C(0xFFFFFFFFFFFF);
}
static uint64_t fingerprint(fm11_keylist_t *list) {
    uint64_t hash = UINT64_C(14695981039346656037);
    for (unsigned i=0; i<list->count; ++i)
        for (unsigned j=0; j<6; ++j)
            hash = (hash ^ ((list->data[i].key >> (8*j)) & 255)) * UINT64_C(1099511628211);
    return hash;
}
int main(void) {
    puts("[");
    for (int test=0; test<3; ++test) {
        fm11_keylist_t lists[17][2] = {0}, defaults = {0};
        uint32_t na = 5000, nb = test == 2 ? 0 : 6500;
        uint32_t nt_a = 0xD14191B3, nt_b = test == 1 ? nt_a : 0xCAAED70D;
        uint64_t state = 123456789;
        for (unsigned i=0; i<na; ++i) fm11_keylist_push(&lists[0][0], random_key(&state), 0);
        for (unsigned i=0; i<nb; ++i) fm11_keylist_push(&lists[0][1], random_key(&state), 0);
        uint64_t preferred[] = {lists[0][0].data[511].key, lists[0][0].data[0].key};
        for (unsigned i=0; i<2; ++i) fm11_keylist_push(&defaults, preferred[i], 0);
        for (unsigned k=0; k<2; ++k) fm11_prioritize_defaults(&lists[0][k], &defaults);
        if (nt_a != nt_b && nb) {
            assert(fm11_intersect_pair(nt_a, &lists[0][0], nt_b, &lists[0][1]) == 0);
            fm11_prioritize_by_paired_seed(&lists[0][0], &lists[0][1]);
            fm11_prioritize_by_paired_seed(&lists[0][1], &lists[0][0]);
            fm11_sort_candidates_by_seed_bucket(&lists[0][0], &lists[0][1]);
        }
        for (unsigned k=0; k<2; ++k) fm11_prioritize_defaults(&lists[0][k], &defaults);
        iso14a_fm11rf08s_nonces_with_data_t nonces = {0};
        for (unsigned b=0; b<4; ++b) { nonces.nt[0][0][b] = nt_a >> (24-8*b); nonces.nt[0][1][b] = nt_b >> (24-8*b); }
        uint64_t found_keys[17][2] = {0}; bool found[17][2] = {0};
        printf("{\"Na\":%u,\"Nb\":%u,\"NtA\":%u,\"NtB\":%u,\"Preferred\":[\"%012llX\",\"%012llX\"],", na, nb, nt_a, nt_b,
            (unsigned long long)preferred[0], (unsigned long long)preferred[1]);
        printf("\"CountA\":%u,\"CountB\":%u,\"HashA\":\"%016llX\",\"HashB\":\"%016llX\",\"CostA\":%u,\"CostB\":%u}%s\n",
            lists[0][0].count, lists[0][1].count,
            (unsigned long long)fingerprint(&lists[0][0]), (unsigned long long)fingerprint(&lists[0][1]),
            fm11_estimate_candidate_path_cost(&nonces,lists,found_keys,found,0,0),
            fm11_estimate_candidate_path_cost(&nonces,lists,found_keys,found,0,1), test == 2 ? "" : ",");
        fm11_keylist_free(&lists[0][0]); fm11_keylist_free(&lists[0][1]); fm11_keylist_free(&defaults);
    }
    puts("]");
}
'''
with tempfile.TemporaryDirectory(prefix='sen-reference-') as tmp:
    cfile, binary = Path(tmp) / 'reference.c', Path(tmp) / 'reference'
    cfile.write_text(body)
    subprocess.run(['gcc', '-std=gnu11', '-O2', str(cfile), '-o', str(binary)], check=True)
    output = subprocess.check_output([str(binary)], text=True)
Path(__file__).with_name('sen_order_vectors.json').write_text(output)
