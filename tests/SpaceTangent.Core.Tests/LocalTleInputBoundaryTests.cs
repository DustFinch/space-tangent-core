// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class LocalTleInputBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-tle-inputs-");
    private const string First = "1 26997U 01055A   11192.81984987 -.00000038  00000-0  10000-3 0  3699";
    private const string Second = "2 26997  66.0463 303.1838 0007562 261.9351  98.0768 12.80928955448691";
    private string Write(string source)
    {
        string path = Path.Combine(_root.FullName, "witness.tle"); File.WriteAllText(path, source); return path;
    }
    private static string Replace(string line, int start, int length, string value)
    {
        string result = line[..start] + value.PadRight(length) + line[(start + length)..68];
        int sum = result.Sum(c => char.IsAsciiDigit(c) ? c - '0' : c == '-' ? 1 : 0);
        return result + (sum % 10).ToString(CultureInfo.InvariantCulture);
    }

    [Fact]
    public void CommentOnlyDownloadsCannotReplaceAnExistingGroup()
    {
        Assert.Throws<InvalidDataException>(() => LocalTleCatalogue.ValidateGroup(Write("# no records\n\n")));
    }

    [Theory]
    [InlineData("orphan-second")]
    [InlineData("preceding-first")]
    [InlineData("interrupted-first")]
    [InlineData("final-first")]
    public void MalformedNeighborsReportTheirLineWithoutDiscardingAValidSatellite(string fault)
    {
        string valid = "Working\n" + First + "\n" + Second + "\n";
        string input = fault switch
        {
            "orphan-second" => Second + "\n" + valid,
            "preceding-first" => First + "\n" + valid,
            "interrupted-first" => First + "\nDiscarded name\n" + valid,
            _ => valid + First + "\n"
        };
        Write(input); var warnings = new List<string>();
        Assert.Equal("Working", Assert.Single(LocalTleCatalogue.Load(_root.FullName, warnings.Add)).Name);
        Assert.Contains(warnings, text => text.Contains("line", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnsupportedNewestElementsFallBackToAnOlderSupportedCandidate()
    {
        string invalid = Replace(Second, 26, 7, "9999999");
        Write("Rejected\n" + First + "\n" + invalid + "\nWorking\n" + First + "\n" + Second + "\n");
        var warnings = new List<string>(); var satellite = Assert.Single(LocalTleCatalogue.Load(_root.FullName, warnings.Add));
        Assert.Equal("Working", satellite.Name); Assert.Contains(warnings, text => text.Contains("Rejected", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => LocalTleCatalogue.ValidateGroup(Write(First + "\n" + invalid + "\n")));
        Assert.Throws<InvalidDataException>(() => LocalTleCatalogue.Load(_root.FullName));
    }

    [Fact]
    public void OverlongNamesAndExcessiveFileCountsAreRejected()
    {
        Write(new string('x', 121)); Assert.Throws<InvalidDataException>(() => LocalTleCatalogue.Load(_root.FullName));
        Write(""); for (int i = 0; i < 64; i++) File.WriteAllText(Path.Combine(_root.FullName, "extra" + i + ".tle"), "");
        Assert.Throws<InvalidDataException>(() => LocalTleCatalogue.Load(_root.FullName));
    }

    [Fact]
    public void TheRecordBudgetIsEnforcedBeforePublishingAnUnboundedCollection()
    {
        string path = Path.Combine(_root.FullName, "witness.tle");
        using (var output = new StreamWriter(path))
            for (int i = 0; i < 16385; i++)
            {
                string number = (10000 + i).ToString("D5", CultureInfo.InvariantCulture);
                output.WriteLine(Replace(First, 2, 5, number)); output.WriteLine(Replace(Second, 2, 5, number));
            }
        Assert.Throws<InvalidDataException>(() => LocalTleCatalogue.Load(_root.FullName));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
