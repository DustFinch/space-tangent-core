// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.Spice;

internal sealed class SpacecraftClock
{
    private readonly record struct Coefficient(double Ticks, double Seconds, double Rate);
    private readonly Coefficient[] _coefficients;
    private readonly double _ticksPerCount;
    private readonly double _lastTick;
    private readonly TimeScale _scale;

    internal SpacecraftClock(SpiceTextKernel pool, int spacecraft)
    {
        string id = Math.Abs((long)spacecraft).ToString(CultureInfo.InvariantCulture);
        if (pool.GetInteger($"SCLK_DATA_TYPE_{id}") != 1)
            throw new NotSupportedException("Expected a type 1 spacecraft clock.");
        string timeSystem = $"SCLK01_TIME_SYSTEM_{id}";
        _scale = (pool.Contains(timeSystem) ? pool.GetInteger(timeSystem) : 1) switch
        {
            1 => TimeScale.Tdb,
            2 => TimeScale.Tt,
            _ => throw new NotSupportedException("Unsupported spacecraft clock time scale.")
        };
        _ticksPerCount = 1;
        foreach (double modulus in pool.GetNumbers($"SCLK01_MODULI_{id}").Skip(1))
        {
            if (modulus <= 0 || modulus != Math.Truncate(modulus))
                throw new InvalidDataException("Invalid spacecraft clock modulus.");
            _ticksPerCount *= modulus;
        }
        double[] start = pool.GetNumbers($"SCLK_PARTITION_START_{id}");
        double[] end = pool.GetNumbers($"SCLK_PARTITION_END_{id}");
        if (start.Length != end.Length || !double.IsFinite(_ticksPerCount))
            throw new InvalidDataException("Invalid spacecraft clock partitions or moduli.");
        for (int index = 0; index < start.Length; index++)
        {
            if (end[index] < start[index])
                throw new InvalidDataException("Invalid spacecraft clock partition bounds.");
            _lastTick += end[index] - start[index];
        }

        double[] values = pool.GetNumbers($"SCLK01_COEFFICIENTS_{id}");
        if (values.Length % 3 != 0 || !double.IsFinite(_lastTick))
            throw new InvalidDataException("Invalid spacecraft clock coefficient list.");
        _coefficients = new Coefficient[values.Length / 3];
        for (int index = 0; index < _coefficients.Length; index++)
        {
            var entry = new Coefficient(values[3 * index], values[3 * index + 1], values[3 * index + 2]);
            if (entry.Rate <= 0 || (index > 0 && (entry.Seconds <= _coefficients[index - 1].Seconds ||
                                                 entry.Ticks <= _coefficients[index - 1].Ticks)))
                throw new InvalidDataException("Spacecraft clock coefficients must have positive rates and increasing epochs.");
            _coefficients[index] = entry;
        }
    }

    internal bool TryEncode(JulianInstant tdb, out double ticks)
    {
        if (tdb.Scale != TimeScale.Tdb)
            throw new ArgumentException("Spacecraft clock queries require TDB.", nameof(tdb));
        JulianInstant time = TimeConversion.Convert(tdb, _scale);
        double seconds = (time.Day - JulianInstant.J2000Day) * (double)JulianInstant.SecondsPerDay +
            time.NanosecondsOfDay / (double)JulianInstant.NanosecondsPerSecond;

        int lower = 0, upper = _coefficients.Length;
        while (lower < upper)
        {
            int middle = lower + (upper - lower) / 2;
            if (_coefficients[middle].Seconds <= seconds)
                lower = middle + 1;
            else
                upper = middle;
        }
        Coefficient entry = _coefficients[Math.Max(0, lower - 1)];
        ticks = entry.Ticks + (seconds - entry.Seconds) * _ticksPerCount / entry.Rate;
        return ticks >= 0 && ticks <= _lastTick;
    }
}
