/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef SPACE_TANGENT_H
#define SPACE_TANGENT_H

#include <stdint.h>

#ifdef _WIN32
#define ST_CALL __cdecl
#else
#define ST_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef uintptr_t st_handle;
typedef int32_t st_status;

enum {
    ST_OK = 0,
    ST_INVALID_ARGUMENT = 1,
    ST_INVALID_HANDLE = 2,
    ST_FILE_UNAVAILABLE = 3,
    ST_INVALID_DATA = 4,
    ST_OUTSIDE_COVERAGE = 5,
    ST_INTERNAL_ERROR = 6
};

typedef struct st_state {
    double x_km, y_km, z_km;
    double vx_km_s, vy_km_s, vz_km_s;
} st_state;

/* High 16 bits are the ABI major version. */
uint32_t ST_CALL st_abi_version(void);

/* UTF-8 absolute path, explicit byte count, no terminating NUL. Own the handle until close. */
st_status ST_CALL st_de440_open(const uint8_t *path, int32_t path_length, st_handle *output);
st_status ST_CALL st_de440_close(st_handle handle);

/* TDB day starts at noon. Target and centre use NAIF IDs 0, 10, 301 or 399. Frame is ICRF3. */
st_status ST_CALL st_de440_state(st_handle handle, int64_t julian_day, int64_t nanoseconds_of_day,
                                 int32_t target, int32_t center, st_state *output);

#ifdef __cplusplus
}
#endif
#endif
