// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionMetadataContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-mission-metadata-");
    private readonly List<string> _warnings = [];

    private static JsonObject Note(bool packed) => JsonNode.Parse(packed ? """
        {"name":"Progress","position":[0.1,0.8],"alignment":"RIGHT","size":"SMALL","color":[1,0.5,0.25],
         "lines":[{"startJdTdb":2451545,"endJdTdb":2451545.25,"line":2,"text":"Phase α","color":[0,1,0]}]}
        """ : """
        {"type":"AnnotationScript","name":"Progress","textPosition":[0.1,0.8],"textAlignment":"RIGHT","textSize":"SMALL","color":[1,0.5,0.25],
         "script":[{"startTime":2451545,"endTime":2451545.25,"lineIndex":2,"text":"Phase α","color":[0,1,0]}]}
        """)!.AsObject();

    private SampledMissionContent Load(bool packed, JsonArray annotations, JsonArray? viewpoints = null)
    {
        JsonObject document;
        if (packed)
            document = new() { ["formatVersion"] = 14, ["annotations"] = annotations, ["viewpoints"] = viewpoints ?? [] };
        else
        {
            var items = new JsonArray(annotations.Select(item => item!.DeepClone()).ToArray());
            foreach (var view in viewpoints ?? []) items.Add(view!.DeepClone());
            document = new() { ["version"] = "1.0", ["items"] = items };
        }
        File.WriteAllText(Path.Combine(_root.FullName, "metadata.json"), document.ToJsonString());
        return SampledMissionCatalog.LoadContent(["metadata.json"], _root.FullName,
            name => name == "Earth" ? SolarSystemSample.EarthId : name == "Moon" ? SolarSystemSample.MoonId : null,
            _warnings.Add);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SourceAndHistoricalMetadataRetainTextColorCoordinatesAndInclusiveWindow(bool packed)
    {
        var content = Load(packed, new(Note(packed)));
        Assert.Empty(content.Missions);
        Assert.Empty(_warnings);
        var note = Assert.Single(content.Annotations);
        Assert.Equal(("Progress", .1, .8, "right", "small"), (note.Name, note.X, note.Y, note.Alignment, note.Size));
        Assert.Equal(new Vector3d(1, .5, .25), note.Color);
        var line = Assert.Single(note.Lines);
        Assert.Equal("Phase α", line.Text);
        Assert.Equal(2, line.Line);
        Assert.Equal(new Vector3d(0, 1, 0), line.Color);
        Assert.False(line.VisibleAt(new JulianInstant(2451544, JulianInstant.NanosecondsPerDay - 1, TimeScale.Tdb)));
        Assert.True(line.VisibleAt(new(2451545, 0, TimeScale.Tdb)));
        var end = JulianInstant.FromJulianDate(2451545.25, TimeScale.Tdb);
        Assert.True(line.VisibleAt(end));
        Assert.False(line.VisibleAt(end.AddSeconds(1)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EmptyAnnotationUsesDocumentedDefaults(bool packed)
    {
        var note = new JsonObject();
        if (!packed) note["type"] = "AnnotationScript";
        var result = Assert.Single(Load(packed, new(note)).Annotations);
        Assert.Equal(("", .05, .9, "left", "medium"), (result.Name, result.X, result.Y, result.Alignment, result.Size));
        Assert.Equal(new Vector3d(1, 1, 1), result.Color);
        Assert.Empty(result.Lines);
    }

    [Theory]
    [InlineData(false, "missing-start")]
    [InlineData(true, "missing-start")]
    [InlineData(false, "reversed")]
    [InlineData(true, "reversed")]
    [InlineData(false, "row")]
    [InlineData(true, "row")]
    [InlineData(false, "text")]
    [InlineData(true, "text")]
    [InlineData(false, "position")]
    [InlineData(true, "position")]
    public void InvalidAnnotationCannotPublishPartialMetadata(bool packed, string fault)
    {
        var note = Note(packed);
        var line = note[packed ? "lines" : "script"]![0]!.AsObject();
        switch (fault)
        {
            case "missing-start": line.Remove(packed ? "startJdTdb" : "startTime"); break;
            case "reversed": line[packed ? "endJdTdb" : "endTime"] = 2451544; break;
            case "row": line[packed ? "line" : "lineIndex"] = 4097; break;
            case "text": line["text"] = new string('x', 4097); break;
            case "position": note[packed ? "position" : "textPosition"] = new JsonArray(1); break;
        }
        Assert.Throws<InvalidDataException>(() => Load(packed, new(note)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AnnotationTrackAndLineCountsAreBounded(bool packed)
    {
        var tracks = new JsonArray(Enumerable.Range(0, 129).Select(_ => (JsonNode)Note(packed)).ToArray());
        Assert.Throws<InvalidDataException>(() => Load(packed, tracks));
        var note = Note(packed);
        var first = note[packed ? "lines" : "script"]![0]!.DeepClone();
        note[packed ? "lines" : "script"] = new JsonArray(Enumerable.Range(0, 1025).Select(_ => first.DeepClone()).ToArray());
        Assert.Throws<InvalidDataException>(() => Load(packed, new(note)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ViewpointUnitsReferencesAndEquatorialUpSurviveMetadataImport(bool packed)
    {
        var view = new JsonObject { ["name"] = "Earth approach", ["center"] = "Earth", ["reference"] = "Moon", ["up"] = "equatorial" };
        if (!packed) view["type"] = "Viewpoint";
        view[packed ? "distanceKm" : "altitude"] = packed ? JsonValue.Create(12.5) : JsonValue.Create("12500 m");
        view[packed ? "azimuthDeg" : "azimuth"] = 123;
        view[packed ? "elevationDeg" : "elevation"] = -10;
        var result = Assert.Single(Load(packed, [], new(view)).Viewpoints);
        Assert.Equal((SolarSystemSample.EarthId, SolarSystemSample.MoonId, 12.5, 123d, -10d, true),
            (result.Center, result.Reference, result.AltitudeKilometers, result.AzimuthDegrees, result.ElevationDegrees, result.EquatorialUp));
        Assert.Empty(_warnings);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OneUnusableViewpointDoesNotRemoveItsValidNeighbour(bool packed)
    {
        var first = new JsonObject { ["name"] = "Unknown", ["center"] = "Missing" };
        var second = new JsonObject { ["name"] = "Default", ["center"] = "Earth" };
        if (!packed) { first["type"] = "Viewpoint"; second["type"] = "Viewpoint"; }
        var result = Assert.Single(Load(packed, [], new(first, second)).Viewpoints);
        Assert.Equal("Default", result.Name);
        Assert.Equal(100000, result.AltitudeKilometers);
        Assert.Null(result.Reference);
        Assert.False(result.EquatorialUp);
        Assert.Single(_warnings);
        Assert.Contains("Missing", _warnings[0]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ViewpointCountLimitIsCheckedBeforePublishing(bool packed)
    {
        var view = new JsonObject { ["name"] = "Earth", ["center"] = "Earth" };
        if (!packed) view["type"] = "Viewpoint";
        var rows = new JsonArray(Enumerable.Range(0, 513).Select(_ => view.DeepClone()).ToArray());
        Assert.Throws<InvalidDataException>(() => Load(packed, [], rows));
    }

    [Theory]
    [InlineData("42")] [InlineData("[null]")] [InlineData("[\"\"]")]
    public void InvalidDependencyListsCannotBeSilentlyIgnored(string require)
    {
        File.WriteAllText(Path.Combine(_root.FullName, "main.json"), "{\"version\":\"1.0\",\"require\":" + require + "}");
        Assert.Throws<InvalidDataException>(() => SampledMissionCatalog.Load("main.json", _root.FullName, _ => null));
    }

    public void Dispose() => _root.Delete(true);
}
