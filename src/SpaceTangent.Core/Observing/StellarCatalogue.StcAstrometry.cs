// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Core.Observing;

public sealed partial class StellarCatalogue
{
    private static Dictionary<ObjectId, StellarEntry> ReadStcAstrometry(string root,
        Dictionary<uint, ObjectId> missing, CancellationToken cancellation)
    {
        var result = new Dictionary<ObjectId, StellarEntry>();
        string path = Path.Combine(root, "stars.dat");
        if (missing.Count == 0 || !AddonDependencies.FileExists(path))
            return result;
        return AddonDiagnostics.Read(AddonStage.Catalog, path, Read);

        Dictionary<ObjectId, StellarEntry> Read()
        {
            const int headerBytes = 14, recordBytes = 20, maxBytes = 64 * 1024 * 1024;
            using var stream = AddonDependencies.OpenRead(LocalAddonFiles.ResolveRelative(root, root, "stars.dat"), maxBytes);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (stream.Length is < headerBytes or > maxBytes ||
                !reader.ReadBytes(8).AsSpan().SequenceEqual("CELSTARS"u8) || reader.ReadUInt16() != 0x0100)
                throw new InvalidDataException("Unsupported stellar astrometry header.");
            uint count = reader.ReadUInt32();
            if (stream.Length != headerBytes + (long)count * recordBytes)
                throw new InvalidDataException("Invalid stellar astrometry length.");

            for (uint index = 0; index < count && result.Count < missing.Count; index++)
            {
                if ((index & 4095) == 0)
                    cancellation.ThrowIfCancellationRequested();
                uint number = reader.ReadUInt32();
                var native = new Vector3d(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                double absolute = reader.ReadInt16() / 256d;
                _ = reader.ReadUInt16(); // STC supplies the overriding stellar properties.
                if (!missing.TryGetValue(number, out var id))
                    continue;
                var position = RotateArchiveVector(native) * ParsecsPerLightYear;
                double distance = position.Length;
                if (!position.IsFinite || distance is <= 0 or >= 100_000)
                    continue;
                string name = CatalogDesignation(number);
                double magnitude = absolute + 5 * Math.Log10(distance / 10);
                result.TryAdd(id, new(id, name, distance, position, default, magnitude, .65, EstimateRadius(absolute, 5772))
                {
                    AbsoluteMagnitude = absolute
                });
            }
            return result;
        }
    }
}
