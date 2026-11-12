// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Core.Observing;

/// <summary>
/// The colour and luminance of a blackbody: Planck's law integrated against the CIE 1931 2-degree colour matching
/// functions in the multi-lobe Gaussian fit of Wyman, Sloan and Shirley (2013, JCGT 2(2)), 380-780 nm in 1 nm steps,
/// then XYZ to linear sRGB (IEC 61966-2-1, D65). Colours outside the sRGB gamut (below about 1900 K) are clipped.
/// </summary>
public static class BlackbodyColor
{
    private static double Lobe(double lambda, double mean, double below, double above)
    {
        double t = (lambda - mean) / (lambda < mean ? below : above);
        return Math.Exp(-.5 * t * t);
    }
    public static (double X, double Y, double Z) MatchingFunctions(double nanometres) => (
        1.056 * Lobe(nanometres, 599.8, 37.9, 31.0) + .362 * Lobe(nanometres, 442.0, 16.0, 26.7) - .065 * Lobe(nanometres, 501.1, 20.4, 26.2),
        .821 * Lobe(nanometres, 568.8, 46.9, 40.5) + .286 * Lobe(nanometres, 530.9, 16.3, 31.1),
        1.217 * Lobe(nanometres, 437.0, 11.8, 36.0) + .681 * Lobe(nanometres, 459.0, 26.0, 13.8));

    /// <summary>CIE XYZ of a blackbody at kelvin, in W sr^-1 m^-2 per unit of the matching functions (relative).</summary>
    public static (double X, double Y, double Z) Tristimulus(double kelvin)
    {
        if (!(kelvin > 0)) throw new ArgumentOutOfRangeException(nameof(kelvin));
        const double h = 6.62607015e-34, c = 299792458.0, k = 1.380649e-23;
        double x = 0, y = 0, z = 0;
        for (int nm = 380; nm <= 780; nm++)
        {
            double lambda = nm * 1e-9, exponent = h * c / (lambda * k * kelvin);
            // B_lambda = 2 h c^2 / lambda^5 / (e^x - 1); exp overflows only where the radiance is negligibly small.
            double radiance = exponent > 700 ? 0 : 2 * h * c * c / Math.Pow(lambda, 5) / (Math.Exp(exponent) - 1) * 1e-9;
            var (xb, yb, zb) = MatchingFunctions(nm);
            x += radiance * xb; y += radiance * yb; z += radiance * zb;
        }
        return (x, y, z);
    }
    /// <summary>CIE 1931 chromaticity (x, y) of a blackbody.</summary>
    public static (double X, double Y) Chromaticity(double kelvin)
    {
        var (x, y, z) = Tristimulus(kelvin);
        return (x / (x + y + z), y / (x + y + z));
    }
    /// <summary>Linear sRGB with luminance 1 (clipped to the gamut, then renormalised).</summary>
    public static (double R, double G, double B) LinearRgb(double kelvin)
    {
        var (x, y, z) = Tristimulus(kelvin);
        double r = Math.Max(0, 3.2404542 * x - 1.5371385 * y - .4985314 * z);
        double g = Math.Max(0, -.9692660 * x + 1.8760108 * y + .0415560 * z);
        double b = Math.Max(0, .0556434 * x - .2040259 * y + 1.0572252 * z);
        double luminance = .2126729 * r + .7151522 * g + .0721750 * b;
        return (r / luminance, g / luminance, b / luminance);
    }

    /// <summary>Table range: log10 of the temperature, 1000 K to 10^9 K, and its size.</summary>
    public const double TableLogMinimum = 3, TableLogMaximum = 9;
    public const int TableSize = 512;
    /// <summary>
    /// RGBA per entry i at log10 T = min + i (max - min) / (size - 1): linear sRGB of unit luminance and log10 of the
    /// luminance Y, as world_lens.gdshader reads it for the disk at its observed temperature.
    /// </summary>
    public static float[] Table()
    {
        var table = new float[4 * TableSize];
        for (int index = 0; index < TableSize; index++)
        {
            double kelvin = Math.Pow(10, TableLogMinimum + index * (TableLogMaximum - TableLogMinimum) / (TableSize - 1));
            var (r, g, b) = LinearRgb(kelvin);
            table[4 * index] = (float)r; table[4 * index + 1] = (float)g; table[4 * index + 2] = (float)b;
            table[4 * index + 3] = (float)Math.Log10(Tristimulus(kelvin).Y);
        }
        return table;
    }
}
