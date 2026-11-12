// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class ObserverEphemeridesTests
{
    [NightEphemerisFact]
    [Trait("Category", "ExternalData")]
    public void RealWinterSkyDistinguishesDarknessAltitudeAndDataCoverage()
    {
        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        const DeltaTModel model = DeltaTModel.MorrisonStephenson2004;
        var moscow = new EarthSite(55.7558, 37.6173, .15);
        var date = new DateOnly(2024, 12, 31);
        double declination = -16.7161 * Math.PI / 180, rightAscension = 101.2872 * Math.PI / 180;
        var sirius = new Vector3d(Math.Cos(declination) * Math.Cos(rightAscension), Math.Cos(declination) * Math.Sin(rightAscension),
            Math.Sin(declination));
        var report = ObserverEphemerides.Night(ephemeris, date, moscow, model,
            (_, light) => EarthObserving.HorizontalDirection(light, sirius));
        Assert.Equal(NightVisibility.Visible, report.Visibility);
        Assert.Equal(29, report.Rows.Count);
        Assert.All(report.Rows, row => Assert.Equal(TimeScale.Tdb, row.Epoch.Scale));
        var chosen = Assert.IsType<ObserverEphemerisRow>(report.BestDarkSample);
        var transit = Assert.IsType<ObserverEphemerisRow>(report.UpperCulmination);
        Assert.InRange(chosen.SunAltitudeDegrees!.Value, -90, -12);
        Assert.InRange(transit.Target!.Value.AltitudeDegrees, 17.52, 17.54);
        Assert.InRange(transit.Target.Value.AzimuthDegrees!.Value, 179.9, 180.1);
        Assert.True(transit.Target.Value.AltitudeDegrees >= chosen.Target!.Value.AltitudeDegrees);
        Assert.Equal(NightVisibility.NoDarkness, ObserverEphemerides.Night(ephemeris, new(2024, 6, 21),
            new(69.6492, 18.9553, .01), model, (_, light) => EarthObserving.HorizontalDirection(light, sirius)).Visibility);
        Assert.Equal(NightVisibility.BelowAltitude, ObserverEphemerides.Night(ephemeris, date, moscow, model,
            (_, light) => EarthObserving.HorizontalDirection(light, new(0, 0, -1))).Visibility);
        var unavailable = ObserverEphemerides.Night(ephemeris, date, moscow, model, (_, _) => null);
        Assert.Equal(NightVisibility.NoEphemeris, unavailable.Visibility);
        Assert.Null(unavailable.BestDarkSample);
        Assert.Null(unavailable.UpperCulmination);

        using var writer = new StringWriter(CultureInfo.GetCultureInfo("ru-RU"));
        ObserverEphemerides.WriteCsv(writer, report);
        string[] lines = writer.ToString().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(30, lines.Length);
        Assert.Equal("jd_tdb,sun_altitude_deg,target_altitude_deg,target_azimuth_deg", lines[0]);
        Assert.All(lines.Skip(1), row => Assert.All(row.Split(','), field =>
            Assert.True(double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out _))));
    }

    private sealed class NightEphemerisFactAttribute : FactAttribute
    {
        public NightEphemerisFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                Skip = "Set the original DE440 ephemeris file.";
        }
    }
}
