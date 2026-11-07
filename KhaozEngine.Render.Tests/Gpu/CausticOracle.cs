using System;
using System.Numerics;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// An independent double-precision forward map of one cosine ripple, written from the caustic design's formulas and
/// calling no engine code. The ripple's slope is <c>G cos(psi)</c> along its heading, with the surface phase
/// <c>psi = k (heading . xz) + time * waveSpeed + seed * 1.61803399</c> and <c>G = sqrt(2.72317) * k</c>, the
/// amplitude one ripple component carries. The ripple normal combines with the flat swell normal as the water pass
/// does, the sun refracts at 1.333, and a still-water point lands at
/// <c>strength * depth * (t.xz / max(-t.y, 0.1) - t0.xz / max(-t0.y, 0.1))</c> from itself. Bands lie along the ripple
/// crests, so everything here is one-dimensional in the heading coordinate <c>u = heading . xz</c>.
/// </summary>
internal sealed class CausticOracle
{
    const double RefractiveIndex = 1.333;
    const double SlopeVariance = 2.72317;
    const double SeedPhaseStride = 1.61803399;
    const int Samples = 16384;

    readonly double _k, _phaseAtOrigin, _entryShift;
    // Per unit of strength times slice depth: the landing offset along the heading and its magnitude in XZ.
    readonly double[] _alongPerDepth = new double[Samples];
    readonly double _maxDeflectionPerDepth, _steepestConvergence;

    public double Wavelength { get; }
    public Vector2 Heading { get; }
    /// <summary>How far a receiver's entry lies from it in XZ per metre of depth, against the still ray's travel.</summary>
    public (double X, double Z) EntryPerMetre { get; }

    /// <param name="wavelength">Ripple wavelength, <c>2 pi * WaveScale</c>.</param>
    /// <param name="normalStrength">The look's <c>NormalStrength</c>.</param>
    /// <param name="seed">The look's <c>RippleSeed</c>, which is also the heading in radians.</param>
    /// <param name="sun">The light's travel direction, pointing down.</param>
    /// <param name="scrolledTime">Effect time times wave speed.</param>
    public CausticOracle(double wavelength, double normalStrength, double seed, Vector3 sun, double scrolledTime)
    {
        Wavelength = wavelength;
        _k = 2d * Math.PI / wavelength;
        Heading = new Vector2((float)Math.Cos(seed), (float)Math.Sin(seed));
        double hx = Math.Cos(seed), hz = Math.Sin(seed);
        _phaseAtOrigin = scrolledTime + seed * SeedPhaseStride;

        double length = Math.Sqrt((double)sun.X * sun.X + (double)sun.Y * sun.Y + (double)sun.Z * sun.Z);
        var l = (sun.X / length, sun.Y / length, sun.Z / length);
        var still = Refract(l, (0d, 1d, 0d));
        double stillX = still.X / Math.Max(-still.Y, 0.1), stillZ = still.Z / Math.Max(-still.Y, 0.1);
        // A receiver at depth h reads the map at its entry E = P - t0 * h / -t0.y, so its band sits that far past it.
        _entryShift = (hx * still.X + hz * still.Z) / -still.Y;
        EntryPerMetre = (-still.X / -still.Y, -still.Z / -still.Y);

        double amplitude = Math.Sqrt(SlopeVariance) * _k * normalStrength;
        for (int i = 0; i < Samples; i++)
        {
            double slope = amplitude * Math.Cos(2d * Math.PI * i / Samples);
            var ripple = Normalize((-slope * hx, 1d, -slope * hz));
            var normal = Normalize((ripple.X, 1d, ripple.Z));
            var t = Refract(l, normal);
            double dx = t.X / Math.Max(-t.Y, 0.1) - stillX, dz = t.Z / Math.Max(-t.Y, 0.1) - stillZ;
            _alongPerDepth[i] = hx * dx + hz * dz;
            _maxDeflectionPerDepth = Math.Max(_maxDeflectionPerDepth, Math.Sqrt(dx * dx + dz * dz));
        }
        double step = Wavelength / Samples;
        for (int i = 0; i < Samples; i++)
            _steepestConvergence = Math.Min(_steepestConvergence,
                (_alongPerDepth[(i + 1) % Samples] - _alongPerDepth[(i + Samples - 1) % Samples]) / (2d * step));
    }

