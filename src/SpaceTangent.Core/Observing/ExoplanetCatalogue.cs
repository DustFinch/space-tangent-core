// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Core.Observing;

public sealed class ExoplanetEntry
{
    private readonly StellarConic _orbit;
    private readonly double _spinHours, _tilt, _node, _meridian, _spinEpoch;

    internal ExoplanetEntry(ObjectId id, ObjectId host, ObjectId parent, IReadOnlyList<string> names,
        double radius, string texture, double mass, double albedo, StellarConic orbit,
        double spinHours, double tilt, double node, double meridian, double spinEpoch)
    {
        Id = id;
        Host = host;
        Parent = parent;
        Names = names;
        RadiusKilometers = radius;
        TextureClass = texture;
        MassEarths = mass;
        GeometricAlbedo = albedo;
        _orbit = orbit;
        (_spinHours, _tilt, _node, _meridian, _spinEpoch) = (spinHours, tilt, node, meridian, spinEpoch);
    }

    public ObjectId Id { get; }
    public ObjectId Host { get; }
    public ObjectId Parent { get; }
    public string Name => Names[0];
    public IReadOnlyList<string> Names { get; }
    public double RadiusKilometers { get; }
    public string TextureClass { get; }
    public double MassEarths { get; }
    public double GeometricAlbedo { get; }

    /// <summary>The catalogue orbit's semimajor axis, in kilometers.</summary>
    public double SemiMajorAxisKilometers =>
        _orbit.Curve.Elements.PeriapsisKilometers / (1 - _orbit.Curve.Elements.Eccentricity);

    public CartesianState ParentRelativeState(JulianInstant epoch)
    {
        var state = _orbit.Evaluate(epoch);
        return new(HistoricalSolarEphemeris.Rotate(state.PositionKilometers),
            HistoricalSolarEphemeris.Rotate(state.VelocityKilometersPerSecond));
    }

    public Basis3d BodyToIcrf(JulianInstant epoch)
    {
        if (epoch.Scale != TimeScale.Tdb)
            throw new ArgumentException("An exoplanet rotation requires TDB.", nameof(epoch));
        double days = (epoch.Day - _spinEpoch) + epoch.NanosecondsOfDay / (double)JulianInstant.NanosecondsPerDay;
        double spin = _meridian + (_spinHours == 0 ? 0 : Math.IEEERemainder(days * 24 / _spinHours, 1) * Math.Tau);
        var (sinSpin, cosSpin) = Math.SinCos(spin);
        var (sinTilt, cosTilt) = Math.SinCos(_tilt);
        var (sinNode, cosNode) = Math.SinCos(_node);
        return new(Column(new(1, 0, 0)), Column(new(0, 1, 0)), Column(new(0, 0, 1)));

        Vector3d Column(Vector3d value)
        {
            double x = value.X * cosSpin - value.Y * sinSpin, y = value.X * sinSpin + value.Y * cosSpin;
            double tiltedY = y * cosTilt - value.Z * sinTilt, tiltedZ = y * sinTilt + value.Z * cosTilt;
            return HistoricalSolarEphemeris.Rotate(new(x * cosNode - tiltedY * sinNode, x * sinNode + tiltedY * cosNode, tiltedZ));
        }
    }
}

/// <summary>Local catalogued exoplanets and the embedded positions of stars that host them.</summary>
public sealed class ExoplanetCatalogue
{
    private const int MaximumBodies = 100_000;
    private const int MaximumBytes = 32 * 1024 * 1024;
    private const double Degrees = Math.PI / 180;
    private static readonly Regex NonId = new("[^a-z0-9]+", RegexOptions.CultureInvariant);
    private readonly Dictionary<ObjectId, ExoplanetEntry> _bodies;
    private readonly Dictionary<ObjectId, StellarEntry> _hosts;
    private readonly ILookup<ObjectId, ExoplanetEntry> _systems;
    private readonly IReadOnlyDictionary<ObjectId, ObjectId> _canonicalHosts;

    private ExoplanetCatalogue(Dictionary<ObjectId, ExoplanetEntry> bodies, Dictionary<ObjectId, StellarEntry> hosts,
        IReadOnlyDictionary<ObjectId, ObjectId>? canonicalHosts = null)
    {
        _bodies = bodies;
        _hosts = hosts;
        Bodies = Array.AsReadOnly(bodies.Values.ToArray());
        Hosts = Array.AsReadOnly(hosts.Values.ToArray());
        _canonicalHosts = canonicalHosts ?? new Dictionary<ObjectId, ObjectId>();
        _systems = Bodies.ToLookup(body => CanonicalHost(body.Host));
    }

