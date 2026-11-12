// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Spice;

internal sealed class SpiceAttitude
{
    private readonly SpiceFramePool _frames;
    private readonly int _frame;

    internal SpiceAttitude(string frame, string baseFrame, SpiceResources resources)
    {
        if (!baseFrame.Equals("J2000", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("SPICE attitudes use J2000 output axes.");
        _frames = resources.Frames;
        _frame = _frames.Id(frame);
        if (_frame == 0)
            throw new InvalidDataException($"Unknown SPICE orientation frame {frame}.");
        _frames.RequireDefinition(_frame);
    }

    internal bool TrySample(JulianInstant tdb, out FrameKinematics attitude) =>
        _frames.TrySample(_frame, tdb, false, out attitude);
}
