using System;
using System.Diagnostics;
using System.Globalization;
using KhaozEngine.Imaging;

namespace KhaozEngine.Benchmarks.Imaging
{
    /// <summary>
    /// The <c>--jpeg</c> mode: times <see cref="JpegWriter.Encode"/> on one bug report clip frame, 960x540 at
    /// quality 70, over a deterministic gradient with a high-frequency ripple so the entropy coder has real work.
    /// </summary>
    internal static class JpegEncodeBenchmark
    {
        const int Width = 960;
        const int Height = 540;
        const int Quality = 70;
        const int WarmupIterations = 10;
        const int TimedIterations = 200;

        /// <summary>Runs the measurement and prints one result line. The mode takes no flags beyond <c>--jpeg</c>.</summary>
        internal static int Run(string[] args)
        {
            byte[] frame = Frame();
            int bytes = 0;
            for (int i = 0; i < WarmupIterations; i++) bytes = JpegWriter.Encode(frame, Width, Height, Quality).Length;

            var milliseconds = new double[TimedIterations];
            for (int i = 0; i < TimedIterations; i++)
            {
                long start = Stopwatch.GetTimestamp();
                JpegWriter.Encode(frame, Width, Height, Quality);
                milliseconds[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            double mean = 0;
            foreach (double ms in milliseconds) mean += ms;
            mean /= TimedIterations;
            Array.Sort(milliseconds);
            double p95 = milliseconds[(int)Math.Ceiling(TimedIterations * 0.95) - 1];

            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"jpeg {Width}x{Height} q{Quality}: mean {mean:0.00} ms, p95 {p95:0.00} ms, bytes {bytes}"));
            return 0;
        }

        static byte[] Frame()
        {
            var rgba = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int i = (y * Width + x) * 4;
                    int ripple = (x * 31 + y * 17) % 64 - 32;
                    rgba[i] = (byte)Math.Clamp(x * 255 / (Width - 1) + ripple, 0, 255);
                    rgba[i + 1] = (byte)Math.Clamp(y * 255 / (Height - 1) + ripple, 0, 255);
                    rgba[i + 2] = (byte)Math.Clamp(128 + ripple, 0, 255);
                    rgba[i + 3] = 255;
                }
            }
            return rgba;
        }
    }
}
