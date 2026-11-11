// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Spice;

internal sealed class SpiceAttitude
{
    private readonly int _instrument;
    private readonly SpacecraftClock _clock;
    private readonly CkKernel[] _kernels;

    internal SpiceAttitude(string frame, string baseFrame, IReadOnlyList<string> files)
    {
        if (!baseFrame.Equals("J2000", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This CK adapter expects a J2000 base frame.");

        var definitions = new SpiceTextKernel();
        foreach (string file in files)
        {
            string extension = Path.GetExtension(file);
            if (extension.Equals(".tf", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tsc", StringComparison.OrdinalIgnoreCase))
                definitions.Furnish(file);
        }

        int frameId = definitions.GetInteger($"FRAME_{frame}");
        if (definitions.GetInteger($"FRAME_{frameId}_CLASS") != 3)
            throw new NotSupportedException("This attitude requires a CK frame.");
        _instrument = definitions.GetInteger($"FRAME_{frameId}_CLASS_ID");
        string clockVariable = $"CK_{_instrument}_SCLK";
        int clock = definitions.Contains(clockVariable) ? definitions.GetInteger(clockVariable) : _instrument / 1000;
        _clock = new SpacecraftClock(definitions, clock);
        _kernels = files.Where(file => Path.GetExtension(file).Equals(".bc", StringComparison.OrdinalIgnoreCase))
            .Select(file => new CkKernel(file)).ToArray();
        if (_kernels.Length == 0)
            throw new InvalidDataException("SPICE attitude has no CK file.");
    }

    internal bool TrySample(JulianInstant tdb, out Basis3d attitude)
    {
        if (_clock.TryEncode(tdb, out double ticks))
        {
            for (int index = _kernels.Length - 1; index >= 0; index--)
            {
                if (_kernels[index].TryAttitude(_instrument, ticks, out attitude))
                    return true;
            }
        }
        attitude = default;
        return false;
    }
}
