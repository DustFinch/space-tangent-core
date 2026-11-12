// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Core.Observing;

public sealed record OpticalTube(string Name, double ApertureMillimeters, double FocalMillimeters)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(Name) && Name.Length <= 100 &&
        ApertureMillimeters is >= 10 and <= 20_000 && FocalMillimeters is >= 50 and <= 200_000;
}

public sealed record OpticalEyepiece(string Name, double FocalMillimeters, double ApparentFieldDegrees)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(Name) && Name.Length <= 100 &&
        FocalMillimeters is >= 1 and <= 100 && ApparentFieldDegrees is >= 20 and <= 120;
}

public sealed record VisualOptics(OpticalTube Tube, OpticalEyepiece Eyepiece, double Barlow = 1,
    double SeeingArcseconds = 2, bool Inverted = true, bool HoldSelection = true)
{
    private const double WavelengthMeters = 550e-9;
    private const double ArcsecondsPerRadian = 180 * 3600 / Math.PI;
    private const double FullWidthPerSigma = 2.35482;

    public double Magnification() => Tube.FocalMillimeters * Barlow / Eyepiece.FocalMillimeters;
    public double TrueFieldDegrees() => Eyepiece.ApparentFieldDegrees / Magnification();
    public double ExitPupilMillimeters() => Tube.ApertureMillimeters / Magnification();
    public double LimitingMagnitude() => 7.5 + 5 * Math.Log10(Tube.ApertureMillimeters / 10);
    public double DiffractionArcseconds() => 1.03 * ApertureAngleArcseconds();
    public double RayleighArcseconds() => 1.22 * ApertureAngleArcseconds();
    private double ApertureAngleArcseconds() => WavelengthMeters / (Tube.ApertureMillimeters / 1000) * ArcsecondsPerRadian;

    public bool IsValid() => Tube?.IsValid() == true && Eyepiece?.IsValid() == true && Barlow is >= 1 and <= 5 &&
        SeeingArcseconds is >= 0 and <= 20 && TrueFieldDegrees() is >= .001 and <= 120;

    public double WanderArcseconds(bool fromEarth)
    {
        if (!fromEarth || SeeingArcseconds <= 0)
            return 0;
        double aperture = Tube.ApertureMillimeters / 1000;
        double coherenceLength = .98 * WavelengthMeters * ArcsecondsPerRadian / SeeingArcseconds;
        return Math.Min(.42 * ApertureAngleArcseconds() * Math.Pow(aperture / coherenceLength, 5.0 / 6),
            SeeingArcseconds / FullWidthPerSigma);
    }

    public double BlurArcseconds(bool fromEarth)
    {
        double seeing = fromEarth ? SeeingArcseconds : 0;
        double wander = WanderArcseconds(fromEarth) * FullWidthPerSigma;
        double diffraction = DiffractionArcseconds();
        return Math.Sqrt(Math.Max(0, seeing * seeing - wander * wander) + diffraction * diffraction);
    }
}
