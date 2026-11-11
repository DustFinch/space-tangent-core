// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class AddonDiagnosticsTests
{
    [Fact]
    public void MalformedOverlayReportsItsOwnFileAndLine()
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("space-tangent-diagnostic-");
        try
        {
            string primary = Path.Combine(root.FullName, "base.ssc");
            string overlay = Path.Combine(root.FullName, "override.ssc");
            File.WriteAllText(primary, "\"Test\" \"Sol/Earth\" { Class \"spacecraft\" }");
            File.WriteAllText(overlay, "Modify \"Test\" \"Sol/Earth\" {\n  Radius }\n");
            var failure = Assert.Throws<InvalidDataException>(() => SscSpiceCatalog.Load([primary, overlay], root.FullName));
            var issue = Assert.IsType<AddonDiagnostic>(AddonDiagnostics.Get(failure));
            Assert.Equal(AddonIssue.InvalidInput, issue.Code);
            Assert.Equal(AddonStage.Catalog, issue.Stage);
            Assert.Equal(overlay, issue.File);
            Assert.Equal(2, issue.Line);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void MalformedJsonRetainsTheOneBasedLineAndExceptionType()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "{\n  \"version\": ??\n}");
            Exception failure = Assert.ThrowsAny<JsonException>(() => ScienceSatelliteCatalog.Load(file, Path.GetDirectoryName(file)!));
            var issue = Assert.IsType<AddonDiagnostic>(AddonDiagnostics.Get(failure));
            Assert.Equal(AddonStage.Catalog, issue.Stage);
            Assert.Equal(file, issue.File);
            Assert.Equal(2, issue.Line);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void NestedKernelFailuresKeepTheKernelPathAndIssueCode()
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("space-tangent-diagnostic-");
        try
        {
            string catalogue = Path.Combine(root.FullName, "mission.ssc");
            string kernel = Path.Combine(root.FullName, "trajectory.bsp");
            File.WriteAllText(Path.Combine(root.FullName, "body.3ds"), "");
            File.WriteAllText(Path.Combine(root.FullName, "mission.tm"), """
                KPL/MK
                \begindata
                KERNELS_TO_LOAD = ( 'trajectory.bsp' )
                """);
            File.WriteAllText(catalogue, """
                "Test" "Sol/Saturn" {
                    Class "spacecraft" Mesh "body.3ds" Radius 1
                    SpiceOrbit { Target "-82" Origin "6" Kernel "mission.tm" }
                }
                """);
            var missing = Assert.Throws<FileNotFoundException>(() => SscSpiceCatalog.Load(catalogue, root.FullName));
            Check(missing, AddonIssue.MissingResource);
            File.WriteAllText(kernel, "invalid kernel");
            var invalid = Assert.Throws<InvalidDataException>(() => SscSpiceCatalog.Load(catalogue, root.FullName));
            Check(invalid, AddonIssue.InvalidInput);

            void Check(Exception error, AddonIssue code)
            {
                Assert.True(AddonDiagnostics.Annotate(error, AddonStage.Catalog, catalogue, source: "Mission"));
                var issue = Assert.IsType<AddonDiagnostic>(AddonDiagnostics.Get(error));
                Assert.Equal(code, issue.Code);
                Assert.Equal(AddonStage.Kernel, issue.Stage);
                Assert.Equal(kernel, issue.File);
                Assert.Equal("Mission", issue.Source);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