    public static ExoplanetCatalogue Empty { get; } = new([], []);
    public IReadOnlyList<ExoplanetEntry> Bodies { get; }
    public IReadOnlyList<StellarEntry> Hosts { get; }
    public ExoplanetEntry? Find(ObjectId id) => _bodies.GetValueOrDefault(id);
    public StellarEntry? FindHost(ObjectId id) => _hosts.GetValueOrDefault(id);
    public ObjectId CanonicalHost(ObjectId host) => _canonicalHosts.GetValueOrDefault(host, host);
    public IEnumerable<ExoplanetEntry> BodiesOf(ObjectId host) => _systems[CanonicalHost(host)];

    /// <summary>Links embedded hosts to positioned stellar entries without changing the published planet identifiers.</summary>
    public ExoplanetCatalogue WithStellarHosts(StellarCatalogue stars)
    {
        if (_hosts.Count == 0 || stars.Stars.Count == 0)
            return this;
        var linked = new Dictionary<ObjectId, ObjectId>();
        foreach (var host in _hosts.Values)
        {
            if (stars.Find(host.Id) is not null || stars.FindByName(host.Name) is not { IsBarycenter: false } candidate)
                continue;
            // Match an exact catalogue name and check the position before using the primary star.
            if ((candidate.PositionParsecs - host.PositionParsecs).Length <= .002 * host.DistanceParsecs)
                linked.Add(host.Id, candidate.Id);
        }
        return linked.Count == 0 ? this : new(_bodies, _hosts, linked);
    }

    public static ExoplanetCatalogue Load(string path, CancellationToken cancellation = default)
    {
        using var input = AddonDependencies.OpenRead(path, MaximumBytes);
        if (input.Length > MaximumBytes)
            throw new InvalidDataException("The exoplanet catalogue exceeds 32 MiB.");
        using var reader = new StreamReader(input, new UTF8Encoding(false, true));
        var bodies = new Dictionary<ObjectId, ExoplanetEntry>();
        var hosts = new Dictionary<ObjectId, StellarEntry>();
        var values = new double[25];
        for (int line = 1; reader.ReadLine() is { } text; line++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('#'))
                continue;
            string[] fields = text.Split('\t');
            if (fields.Length != values.Length || bodies.Count == MaximumBodies ||
                !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint number) || number == 0)
                throw Error(line);
            for (int i = 3; i < values.Length; i++)
                if (i != 9 && (!double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                    !double.IsFinite(values[i])))
                    throw Error(line);
            string[] names = fields[0].Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string texture = fields[9];
            if (names.Length == 0 || string.IsNullOrWhiteSpace(fields[2]) || texture.Length is < 1 or > 32 ||
                texture.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') ||
                values[7] <= 0 || values[8] <= 0 || values[10] < 0 || values[11] is < 0 or > 1)
                throw Error(line);
            ObjectId hostId = StellarCatalogue.CatalogId(number);
            if (!hosts.ContainsKey(hostId))
                hosts.Add(hostId, Host(hostId, fields[2], number, values));
            ObjectId id = BodyId(number, names[0]);
            StellarConic orbit = StellarCatalogue.StellarOrbit(values[13], values[14], values[12] * 86400,
                values[15], values[16], values[17], values[18], values[19]);
            if (!bodies.TryAdd(id, new(id, hostId, hostId, Array.AsReadOnly(names), values[8], texture, values[10], values[11],
                orbit, values[20], values[21] * Degrees, values[22] * Degrees, values[23] * Degrees, values[24])))
                throw Error(line);
        }
        return new(bodies, hosts);
    }

    private static StellarEntry Host(ObjectId id, string name, uint number, double[] values)
    {
        double ratio = StellarEntry.KilometersPerLightYear / StellarEntry.KilometersPerParsec;
        Vector3d position = HistoricalSolarEphemeris.Rotate(new(values[3], -values[5], values[4])) * ratio;
        double distance = position.Length;
        if (!(distance > 0 && double.IsFinite(distance)))
            throw new InvalidDataException($"The exoplanet host {name} has an invalid position.");
        double absolute = values[6], temperature = values[7];
        double color = Math.Clamp(.65 + 1.45 * (5772 / temperature - 1), -.4, 2.5);
        return new(id, name, distance, position, default, absolute + 5 * Math.Log10(distance / 10), color,
            StellarCatalogue.EstimateRadius(absolute, temperature))
        {
            AbsoluteMagnitude = absolute,
            Aliases = [StellarCatalogue.CatalogDesignation(number)]
        };
    }

    private static ObjectId BodyId(uint host, string name)
    {
        string slug = NonId.Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length is 0 or > 80)
            slug = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16].ToLowerInvariant();
        return new(FormattableString.Invariant($"exo/{host}/{slug}"));
    }

    private static InvalidDataException Error(int line) => new($"Invalid exoplanet catalogue record on line {line}.");
}
