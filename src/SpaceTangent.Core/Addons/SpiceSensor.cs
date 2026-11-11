// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Core.Addons;

public sealed class SpiceSensor
{
    internal SpiceSensor(string name, SpiceSpacecraft spacecraft, string instrument, string target, double range,
        Vector3d color, double frustumOpacity, double footprintOpacity, SpiceFieldOfView fieldOfView)
    {
        Name = name;
        Spacecraft = spacecraft;
        Instrument = instrument;
        Target = target;
        RangeKilometers = range;
        Color = color;
        FrustumOpacity = frustumOpacity;
        FootprintOpacity = footprintOpacity;
        InstrumentId = fieldOfView.InstrumentId;
        BoresightInBody = fieldOfView.Boresight;
        CornersInBody = Array.AsReadOnly(fieldOfView.Corners);
    }

    public string Name { get; }
    public SpiceSpacecraft Spacecraft { get; }
    public string Instrument { get; }
    public int InstrumentId { get; }
    public string Target { get; }
    public double RangeKilometers { get; }
    public Vector3d Color { get; }
    public double FrustumOpacity { get; }
    public double FootprintOpacity { get; }
    public Vector3d BoresightInBody { get; }
    public IReadOnlyList<Vector3d> CornersInBody { get; }
}
