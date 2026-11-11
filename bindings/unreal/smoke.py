# SPDX-License-Identifier: GPL-2.0-or-later
"""Call the published Win64 ABI from the Unreal Editor Python commandlet."""

import ctypes as ct
import math
import os
from pathlib import Path

import unreal


class State(ct.Structure):
    _fields_ = [(name, ct.c_double) for name in ("x", "y", "z", "vx", "vy", "vz")]


def absolute_input(name):
    path = Path(os.environ[name])
    if not path.is_absolute() or not path.is_file():
        raise ValueError(f"{name} must name an existing absolute file.")
    return path


def verify():
    library = ct.CDLL(str(absolute_input("SPACETANGENT_NATIVE_DLL")))
    library.st_abi_version.argtypes = ()
    library.st_abi_version.restype = ct.c_uint32
    library.st_de440_open.argtypes = (ct.c_char_p, ct.c_int32, ct.POINTER(ct.c_uint64))
    library.st_de440_open.restype = ct.c_int32
    library.st_de440_state.argtypes = (ct.c_uint64, ct.c_int64, ct.c_int64, ct.c_int32, ct.c_int32, ct.POINTER(State))
    library.st_de440_state.restype = ct.c_int32
    library.st_de440_close.argtypes = (ct.c_uint64,)
    library.st_de440_close.restype = ct.c_int32

    if library.st_abi_version() != 0x10000 or ct.sizeof(State) != 48:
        raise RuntimeError("Unexpected SpaceTangent ABI version or state layout.")
    path = str(absolute_input("SPACETANGENT_DE440")).encode("utf-8")
    handle = ct.c_uint64()
    if library.st_de440_open(path, len(path), ct.byref(handle)) != 0 or not handle.value:
        raise RuntimeError("SpaceTangent could not open the DE440 data.")
    try:
        moon = State()
        if library.st_de440_state(handle.value, 2451545, 0, 301, 399, ct.byref(moon)) != 0:
            raise RuntimeError("SpaceTangent could not query the geocentric lunar state.")
        # Independent CSPICE reference at JD 2451545 TDB, geometric J2000 state.
        expected = (-291608.38463343546, -266716.83339423337, -76102.48709990202,
                    0.64353138771903273, -0.66608768409163044, -0.30132570498227307)
        for (name, _), value, tolerance in zip(State._fields_, expected, (5e-6,) * 3 + (5e-10,) * 3):
            if not math.isclose(getattr(moon, name), value, rel_tol=0, abs_tol=tolerance):
                raise RuntimeError(f"Lunar {name} differs from the CSPICE reference.")
    finally:
        if library.st_de440_close(handle.value) != 0:
            raise RuntimeError("SpaceTangent could not close the DE440 handle.")

    unreal.log(f"SpaceTangent Unreal C ABI smoke passed in {unreal.SystemLibrary.get_engine_version()}.")


if __name__ == "__main__":
    verify()
