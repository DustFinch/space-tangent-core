// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Addons;

public sealed record SscScriptRequest(string Root, string ModulePath, string Function,
    IReadOnlyDictionary<string, object> Arguments);

public sealed record SscScriptOrbit(Func<JulianInstant, Vector3d?> Position);
public readonly record struct SscScriptQuaternion(double W, double X, double Y, double Z);
public sealed record SscScriptRotation(Func<JulianInstant, SscScriptQuaternion?> Orientation);

public interface ISscScriptModels
{
    SscScriptOrbit? CreateOrbit(SscScriptRequest request);
    SscScriptRotation? CreateRotation(SscScriptRequest request);
}

public static partial class SscBodyCatalogue
{
    private static SscScriptRequest ScriptRequest(SscDefinition body, string field, string root, SscResource resolve)
    {
        var definition = body.Properties.Object(field);
        string module = definition.Text("Module");
        string function = definition.Text("Function");
        if (string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(function) || module.Length > 256 || function.Length > 128)
            throw new InvalidDataException($"Invalid {field} module or function.");
        string source = module.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ? module : module.Replace('.', '/') + ".lua";
        string path = resolve(body, field, AddonStage.Catalog, source, "scripts");
        var arguments = Map(definition);
        return new(root, path, function, arguments);

        static IReadOnlyDictionary<string, object> Map(SscValue.Map block) =>
            new ReadOnlyDictionary<string, object>(block.Fields.ToDictionary(pair => pair.Key,
                pair => ConvertValue(pair.Value), StringComparer.Ordinal));

        static object ConvertValue(SscValue field) => field switch
        {
            SscValue.Atom { Quoted: false, Text: "true" } => true,
            SscValue.Atom { Quoted: false, Text: "false" } => false,
            SscValue.Atom { Quoted: false } atom when double.TryParse(atom.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) => number,
            SscValue.Atom atom => atom.Text,
            SscValue.Map map => Map(map),
            SscValue.List list => Array.AsReadOnly(list.Items.Select(ConvertValue).ToArray()),
            _ => throw new InvalidDataException("Unsupported script argument.")
        };
    }

    private static Func<JulianInstant, CartesianState?> ScriptOrbit(SscScriptOrbit orbit)
    {
        const double step = 60;
        return epoch =>
        {
            if (orbit.Position(epoch) is not { } position || !position.IsFinite)
                return null;
            if (orbit.Position(epoch.AddSeconds(step)) is { IsFinite: true } later)
                return new(position, (later - position) / step);
            return orbit.Position(epoch.AddSeconds(-step)) is { IsFinite: true } before
                ? new(position, (position - before) / step) : null;
        };
    }

    private static Func<JulianInstant, FrameKinematics?> ScriptRotation(SscScriptRotation model, Basis3d reference) =>
        epoch => model.Orientation(epoch) is { } quaternion ? ResolveRotation(new(quaternion.W, quaternion.X, quaternion.Y, quaternion.Z), reference) : null;

    private static FrameKinematics ResolveRotation(RigidQuaternion rotation, Basis3d reference)
    {
        var native = rotation.Normalize().InverseBasis();
        Vector3d Axis(Vector3d value) => reference.Transform(new(value.X, -value.Z, value.Y));
        return new(new(Axis(native.X), Axis(native.Z * -1), Axis(native.Y)), null);
    }

    private static Func<JulianInstant, FrameKinematics?> ReadSampledSpin(string file, Basis3d reference,
        CancellationToken cancellation)
    {
        var samples = SampledMissionAttitude.ReadSsc(file, cancellation);
        return epoch =>
        {
            var local = samples.Sample(epoch);
            return new FrameKinematics(SampledMissionCatalog.Multiply(reference, local.ToWorld),
                local.AngularVelocityRadiansPerSecond is { } rate ? reference.Transform(rate) : null);
        };
    }
}