    /// <summary>Strength times slice depth at which the first fold forms: the map is single valued below it.</summary>
    public double FocalDepth => -1d / _steepestConvergence;

    /// <summary>Largest XZ distance a ray lands from its still ray at this strength times slice depth.</summary>
    public double MaxDeflection(double scaledDepth) => _maxDeflectionPerDepth * scaledDepth;

    /// <summary>The brightest band's heading coordinate in absolute world for a receiver at <paramref name="receiverDepth"/>,
    /// reading a slice at <paramref name="scaledDepth"/> (strength times slice depth), reduced to one wavelength.</summary>
    public double BrightestBand(double scaledDepth, double receiverDepth)
    {
        int best = 0;
        double steepest = double.PositiveInfinity, step = Wavelength / Samples;
        for (int i = 0; i < Samples; i++)
        {
            double slope = (_alongPerDepth[(i + 1) % Samples] - _alongPerDepth[(i + Samples - 1) % Samples]) / (2d * step);
            if (slope < steepest)
            {
                steepest = slope;
                best = i;
            }
        }
        return Wrap(MapCoordinate(best, scaledDepth) + _entryShift * receiverDepth);
    }

    /// <summary>The term a slice at <paramref name="scaledDepth"/> holds at map heading coordinate <paramref name="u"/>:
    /// the still-water length that lands there per unit length, summed over every ray that does.</summary>
    public double Term(double scaledDepth, double u)
    {
        double target = Wrap(u), step = Wavelength / Samples, sum = 0d;
        for (int i = 0; i < Samples; i++)
        {
            double a = MapCoordinate(i, scaledDepth), b = a + step + scaledDepth
                * (_alongPerDepth[(i + 1) % Samples] - _alongPerDepth[i]);
            double low = Math.Min(a, b), high = Math.Max(a, b);
            // The segment can sit a wavelength either side of the target after wrapping its start.
            for (int shift = -1; shift <= 1; shift++)
            {
                double x = target + shift * Wavelength;
                if (x >= low && x < high) sum += step / (high - low);
            }
        }
        return sum;
    }

    /// <summary><see cref="Term"/> tabulated over one wavelength and read back with linear interpolation, for per-pixel use.</summary>
    public Func<double, double> TermLookup(double scaledDepth, int points = 1024)
    {
        var table = new double[points + 1];
        for (int i = 0; i < points; i++) table[i] = Term(scaledDepth, Wavelength * i / points);
        table[points] = table[0];
        return u =>
        {
            double x = Wrap(u) / Wavelength * points;
            int i = Math.Min((int)x, points - 1);
            return table[i] + (table[i + 1] - table[i]) * (x - i);
        };
    }

    /// <summary>Peak term of the brightest band at this strength times slice depth.</summary>
    public double PeakTerm(double scaledDepth) => Term(scaledDepth, BrightestBand(scaledDepth, 0d));

    /// <summary>Map heading coordinate of a receiver at <paramref name="receiverDepth"/> below a point at <paramref name="u"/>.</summary>
    public double EntryOf(double u, double receiverDepth) => u - _entryShift * receiverDepth;

    /// <summary>Signed distance from <paramref name="u"/> to <paramref name="reference"/>, wrapped to half a wavelength.</summary>
    public double Distance(double u, double reference)
    {
        double d = Wrap(u - reference);
        return d > Wavelength / 2d ? d - Wavelength : d;
    }

    double MapCoordinate(int sample, double scaledDepth) =>
        Wrap((2d * Math.PI * sample / Samples - _phaseAtOrigin) / _k) + scaledDepth * _alongPerDepth[sample];

    double Wrap(double u) => u - Math.Floor(u / Wavelength) * Wavelength;

    static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
    {
        double length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return (v.X / length, v.Y / length, v.Z / length);
    }

    // Snell's law in vector form for a unit incident direction and a unit normal facing against it.
    static (double X, double Y, double Z) Refract((double X, double Y, double Z) i, (double X, double Y, double Z) n)
    {
        const double eta = 1d / RefractiveIndex;
        double cosine = n.X * i.X + n.Y * i.Y + n.Z * i.Z;
        double k = 1d - eta * eta * (1d - cosine * cosine);
        double scale = eta * cosine + Math.Sqrt(k);
        return (eta * i.X - scale * n.X, eta * i.Y - scale * n.Y, eta * i.Z - scale * n.Z);
    }
}
