// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class AddonDependenciesTests
{
    [Fact]
    public void ContentControlsReuseEvenWhenSizeAndTimestampAgree()
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("space-tangent-inputs-");
        try
        {
            string file = Path.Combine(root.FullName, "read.ssc");
            File.WriteAllText(file, "original");
            DateTime timestamp = File.GetLastWriteTimeUtc(file);
            var (text, inputs) = AddonDependencies.Capture(() =>
            {
                using var reader = new StreamReader(AddonDependencies.OpenRead(file, 1024));
                return reader.ReadToEnd();
            });
            Assert.Equal("original", text);
            File.SetLastWriteTimeUtc(file, timestamp.AddMinutes(1));
            File.WriteAllText(Path.Combine(root.FullName, "unused.png"), "unrelated");
            Assert.True(inputs.IsCurrent());

            File.WriteAllText(file, "modified");
            File.SetLastWriteTimeUtc(file, timestamp);
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(file));
            Assert.Equal(8, new FileInfo(file).Length);
            Assert.False(inputs.IsCurrent());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void MissingInputsAndMatchingDirectoryEntriesInvalidateTheirOwnObservations()
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("space-tangent-inputs-");
        try
        {
            string missing = Path.Combine(root.FullName, "preferred.3ds");
            string overlays = Path.Combine(root.FullName, "overrides");
            var (_, missingInputs) = AddonDependencies.Capture(() =>
            {
                Assert.False(AddonDependencies.FileExists(missing));
                Assert.Throws<FileNotFoundException>(() => AddonDependencies.OpenRead(missing, 1024));
                return AddonDependencies.DirectoryExists(overlays);
            });
            Assert.True(missingInputs.IsCurrent());
            File.WriteAllText(missing, "new preferred resource");
            Assert.False(missingInputs.IsCurrent());

            var (_, absentDirectory) = AddonDependencies.Capture(() => AddonDependencies.DirectoryExists(overlays));
            Directory.CreateDirectory(overlays);
            Assert.False(absentDirectory.IsCurrent());
            var (_, listing) = AddonDependencies.Capture(() => AddonDependencies.ListFiles(overlays, "*.ssc"));
            File.WriteAllText(Path.Combine(overlays, "unrelated.png"), "ignored");
            Assert.True(listing.IsCurrent());
            File.WriteAllText(Path.Combine(overlays, "new.ssc"), "used");
            Assert.False(listing.IsCurrent());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
