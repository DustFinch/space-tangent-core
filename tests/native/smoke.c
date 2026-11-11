/* SPDX-License-Identifier: GPL-2.0-or-later */
#include "space_tangent.h"
#include <math.h>
#include <stddef.h>
#include <stdio.h>
#include <windows.h>

typedef uint32_t (ST_CALL *version_fn)(void);
typedef st_status (ST_CALL *open_fn)(const uint8_t *, int32_t, st_handle *);
typedef st_status (ST_CALL *close_fn)(st_handle);
typedef st_status (ST_CALL *state_fn)(st_handle, int64_t, int64_t, int32_t, int32_t, st_state *);

_Static_assert(sizeof(st_state) == 48, "State must contain six consecutive doubles");
_Static_assert(offsetof(st_state, vx_km_s) == 24, "Velocity must follow the position");

static int failed(const char *message) {
    fprintf(stderr, "%s\n", message);
    return 1;
}

int wmain(int argc, wchar_t **argv) {
    if (argc != 3)
        return failed("Expected the DLL and DE440 paths.");
    HMODULE library = LoadLibraryW(argv[1]);
    if (!library)
        return failed("Could not load the native DLL.");
    version_fn version = (version_fn)GetProcAddress(library, "st_abi_version");
    open_fn open_model = (open_fn)GetProcAddress(library, "st_de440_open");
    close_fn close_model = (close_fn)GetProcAddress(library, "st_de440_close");
    state_fn state = (state_fn)GetProcAddress(library, "st_de440_state");
    if (!version || !open_model || !close_model || !state || version() != 0x10000)
        return failed("Missing or incompatible native exports.");

    uint8_t path[4097];
    int bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, argv[2], -1,
                                    (char *)path, sizeof(path), NULL, NULL);
    st_handle handle = 0;
    if (bytes <= 1 || open_model(path, bytes - 1, &handle) != ST_OK || handle == 0)
        return failed("Could not load DE440 through the C ABI.");
    st_state result = {0};
    if (state(handle, 2451545, 0, 301, 399, &result) != ST_OK)
        return failed("Could not query the Moon relative to Earth.");
    if (fabs(result.x_km + 291608.38463343546) > 5e-6 ||
        fabs(result.y_km + 266716.83339423337) > 5e-6 ||
        fabs(result.z_km + 76102.48709990202) > 5e-6 ||
        fabs(result.vx_km_s - 0.64353138771903273) > 5e-10 ||
        fabs(result.vy_km_s + 0.66608768409163044) > 5e-10 ||
        fabs(result.vz_km_s + 0.30132570498227307) > 5e-10)
        return failed("The six returned components differ from the CSPICE reference.");
    if (state(handle, 2451545, -1, 301, 399, &result) != ST_INVALID_ARGUMENT ||
        state(handle, 1, 0, 301, 399, &result) != ST_OUTSIDE_COVERAGE ||
        close_model(handle) != ST_OK || close_model(handle) != ST_INVALID_HANDLE ||
        state(handle, 2451545, 0, 301, 399, &result) != ST_INVALID_HANDLE)
        return failed("Invalid arguments, coverage or handle lifetime failed.");

    puts("SpaceTangent Win64 C ABI smoke passed");
    return 0;
}
