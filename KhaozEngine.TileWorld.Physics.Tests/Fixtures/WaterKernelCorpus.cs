using System;
using System.Numerics;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

internal static class WaterKernelCorpus
{
    internal static int Sweep(TileMediumSampler field, FrozenWaterReference frozen, int seed, int samples)
    {
        var snapshot = field.CaptureTraceForTests();
        var columns = snapshot.Columns.Concat(snapshot.Nonuniform).Distinct().ToArray();
        if (columns.Length == 0) throw new InvalidOperationException("A water corpus fixture must contain captured water columns.");
        var random = new Random(seed);
        int count = 0;
        for (int i = 0; i < samples; i++)
        {
            var column = columns[(int)((long)i * columns.Length / samples)];
            float x = Lerp(column.Minimum.X - 0.35f, column.Maximum.X + 0.35f);
            float z = Lerp(column.Minimum.Z - 0.35f, column.Maximum.Z + 0.35f);
            float y = Lerp(column.Minimum.Y - 1, column.Maximum.Y + 1);
            var size = (i % 5) switch
            {
                0 => (0.3f, 0.75f),
                1 => (0.9f, 0.95f),
                2 => (0.2f, 0.25f),
                3 => (0.125f, 0.25f),
                _ => (0.1f, 0.25f)
            };
            Vector3 direction = (i % 12) switch
            {
                0 => Vector3.UnitX,
                1 => -Vector3.UnitX,
                2 => Vector3.UnitZ,
                3 => -Vector3.UnitZ,
                4 => Vector3.Normalize(new(1, 0, 1)),
                5 => Vector3.Normalize(new(-1, 0, 1)),
                6 => Vector3.Normalize(new(1, 0, -1)),
                7 => Vector3.Normalize(new(-1, 0, -1)),
                8 => Vector3.UnitY,
                9 => -Vector3.UnitY,
                10 => Vector3.Normalize(new(1, 1, -1)),
                _ => Vector3.Normalize(new(-1, -1, 1)),
            };
            float length = (i % 7) switch { 0 => 0, 1 => 0.0001f, 2 => 0.1f, 3 => 0.5f, 4 => 1, 5 => 3, _ => 8 };
            Vector3 origin = (i % 3) switch { 0 => Vector3.Zero, 1 => new(64, 0, -192), _ => new(1048576, 4096, -1048576) };
            var body = new MovementBodyQuery(new Vector3(x, y, z) - origin, size.Item1, size.Item2,
                MovementWaterFieldTests.Space, null);
            var query = new MovementMediumSweepQuery(body, direction * length);
            _ = FrozenWaterComparison.Check(field, frozen, query, origin);
            count++;
            if (i == 1)
            {
                // Independent perturbations of every start and drive component, in both adjacent-float directions.
                for (int component = 0; component < 6; component++)
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        Vector3 start = body.Centre, delta = query.Delta;
                        float value = component switch
                        {
                            0 => start.X,
                            1 => start.Y,
                            2 => start.Z,
                            3 => delta.X,
                            4 => delta.Y,
                            _ => delta.Z
                        };
                        value = sign < 0 ? MathF.BitDecrement(value) : MathF.BitIncrement(value);
                        switch (component)
                        {
                            case 0: start.X = value; break;
                            case 1: start.Y = value; break;
                            case 2: start.Z = value; break;
                            case 3: delta.X = value; break;
                            case 4: delta.Y = value; break;
                            default: delta.Z = value; break;
                        }
                        _ = FrozenWaterComparison.Check(field, frozen, new(new(start, body.Radius, body.HalfHeight,
                            body.CurrentSpace, null), delta), origin);
                        count++;
                    }
            }
        }
        return count;
        float Lerp(float low, float high) => low + (high - low) * (float)random.NextDouble();
    }
}
