using System;
using System.Collections.Generic;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Whether display pixel (<paramref name="x"/>, <paramref name="y"/>) on frame <paramref name="frame"/> lies
    /// on a measured receiver, whether it lies on the receiver's flat half, and its signed distance in display pixels
    /// to the shadow edge, positive on the shadow side.</summary>
    internal delegate bool ShadowEdgeSample(int frame, int x, int y, out bool flat, out float edgeDistance);

    /// <summary>One region's pooled values over every measured frame.</summary>
    internal sealed class ShadowEdgeRegion
    {
        readonly List<float> _abs = new();
        double _signed, _grainOut, _grainRef, _errorGrain, _change, _changeRef, _changeError;
        int _changes;

        public void Add(float error, float grainOut, float grainRef, float errorGrain)
        {
            _abs.Add(MathF.Abs(error));
            _signed += error;
            _grainOut += grainOut;
            _grainRef += grainRef;
            _errorGrain += errorGrain;
        }

        /// <summary>A pixel's luma change from the frame before in the output and in the reference.</summary>
        public void AddChange(float change, float changeRef)
        {
            _change += MathF.Abs(change);
            _changeRef += MathF.Abs(changeRef);
            _changeError += MathF.Abs(change - changeRef);
            _changes++;
        }

        public int Count => _abs.Count;
        double Per(double sum) => Count == 0 ? 0 : sum / Count;
        double PerChange(double sum) => _changes == 0 ? 0 : sum / _changes;
        public double Mean { get { double s = 0; foreach (float a in _abs) s += a; return Per(s); } }
        public double Bias => Per(_signed);
        public double GrainOut => Per(_grainOut);
        public double GrainRef => Per(_grainRef);
        public double ErrorGrain => Per(_errorGrain);

        /// <summary>The mean absolute frame-to-frame luma change of the output over the region's pixels on every
        /// measured frame after the first.</summary>
        public double Change => PerChange(_change);

        /// <summary><see cref="Change"/> of the reference over the same pixels.</summary>
        public double ChangeRef => PerChange(_changeRef);

        /// <summary>The mean of <c>|(o[t] - o[t - 1]) - (r[t] - r[t - 1])|</c> over the same pixels, the change the
        /// reference does not make: flicker, or lag behind a change it does make.</summary>
        public double ChangeError => PerChange(_changeError);

        public double P95
        {
            get
            {
                if (Count == 0) return 0;
                var sorted = _abs.ToArray();
                Array.Sort(sorted);
                return sorted[Math.Min(sorted.Length - 1, (int)(0.95 * sorted.Length))];
            }
        }
    }

    /// <summary>
    /// The band, tail and control metrics of a run's measured frames against their references, over the receiver
    /// pixels a <see cref="ShadowEdgeSample"/> names. The edge is where the reference's flat half crosses midway
    /// between its lit and its shadow luma, found from the analytic line first, since normal offset and filtering
    /// move the drawn edge off it. Distances are then taken along the travel, <c>t</c>, positive on the side the edge
    /// moves towards over the receiver, which it has not crossed, and negative on the side it came from. The band is
    /// <c>|t|</c> up to <see cref="BandPixels"/>, the tail <c>t</c> from <see cref="TailPixels"/> to minus
    /// <see cref="BandPixels"/>, and the control <c>t</c> from <see cref="FarPixels"/> on, which no edge has crossed.
    /// Per region it pools the absolute and signed luma error, the grain of the output and of the reference, a
    /// pixel's luma less its 3x3 median, the grain of the error image itself, and from the second measured frame on
    /// the frame-to-frame luma change of the output and of the reference, and per half the signed error
    /// profile along the travel. <c>TemporalShadowEdgeLagGpuTests</c> and <c>TemporalMovingShadeLagGpuTests</c> read
    /// it.
    /// </summary>
    internal sealed class ShadowEdgeMeasure
    {
        public const float BandPixels = 12f, TailPixels = 48f, FarPixels = 32f;
        public const int Border = 2, ProfileBins = 16, ProfileBin = 4, ProfileAhead = 16;

        readonly ShadowEdgeRegion _bandTextured = new(), _bandFlat = new(), _tailTextured = new(), _tailFlat = new(),
            _farTextured = new(), _farFlat = new();
        readonly double[] _profileSum = new double[2 * ProfileBins];
        readonly int[] _profileCount = new int[2 * ProfileBins];
        readonly int _w, _h;
        readonly ShadowEdgeSample _sample;
        readonly float _sign;
        float[]? _previousOut, _previousRef;

        /// <summary>Measures <paramref name="frames"/>, the frames from <paramref name="firstFrame"/> on, against
        /// <paramref name="references"/> of the same frames. <paramref name="sign"/> is 1 where the edge moves over
        /// the receiver towards its lit side and minus 1 where it moves towards its shadow side.</summary>
        public ShadowEdgeMeasure(int w, int h, ShadowEdgeSample sample, float sign, IReadOnlyList<byte[]> frames,
            IReadOnlyList<byte[]> references, int firstFrame)
        {
            _w = w;
            _h = h;
            _sample = sample;
            _sign = sign;
            FindEdge(references, firstFrame);
            var window = new float[9];
            for (int i = 0; i < frames.Count; i++) Add(firstFrame + i, frames[i], references[i], window);
        }

        /// <summary>The drawn edge's offset from the analytic line in display pixels, towards the shadow.</summary>
        public float EdgeOffset { get; private set; }

        /// <summary>The reference's flat lit luma 14 to 30 pixels from the edge less its shadow luma there.</summary>
        public double ReferenceContrast { get; private set; }

        public IEnumerable<(string, ShadowEdgeRegion)> Regions => new[]
        {
            ("band textured", _bandTextured), ("band flat", _bandFlat), ("tail textured", _tailTextured),
            ("tail flat", _tailFlat), ("far textured", _farTextured), ("far flat", _farFlat),
        };

        public string Profile(bool flat)
        {
            var parts = new string[ProfileBins];
            for (int i = 0; i < ProfileBins; i++)
            {
                int k = (flat ? ProfileBins : 0) + i;
                parts[i] = _profileCount[k] == 0 ? "-" : $"{1000 * _profileSum[k] / _profileCount[k]:0}";
            }
            return string.Join(" ", parts);
        }

        float[] LumaOf(byte[] rgba)
        {
            var l = new float[_w * _h];
            for (int y = 0; y < _h; y++)
                for (int x = 0; x < _w; x++) l[y * _w + x] = TemporalAcceptance.Luma(rgba, _w, x, y);
            return l;
        }

        void FindEdge(IReadOnlyList<byte[]> references, int firstFrame)
        {
            const float Reach = 8f, Step = 0.25f;
            int bins = (int)(2 * Reach / Step);
            var sum = new double[bins];
            var count = new int[bins];
            double lit = 0, shadow = 0;
            int litCount = 0, shadowCount = 0;
            for (int i = 0; i < references.Count; i++)
            {
                float[] lr = LumaOf(references[i]);
                for (int y = Border; y < _h - Border; y++)
                    for (int x = Border; x < _w - Border; x++)
                    {
                        if (!_sample(firstFrame + i, x, y, out bool flat, out float d) || !flat) continue;
                        float ad = MathF.Abs(d);
                        if (ad >= 14f && ad <= 30f)
                        {
                            if (d < 0f) { lit += lr[y * _w + x]; litCount++; }
                            else { shadow += lr[y * _w + x]; shadowCount++; }
                        }
                        if (d >= -Reach && d < Reach)
                        {
                            int b = (int)((d + Reach) / Step);
                            sum[b] += lr[y * _w + x];
                            count[b]++;
                        }
                    }
            }
            double litMean = litCount == 0 ? 0 : lit / litCount;
            double shadowMean = shadowCount == 0 ? 0 : shadow / shadowCount;
            ReferenceContrast = litMean - shadowMean;
            double mid = 0.5 * (litMean + shadowMean);
            EdgeOffset = 0f;
            for (int b = 0; b < bins; b++)
                if (count[b] > 0 && sum[b] / count[b] < mid)
                {
                    EdgeOffset = -Reach + (b + 0.5f) * Step;
                    break;
                }
        }

        void Add(int n, byte[] frame, byte[] reference, float[] window)
        {
            float[] lo = LumaOf(frame), lr = LumaOf(reference);
            var e = new float[_w * _h];
            for (int p = 0; p < e.Length; p++) e[p] = lo[p] - lr[p];
            for (int y = Border; y < _h - Border; y++)
                for (int x = Border; x < _w - Border; x++)
                {
                    if (!_sample(n, x, y, out bool flat, out float d)) continue;
                    float t = -_sign * (d - EdgeOffset);
                    int p = y * _w + x;
                    if (t >= -ProfileBins * ProfileBin + ProfileAhead && t < ProfileAhead)
                    {
                        int bin = (int)MathF.Floor((t + ProfileBins * ProfileBin - ProfileAhead) / ProfileBin);
                        _profileSum[(flat ? ProfileBins : 0) + bin] += e[p];
                        _profileCount[(flat ? ProfileBins : 0) + bin]++;
                    }
                    ShadowEdgeRegion? region = MathF.Abs(t) <= BandPixels ? (flat ? _bandFlat : _bandTextured)
                        : t < 0f && t >= -TailPixels ? (flat ? _tailFlat : _tailTextured)
                        : t >= FarPixels ? (flat ? _farFlat : _farTextured) : null;
                    if (region == null) continue;
                    region.Add(e[p], MathF.Abs(lo[p] - Median(lo, x, y, window)),
                        MathF.Abs(lr[p] - Median(lr, x, y, window)), MathF.Abs(e[p] - Median(e, x, y, window)));
                    if (_previousOut != null && _previousRef != null)
                        region.AddChange(lo[p] - _previousOut[p], lr[p] - _previousRef[p]);
                }
            _previousOut = lo;
            _previousRef = lr;
        }

        float Median(float[] image, int x, int y, float[] window)
        {
            int k = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++) window[k++] = image[(y + dy) * _w + x + dx];
            Array.Sort(window);
            return window[4];
        }
    }
}
