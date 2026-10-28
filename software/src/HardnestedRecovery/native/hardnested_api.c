#include "hardnested_api.h"
#include "cmdhfmfhard.h"

int chamelab_hardnested_recover(uint32_t uid, const char *path, uint64_t *key) {
    if (path == NULL || key == NULL) return -1;
    *key = 0;
    return mfnestedhard(0, 0, NULL, 0, 0, NULL, false, false, false, key, NULL, uid, (char *)path);
}
