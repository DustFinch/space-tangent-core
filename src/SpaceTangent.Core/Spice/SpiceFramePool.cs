// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Spice;

/// <summary>Resolves inertial, text PCK, fixed FK and CK frames in one ordered local kernel pool.</summary>
internal sealed class SpiceFramePool(SpiceTextKernel definitions, Func<CkKernel[]> kernels)
{
    private const int MaximumFrameDepth = 64;
    private readonly ConcurrentDictionary<int, FrameModel> _frames = new();

    private abstract record FrameModel
    {
        internal sealed record Missing : FrameModel;
        internal sealed record Inertial(Basis3d Basis) : FrameModel;
        internal sealed record Fixed(int Parent, Basis3d ToParent) : FrameModel;
        internal sealed record Body(SpicePolynomialBodyFrame Rotation) : FrameModel;
        internal sealed record Attitude(int Instrument, SpacecraftClock Clock) : FrameModel;
    }

    internal int Id(string name)
    {
        int id = SpiceReferenceFrames.Id(name);
        if (id != 0)
            return id;
        if (name.Equals("EARTH_FIXED", StringComparison.OrdinalIgnoreCase))
            return 10081;
        if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            return id;
        string key = "FRAME_" + name;
        return definitions.Contains(key) ? definitions.GetInteger(key) : 0;
    }

    internal void RequireDefinition(int id)
    {
        for (int depth = 0; depth < MaximumFrameDepth; depth++)
        {
            switch (_frames.GetOrAdd(id, Compile))
            {
                case FrameModel.Missing:
                    throw new InvalidDataException($"Unavailable SPICE reference frame {id}.");
                case FrameModel.Fixed link:
                    id = link.Parent;
                    continue;
                case FrameModel.Attitude when kernels().Length == 0:
                    throw new InvalidDataException("SPICE attitude has no CK file.");
                default:
                    return;
            }
        }
        throw new InvalidDataException("The fixed SPICE frame chain exceeds its depth limit or contains a cycle.");
    }

    internal bool TrySample(int id, JulianInstant epoch, bool needRates, out FrameKinematics frame)
    {
        if (epoch.Scale != TimeScale.Tdb)
            throw new ArgumentException("SPICE frames require a TDB epoch.", nameof(epoch));
        return Sample(id, epoch, needRates, 0, out frame);
    }

    private bool Sample(int id, JulianInstant epoch, bool needRates, int depth, out FrameKinematics frame)
    {
        if (depth < MaximumFrameDepth)
            switch (_frames.GetOrAdd(id, Compile))
            {
                case FrameModel.Inertial inertial:
                    frame = new(inertial.Basis, default(Vector3d));
                    return true;
                case FrameModel.Body body:
                    frame = body.Rotation.Sample(epoch);
                    return true;
                case FrameModel.Fixed fixedFrame when Sample(fixedFrame.Parent, epoch, needRates, depth + 1, out var parent):
                    frame = new(SpiceFixedFrames.Compose(parent.ToWorld, fixedFrame.ToParent),
                        parent.AngularVelocityRadiansPerSecond);
                    return true;
                case FrameModel.Attitude attitude when attitude.Clock.TryEncode(epoch, out double ticks):
                    var available = kernels();
                    for (int index = available.Length - 1; index >= 0; index--)
                    {
                        if (!available[index].TryPointing(attitude.Instrument, ticks, needRates, out var point))
                            continue;
                        if (!Sample(point.Reference, epoch, needRates, depth + 1, out var reference))
                            break;
                        Vector3d? velocity = point.AngularVelocity is { } local &&
                            reference.AngularVelocityRadiansPerSecond is { } parentRate
                            ? parentRate + reference.ToWorld.Transform(local) : null;
                        frame = new(SpiceFixedFrames.Compose(reference.ToWorld, point.ToReference), velocity);
                        return true;
                    }
                    break;
            }
        frame = default;
        return false;
    }

    private FrameModel Compile(int id)
    {
        if (SpiceReferenceFrames.InertialBasis(id) is { } basis)
            return new FrameModel.Inertial(basis);
        if (SpiceReferenceFrames.Body(id).Body is > 0 and var body)
            return Pck(body);
        if (id == 10081)
        {
            string earth = SpiceFixedFrames.Key(definitions, id, id);
            return definitions.Contains(earth + "_SPEC") ? ReadFixed(earth) :
                new FrameModel.Fixed(10013, Basis3d.Identity);
        }

        string key = FormattableString.Invariant($"FRAME_{id}");
        if (!definitions.Contains(key + "_CLASS") || !definitions.Contains(key + "_CLASS_ID"))
            return new FrameModel.Missing();
        int kind = definitions.GetInteger(key + "_CLASS"), classId = definitions.GetInteger(key + "_CLASS_ID");
        switch (kind)
        {
            case 2:
                return Pck(classId);
            case 3:
                string clockKey = FormattableString.Invariant($"CK_{classId}_SCLK");
                int clock = definitions.Contains(clockKey) ? definitions.GetInteger(clockKey) : classId / 1000;
                return new FrameModel.Attitude(classId, new(definitions, clock));
            case 4:
                return ReadFixed(SpiceFixedFrames.Key(definitions, id, classId));
            default:
                return new FrameModel.Missing();
        }
    }

    private FrameModel ReadFixed(string key)
    {
        int parent = Id(SpiceFixedFrames.Parent(definitions, key));
        return parent == 0 ? new FrameModel.Missing() : new FrameModel.Fixed(parent, SpiceFixedFrames.Transform(definitions, key));
    }

    private FrameModel Pck(int body)
    {
        string key = FormattableString.Invariant($"BODY{body}");
        if (!definitions.Contains(key + "_POLE_RA") || !definitions.Contains(key + "_POLE_DEC") || !definitions.Contains(key + "_PM"))
            return new FrameModel.Missing();
        return new FrameModel.Body(new(definitions, body, requireRadii: false));
    }
}
