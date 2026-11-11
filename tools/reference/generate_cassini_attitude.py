"""Generate Cassini body-to-J2000 matrices with CSPICE and the original CK/FK/SCLK."""

import argparse
import ctypes as ct
import hashlib
from pathlib import Path
import struct


def sample_ticks(kernel):
    data = kernel.read_bytes()
    word = lambda address: struct.unpack_from(">d", data, (address - 1) * 8)[0]
    end = 17317
    count = int(word(end))
    intervals = int(word(end - 1))
    times = 1409 + count * 7
    starts = times + count + (count - 1) // 100
    epoch = [word(times + i) for i in range(count)]
    begins = {word(starts + i) for i in range(intervals)}
    covered = [(left + right) / 2 for left, right in zip(epoch, epoch[1:]) if right not in begins]
    for index in (0, len(covered) // 4, len(covered) // 2, 3 * len(covered) // 4, len(covered) - 1):
        yield covered[index]
    gap = [(left + right) / 2 for left, right in zip(epoch, epoch[1:]) if right in begins]
    if gap:
        yield gap[len(gap) // 2]


def generate(library, leapseconds, root):
    spice = ct.CDLL(str(library.resolve()))
    spice.furnsh_c.argtypes = [ct.c_char_p]
    spice.sct2e_c.argtypes = [ct.c_int, ct.c_double, ct.POINTER(ct.c_double)]
    spice.sce2c_c.argtypes = spice.sct2e_c.argtypes
    spice.ckgp_c.argtypes = [ct.c_int, ct.c_double, ct.c_double, ct.c_char_p,
                            ct.POINTER(ct.c_double), ct.POINTER(ct.c_double), ct.POINTER(ct.c_int)]
    kernel = root / "04182_04183pa_soi_itl.bc"
    for path in (leapseconds, root / "cas_v43.tf", root / "cas00172.tsc", kernel):
        spice.furnsh_c(str(path.resolve()).encode("ascii"))

    yield "# CSPICE N0067 CK type 3; CASSINI_SC_COORD to J2000 columns"
    yield f"# CK SHA256 {hashlib.sha256(kernel.read_bytes()).hexdigest()}"
    for sample in sample_ticks(kernel):
        et = ct.c_double()
        ticks = ct.c_double()
        spice.sct2e_c(-82, sample, ct.byref(et))
        spice.sce2c_c(-82, et.value, ct.byref(ticks))
        matrix = (ct.c_double * 9)()
        clock = ct.c_double()
        found = ct.c_int()
        spice.ckgp_c(-82000, ticks.value, 0, b"J2000", matrix, ct.byref(clock), ct.byref(found))
        data = (et.value, found.value, *matrix) if found.value else (et.value, 0)
        yield ",".join(format(value, ".17g") for value in data)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("library", type=Path)
    parser.add_argument("leapseconds", type=Path)
    parser.add_argument("root", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    args.output.write_text("\n".join(generate(args.library, args.leapseconds, args.root)) + "\n",
                           encoding="utf-8", newline="\n")
