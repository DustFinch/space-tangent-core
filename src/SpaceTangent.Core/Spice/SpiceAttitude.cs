// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Spice;

internal sealed class SpiceAttitude
{
    private readonly int _instrument;
    private readonly SpacecraftClock? _clock;
    private readonly Basis3d _fixedToRoot;
    private readonly CkKernel[] _kernels = [];

    internal SpiceAttitude(string frame, string baseFrame, SpiceResources resources)
    {
        if (!baseFrame.Equals("J2000", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This CK adapter expects a J2000 base frame.");

        var definitions = resources.Text;
        var (root, toRoot) = SpiceFixedFrames.Resolve(definitions, frame);
        _fixedToRoot = toRoot;
        if (root.Equals("J2000", StringComparison.OrdinalIgnoreCase))
            return;
        int frameId = definitions.GetInteger($"FRAME_{root}");
        if (definitions.GetInteger($"FRAME_{frameId}_CLASS") != 3)
            throw new NotSupportedException("This attitude requires a CK frame.");
        _instrument = definitions.GetInteger($"FRAME_{frameId}_CLASS_ID");
        string clockVariable = $"CK_{_instrument}_SCLK";
        int clock = definitions.Contains(clockVariable) ? definitions.GetInteger(clockVariable) : _instrument / 1000;
        _clock = new SpacecraftClock(definitions, clock);
        _kernels = resources.AttitudeKernels();
    }

    internal bool TrySample(JulianInstant tdb, out Basis3d attitude)
    {
        if (_clock is null)
        {
            attitude = _fixedToRoot;
            return true;
        }
        if (_clock.TryEncode(tdb, out double ticks))
        {
            for (int index = _kernels.Length - 1; index >= 0; index--)
            {
                if (_kernels[index].TryAttitude(_instrument, ticks, out var root))
                {
                    attitude = SpiceFixedFrames.Compose(root, _fixedToRoot);
                    return true;
                }
            }
        }
        attitude = default;
        return false;
    }
}
