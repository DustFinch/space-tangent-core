// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;

namespace SpaceTangent.Core.Observing;

public sealed record PanoramaPixels(int Width, int Height, byte[] Rgba);

public static class PanoramaBaker
{
    private const double Radians = Math.PI / 180;
    private readonly record struct Pixel(double R, double G, double B, double A)
    {
        public static Pixel operator *(Pixel pixel, double weight) =>
            new(pixel.R * weight, pixel.G * weight, pixel.B * weight, pixel.A * weight);
        public static Pixel operator +(Pixel a, Pixel b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);
    }

    private readonly record struct Panel(int Texture, double Left, double Bottom, double Right, double Top);

    public static PanoramaPixels? Bake(ObservingLandscape landscape, LandscapeLayer layer,
        Func<string, PanoramaPixels?> image, Func<string, string?> text, int width = 2048, int height = 1024,
        CancellationToken cancellation = default)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(width));
        var sample = landscape.Projection switch
        {
            LandscapeProjection.Panels when layer == LandscapeLayer.Fog => PanelFog(landscape, image),
            LandscapeProjection.Panels => Panels(landscape, layer, image),
            LandscapeProjection.Fisheye => Fisheye(landscape, layer, image),
            LandscapeProjection.Polygonal when layer == LandscapeLayer.Ground => Polygon(landscape, text),
            LandscapeProjection.Polygonal => null,
            _ => throw new ArgumentException("A spherical landscape uses its original textures.", nameof(landscape))
        };
        if (sample is null)
            return null;
        byte[] rgba = new byte[width * height * 4];
        Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellation }, y =>
        {
            double altitude = 90 - 180.0 * (y + .5) / height;
            for (int x = 0; x < width; x++)
            {
                double azimuth = 90 + 360.0 * (x + .5) / width;
                Pixel pixel = sample(azimuth, altitude);
                int i = 4 * (y * width + x);
                rgba[i] = Byte(pixel.R);
                rgba[i + 1] = Byte(pixel.G);
                rgba[i + 2] = Byte(pixel.B);
                rgba[i + 3] = Byte(pixel.A);
            }
        });
        return new(width, height, rgba);
    }

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
    private static double Wrap(double value) => ObservingLandscape.Wrap(value);

    private static Func<double, double, Pixel>? Fisheye(ObservingLandscape landscape, LandscapeLayer layer,
        Func<string, PanoramaPixels?> image)
    {
        if (landscape.File(layer) is not { } file)
            return null;
        var picture = image(file);
        if (picture is null)
            return null;
        double field = landscape.Number("texturefov", 360);
        double rotation = landscape.Number("angle_rotatez", 0);
        if (field is <= 0 or > 360)
            throw new InvalidDataException($"{landscape.Id} has an invalid fisheye field.");
        return (azimuth, altitude) =>
        {
            var (sine, cosine) = Math.SinCos((azimuth - 90 - rotation) * Radians);
            double radius = Math.Min((90 - altitude) / field, .5);
            return Sample(picture, .5 + radius * cosine, .5 + radius * sine);
        };
    }

    private static Func<double, double, Pixel>? Panels(ObservingLandscape landscape, LandscapeLayer layer,
        Func<string, PanoramaPixels?> image)
    {
        int sides = landscape.Count("nbside", 0), repeat = landscape.Count("nb_decor_repeat", 1);
        bool lights = layer == LandscapeLayer.Lights;
        int textureCount = landscape.Count("nbsidetex", 0);
        var pictures = new PanoramaPixels?[textureCount];
        for (int i = 0; i < textureCount; i++)
        {
            string? name = landscape.Value((lights ? "light" : "tex") + i);
            pictures[i] = name is null ? null : image(name);
            if (!lights && pictures[i] is null)
                throw new InvalidDataException($"{landscape.Id} is missing panel texture {name ?? i.ToString(CultureInfo.InvariantCulture)}.");
        }
        if (lights && pictures.All(picture => picture is null))
            return null;
        Panel[] panels = Enumerable.Range(0, sides).Select(ReadPanel).ToArray();
        double bottom = landscape.Number("decor_angle_shift", 0);
        double height = landscape.Number("decor_alt_angle", 0);
        double rotation = landscape.Number("decor_angle_rotatez", 0);
        bool calibrated = landscape.Flag("calibrated");
        bool tangent = landscape.Flag("tan_mode");
        var panelRise = Rise(bottom, height, calibrated, tangent);
        double groundAngle = landscape.Number("ground_angle_shift", 0) * Radians;
        double groundScale = calibrated || tangent ? Math.Tan(groundAngle) : Math.Sin(groundAngle);
        double groundRotation = landscape.Number("ground_angle_rotatez", 0);
        var ground = !lights && landscape.Value("groundtex") is { } path ? image(path) : null;
        bool groundFirst = landscape.Flag("draw_ground_first");
        double slotWidth = 360.0 / (sides * repeat);
        return (azimuth, altitude) =>
        {
            double around = Wrap(azimuth - 90 - rotation) / slotWidth;
            int slot = Math.Min((int)around, sides * repeat - 1);
            var panel = panels[slot % sides];
            double across = around - slot;
            double up = panelRise(altitude);
            Pixel decoration = default, floor = default;
            if (up is >= 0 and <= 1 && pictures[panel.Texture] is { } picture)
                decoration = Sample(picture, Lerp(panel.Left, panel.Right, across), Lerp(panel.Bottom, panel.Top, up));
            if (ground is not null && altitude < 0)
            {
                double radius = groundScale / Math.Tan(altitude * Radians);
                if (Math.Abs(radius) <= 1)
                {
                    var (sine, cosine) = Math.SinCos((azimuth - 90 + groundRotation) * Radians);
                    floor = Sample(ground, .5 + radius * sine / 2, .5 + radius * cosine / 2);
                }
            }
            return groundFirst ? Over(decoration, floor) : Over(floor, decoration);
        };

        Panel ReadPanel(int i)
        {
            string[] fields = (landscape.Value($"side{i}") ?? "tex0:0:0:1:1").Split(':');
            if (fields.Length > 5 || !fields[0].StartsWith("tex", StringComparison.Ordinal) ||
                !int.TryParse(fields[0].AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int slot) ||
                slot >= textureCount)
                throw new InvalidDataException($"{landscape.Id} has an invalid side{i} texture reference.");
            return new(slot, Coordinate(1, 0), Coordinate(2, 0), Coordinate(3, 1), Coordinate(4, 1));

            double Coordinate(int index, double fallback) =>
                index < fields.Length ? ObservingLandscape.ParseNumber(fields[index]) : fallback;
        }
    }

    private static Func<double, double, Pixel>? PanelFog(ObservingLandscape landscape, Func<string, PanoramaPixels?> image)
    {
        if (landscape.Value("fogtex") is not { } file || image(file) is not { } picture)
            return null;
        bool calibrated = landscape.Flag("calibrated");
        var rise = Rise(landscape.Number("fog_angle_shift", 0), landscape.Number("fog_alt_angle", 0),
            calibrated, landscape.Flag("tan_mode"), forceTangent: true);
        double rotation = calibrated ? landscape.Number("decor_angle_rotatez", 0) : 0;
        return (azimuth, altitude) =>
        {
            double row = rise(altitude);
            return row is >= 0 and <= 1 ? Sample(picture, Wrap(azimuth - 90 - rotation) / 360, row) : default;
        };
    }

    private static Func<double, double> Rise(double bottom, double height, bool calibrated, bool tangent, bool forceTangent = false)
    {
        if (calibrated && !tangent && !forceTangent)
            return degrees => (degrees - bottom) / height;
        double low = calibrated || tangent ? Math.Tan(bottom * Radians) : Math.Sin(bottom * Radians);
        double span = calibrated ? Math.Tan((bottom + height) * Radians) - low :
            tangent ? Math.Tan(height * Radians) : Math.Sin(height * Radians);
        return degrees => degrees is > -89.9 and < 89.9 ? (Math.Tan(degrees * Radians) - low) / span : -1;
    }

    private static Func<double, double, Pixel>? Polygon(ObservingLandscape landscape, Func<string, string?> text)
    {
        if (landscape.Value("polygonal_horizon_list") is not { } file || text(file) is not { } list)
            return null;
        var horizon = ReadHorizon(list, landscape.Number("polygonal_angle_rotatez", 0),
            landscape.Value("polygonal_horizon_list_mode") ?? "azDeg_altDeg");
        double[] sampled = Enumerable.Range(0, 3600).Select(i => HorizonAltitude(horizon, i / 10.0)).ToArray();
        Pixel ground = Color(landscape.Value("ground_color") ?? "0,0,0");
        Pixel? line = landscape.Value("horizon_line_color") is { } value ? Color(value) : null;
        return (azimuth, altitude) =>
        {
            double sample = Wrap(azimuth) * 10;
            int i = (int)sample % sampled.Length;
            double threshold = Lerp(sampled[i], sampled[(i + 1) % sampled.Length], sample - Math.Floor(sample));
            if (line is { } stroke && Math.Abs(altitude - threshold) < .12)
                return stroke;
            return altitude < threshold ? ground : default;
        };
    }

    public static IReadOnlyList<(double Azimuth, double Altitude)> ReadHorizon(string text, double rotation, string mode)
    {
        double unit = mode switch
        {
            "azDeg_altDeg" or "azDeg_zdDeg" => 1,
            "azRad_altRad" or "azRad_zdRad" => 1 / Radians,
            "azGrad_altGrad" or "azGrad_zdGrad" => .9,
            _ => throw new InvalidDataException($"Unknown horizon coordinate mode '{mode}'.")
        };
        bool zenithDistance = mode.Contains("_zd", StringComparison.Ordinal);
        var sorted = new SortedDictionary<double, double>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            string row = raw.Trim();
            if (row.Length == 0 || row[0] is '#' or ';')
                continue;
            var fields = row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
                throw new InvalidDataException("Horizon point needs two coordinates.");
            double azimuth = ObservingLandscape.ParseNumber(fields[0]) * unit;
            double elevation = ObservingLandscape.ParseNumber(fields[1]) * unit;
            if (zenithDistance)
                elevation = 90 - elevation;
            if (elevation is < -90 or > 90)
                throw new InvalidDataException("Horizon elevation must be in [-90, 90].");
            sorted[Wrap(azimuth + rotation)] = elevation;
        }
        if (sorted.Count == 0)
            throw new InvalidDataException("The horizon has no points.");
        return Array.AsReadOnly(sorted.Select(item => (item.Key, item.Value)).ToArray());
    }

    public static double HorizonAltitude(IReadOnlyList<(double Azimuth, double Altitude)> horizon, double azimuth)
    {
        azimuth = Wrap(azimuth);
        int low = 0, high = horizon.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (horizon[middle].Azimuth < azimuth) low = middle + 1;
            else high = middle;
        }
        var next = horizon[low % horizon.Count];
        var previous = horizon[(low + horizon.Count - 1) % horizon.Count];
        double span = Wrap(next.Azimuth - previous.Azimuth);
        return span <= 1e-9 ? next.Altitude : Lerp(previous.Altitude, next.Altitude, Wrap(azimuth - previous.Azimuth) / span);
    }

    private static Pixel Sample(PanoramaPixels image, double u, double bottomUp)
    {
        double x = u * image.Width - .5, y = (1 - bottomUp) * image.Height - .5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        Pixel top = Premultiplied(x0, y0) * (1 - fx) + Premultiplied(x0 + 1, y0) * fx;
        Pixel bottom = Premultiplied(x0, y0 + 1) * (1 - fx) + Premultiplied(x0 + 1, y0 + 1) * fx;
        var sample = top * (1 - fy) + bottom * fy;
        return sample.A > 0 ? new(sample.R / sample.A, sample.G / sample.A, sample.B / sample.A, sample.A) : default;

        Pixel Premultiplied(int column, int row)
        {
            int px = Math.Clamp(column, 0, image.Width - 1), py = Math.Clamp(row, 0, image.Height - 1);
            int i = 4 * (py * image.Width + px);
            double alpha = image.Rgba[i + 3] / 255.0;
            return new(image.Rgba[i] * alpha / 255, image.Rgba[i + 1] * alpha / 255, image.Rgba[i + 2] * alpha / 255, alpha);
        }
    }

    private static Pixel Over(Pixel top, Pixel bottom)
    {
        double underneath = bottom.A * (1 - top.A);
        double alpha = top.A + underneath;
        return alpha <= 0 ? default : new((top.R * top.A + bottom.R * underneath) / alpha,
            (top.G * top.A + bottom.G * underneath) / alpha, (top.B * top.A + bottom.B * underneath) / alpha, alpha);
    }

    private static Pixel Color(string value)
    {
        var components = value.Split(',').Select(ObservingLandscape.ParseNumber).ToArray();
        if (components.Length != 3 || components.Any(channel => channel is < 0 or > 1))
            throw new InvalidDataException("A horizon color needs three channels in [0, 1].");
        return new(components[0], components[1], components[2], 1);
    }

    private static double Lerp(double first, double second, double fraction) => first + (second - first) * fraction;
}
