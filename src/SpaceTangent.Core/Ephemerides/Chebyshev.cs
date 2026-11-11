// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;

namespace SpaceTangent.Core.Ephemerides;

internal static class Chebyshev
{
    internal static (double Value, double Derivative) Evaluate(ReadOnlySpan<byte> coefficients, double u, bool littleEndian)
    {
        double next = 0, second = 0, dNext = 0, dSecond = 0;
        for (int offset = coefficients.Length - sizeof(double); offset > 0; offset -= sizeof(double))
        {
            double current = Read(coefficients[offset..], littleEndian) + 2 * u * next - second;
            double dCurrent = 2 * next + 2 * u * dNext - dSecond;
            (next, second) = (current, next);
            (dNext, dSecond) = (dCurrent, dNext);
        }
        return (Read(coefficients, littleEndian) + u * next - second, next + u * dNext - dSecond);
    }

    private static double Read(ReadOnlySpan<byte> data, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadDoubleLittleEndian(data)
        : BinaryPrimitives.ReadDoubleBigEndian(data);
}
