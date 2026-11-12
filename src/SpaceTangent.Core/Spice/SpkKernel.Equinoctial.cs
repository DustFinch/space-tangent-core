// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Spice;

public sealed partial class SpkKernel
{
    private Segment PrepareEquinoctial(DafSegment descriptor, JulianInstant start, JulianInstant end)
    {
        int first = descriptor.FirstWord;
        if (descriptor.LastWord - first != 11)
            throw new InvalidDataException("An SPK equinoctial record requires 12 words.");
        for (int index = 0; index < 12; index++)
            if (!double.IsFinite(_file.Word(first + index)))
                throw new InvalidDataException("The SPK equinoctial elements must be finite.");
        double h = _file.Word(first + 2), k = _file.Word(first + 3);
        if (_file.Word(first + 1) <= 0 || h * h + k * k > .81)
            throw new InvalidDataException("The SPK equinoctial orbit is outside its supported eccentricity range.");
        return new(descriptor, start, end);
    }

    private CartesianState SampleEquinoctial(Segment segment, double epoch)
    {
        int first = segment.Descriptor.FirstWord;
        double dt = epoch - _file.Word(first), semiMajor = _file.Word(first + 1);
        double h = _file.Word(first + 2), k = _file.Word(first + 3);
        double lambda = _file.Word(first + 4);
        double p = _file.Word(first + 5), q = _file.Word(first + 6);
        double apsides = _file.Word(first + 7), longitudeRate = _file.Word(first + 8), nodes = _file.Word(first + 9);
        (h, k) = Precess(h, k, apsides * dt);
        (p, q) = Precess(p, q, nodes * dt);
        double hRate = apsides * k, kRate = -apsides * h;
        double pRate = nodes * q, qRate = -nodes * p;
        lambda = Math.IEEERemainder(lambda + longitudeRate * dt, Math.Tau);
        double longitude = lambda;
        for (int iteration = 0; iteration < 30; iteration++)
        {
            var (sin, cos) = Math.SinCos(longitude);
            double step = (longitude - k * sin + h * cos - lambda) / (1 - k * cos - h * sin);
            longitude -= step;
            if (Math.Abs(step) < 1e-14)
                break;
        }
        var (sine, cosine) = Math.SinCos(longitude);
        double longitudeDerivative = (longitudeRate + kRate * sine - hRate * cosine) / (1 - k * cosine - h * sine);
        double sineRate = cosine * longitudeDerivative, cosineRate = -sine * longitudeDerivative;
        double beta = 1 / (1 + Math.Sqrt(1 - h * h - k * k));
        double cross = beta * h * k, crossRate = beta * (hRate * k + h * kRate);
        double xCos = 1 - beta * h * h, ySin = 1 - beta * k * k;
        double x = semiMajor * (xCos * cosine + cross * sine - k);
        double y = semiMajor * (ySin * sine + cross * cosine - h);
        double xRate = semiMajor * (xCos * cosineRate - 2 * beta * h * hRate * cosine + cross * sineRate + crossRate * sine - kRate);
        double yRate = semiMajor * (ySin * sineRate - 2 * beta * k * kRate * sine + cross * cosineRate + crossRate * cosine - hRate);

        double denominator = 1 + p * p + q * q;
        double crossAxis = 2 * p * q, crossAxisRate = 2 * (pRate * q + p * qRate);
        double diagonalRate = 2 * (q * qRate - p * pRate);
        var firstAxis = new Vector3d(1 - p * p + q * q, crossAxis, -2 * p) / denominator;
        var secondAxis = new Vector3d(crossAxis, 1 + p * p - q * q, 2 * q) / denominator;
        var firstRate = new Vector3d(diagonalRate, crossAxisRate, -2 * pRate) / denominator;
        var secondRate = new Vector3d(crossAxisRate, -diagonalRate, 2 * qRate) / denominator;
        var (sinRa, cosRa) = Math.SinCos(_file.Word(first + 10));
        var (sinDec, cosDec) = Math.SinCos(_file.Word(first + 11));
        var equator = new Basis3d(new(-sinRa, cosRa, 0), new(-cosRa * sinDec, -sinRa * sinDec, cosDec),
            new(cosRa * cosDec, sinRa * cosDec, sinDec));
        return CheckedState(new(equator.Transform(firstAxis * x + secondAxis * y),
            equator.Transform(firstAxis * xRate + secondAxis * yRate + firstRate * x + secondRate * y)));
    }

    private static (double First, double Second) Precess(double first, double second, double radians)
    {
        var (sin, cos) = Math.SinCos(radians);
        return (first * cos + second * sin, second * cos - first * sin);
    }
}
