using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.Terrain;

/// <summary>One frozen released ground-cover row: its lattice address, the seven channel bits in salt order
/// (jitter X, jitter Z, density, model, scale, yaw, rank), the chosen model and the pose bits (position X, Y, Z
/// then the sixteen transform components M11 to M44).</summary>
public sealed record GroundCoverReleasedRow(int X, int Z, string Model, uint[] Channels, uint[] Pose);

/// <summary>An independently computed candidate: the seven channel values for one lattice address.</summary>
public readonly record struct GroundCoverChannels(
    int X, int Z, float JitterX, float JitterZ, float Density, float Model, float Scale, float Yaw, float Rank);

/// <summary>One independently expected outcome for an owned candidate.</summary>
public sealed record GroundCoverExpectedRow(
    GroundCoverChannels Channels, float WorldX, float WorldZ, GroundCoverInstance? Instance);

/// <summary>Invented fixtures and an independent scalar oracle for the released ground-cover generator. The
/// oracle recomputes every channel from the pinned salts and the released arithmetic order, so a changed kernel
/// is compared against it and against frozen literals, never against its own output.</summary>
public static class GroundCoverTraceFixtures
{
    public const ulong JitterXSalt = 0x60B7A3D53B14C2A1UL;
    public const ulong JitterZSalt = 0x91E10DA5C79E7B1DUL;
    public const ulong DensitySalt = 0xD18A40B4F35062C7UL;
    public const ulong ModelSalt = 0xA4FBCB9528163E09UL;
    public const ulong ScaleSalt = 0x9C3C8D7E5F2A61B4UL;
    public const ulong YawSalt = 0x3D17F0A58C6B42E9UL;
    public const ulong RankSalt = 0xE2A86149B7D30C5FUL;

    /// <summary>The basic invented settings: seed 17, spacing 0.5, scale 0.8 to 1.4, root offset -0.01 and
    /// ordered models reed-a weight 3 and reed-b weight 1.</summary>
    public static GroundCoverSettings Basic(int seed = 17, float spacing = 0.5f) => new()
    {
        Seed = seed,
        Spacing = spacing,
        ScaleMin = 0.8f,
        ScaleMax = 1.4f,
        RootOffset = -0.01f,
        Models = [new GroundCoverModel("reed-a", 3f), new GroundCoverModel("reed-b", 1f)],
    };

    /// <summary>The invented baseline area, which spans negative and positive lattice addresses.</summary>
    public static RectArea BasicArea => new(-2f, -2f, 2f, 2f);

    /// <summary>A flat upward surface at height 1 with full density.</summary>
    public static GroundCoverSample Flat(float x, float z) => new(1f, Vector3.UnitY, 1f);

    /// <summary>An invented noncoplanar surface: height and normal vary with both axes.</summary>
    public static GroundCoverSample Rolling(float x, float z) => new(
        0.5f + x * 0.3f - z * 0.2f + x * z * 0.05f,
        new Vector3(-0.3f - z * 0.05f, 1f, 0.2f - x * 0.05f),
        0.75f);

    /// <summary>The released scalar channel hash, restated from its pinned constants.</summary>
    public static float Hash01(int x, int z, int seed, ulong salt)
    {
        unchecked
        {
            ulong key = ((ulong)(uint)x << 32) | (uint)z;
            ulong value = key ^ ((ulong)(uint)seed * 0x9E3779B97F4A7C15UL) ^ salt;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return (value >> 40) / 16_777_216f;
        }
    }

    /// <summary>All seven channels for one lattice address.</summary>
    public static GroundCoverChannels ChannelsAt(int x, int z, int seed) => new(
        x, z,
        Hash01(x, z, seed, JitterXSalt), Hash01(x, z, seed, JitterZSalt), Hash01(x, z, seed, DensitySalt),
        Hash01(x, z, seed, ModelSalt), Hash01(x, z, seed, ScaleSalt), Hash01(x, z, seed, YawSalt),
        Hash01(x, z, seed, RankSalt));

    /// <summary>The jittered world X of an address, int plus jitter minus a half, then times spacing.</summary>
    public static float WorldX(in GroundCoverChannels c, float spacing) => (c.X + c.JitterX - 0.5f) * spacing;

    /// <summary>The jittered world Z of an address.</summary>
    public static float WorldZ(in GroundCoverChannels c, float spacing) => (c.Z + c.JitterZ - 0.5f) * spacing;

    /// <summary>The released uniform scale for a scale pick.</summary>
    public static float Scale(GroundCoverSettings settings, float pick) =>
        settings.ScaleMin + pick * (settings.ScaleMax - settings.ScaleMin);

    /// <summary>The released yaw in radians for a yaw pick.</summary>
    public static float Yaw(float pick) => pick * MathF.Tau;

    /// <summary>The released weighted pick in list order.</summary>
    public static string PickModel(GroundCoverSettings settings, float pick)
    {
        float total = 0f;
        foreach (GroundCoverModel model in settings.Models) total += model.Weight;
        float target = pick * total;
        float running = 0f;
        foreach (GroundCoverModel model in settings.Models)
        {
            running += model.Weight;
            if (target < running) return model.Id;
        }
        return settings.Models[^1].Id;
    }

    /// <summary>The released accepted instance for one candidate over one surface sample.</summary>
    public static GroundCoverInstance Instance(GroundCoverSettings settings, in GroundCoverChannels c,
        float worldX, float worldZ, GroundCoverSample sample)
    {
        Vector3 normal = Vector3.Normalize(sample.Normal);
        Vector3 position = new Vector3(worldX, sample.Height, worldZ) + normal * settings.RootOffset;
        Vector3 reference = MathF.Abs(normal.Y) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 right = Vector3.Normalize(Vector3.Cross(reference, normal));
        Vector3 forward = Vector3.Normalize(Vector3.Cross(right, normal));
        var alignment = new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            normal.X, normal.Y, normal.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f);
        Matrix4x4 transform = Matrix4x4.CreateScale(Scale(settings, c.Scale)) *
            Matrix4x4.CreateRotationY(Yaw(c.Yaw)) * alignment * Matrix4x4.CreateTranslation(position);
        return new GroundCoverInstance(PickModel(settings, c.Model), position, transform, c.Rank);
    }

    /// <summary>The lattice bounds the released generator scans for an area.</summary>
    public static (int XLo, int XHi, int ZLo, int ZHi) Bounds(RectArea area, float spacing) => (
        (int)Math.Floor(area.MinX / spacing - 0.5f), (int)Math.Ceiling(area.MaxX / spacing + 0.5f),
        (int)Math.Floor(area.MinZ / spacing - 0.5f), (int)Math.Ceiling(area.MaxZ / spacing + 0.5f));

    /// <summary>Every owned candidate in released scan order (Z then X, half-open area) with its expected
    /// instance, or null when the sample's density rejects it.</summary>
    public static List<GroundCoverExpectedRow> Expected(RectArea area, GroundCoverSettings settings,
        Func<float, float, GroundCoverSample> surface)
    {
        var rows = new List<GroundCoverExpectedRow>();
        (int xLo, int xHi, int zLo, int zHi) = Bounds(area, settings.Spacing);
        for (int z = zLo; z <= zHi; z++)
            for (int x = xLo; x <= xHi; x++)
            {
                GroundCoverChannels c = ChannelsAt(x, z, settings.Seed);
                float worldX = WorldX(c, settings.Spacing);
                float worldZ = WorldZ(c, settings.Spacing);
                if (worldX < area.MinX || worldX >= area.MaxX || worldZ < area.MinZ || worldZ >= area.MaxZ) continue;
                GroundCoverSample sample = surface(worldX, worldZ);
                bool accepted = sample.Density != 0f && c.Density < sample.Density;
                rows.Add(new GroundCoverExpectedRow(c, worldX, worldZ,
                    accepted ? Instance(settings, c, worldX, worldZ, sample) : null));
            }
        return rows;
    }

    /// <summary>A surface whose density equals each owned candidate's density pick, the threshold-equality case.
    /// Bias moves the density by whole binary32 steps (zero is exact equality).</summary>
    public static Func<float, float, GroundCoverSample> Threshold(RectArea area, GroundCoverSettings settings,
        int bias)
    {
        var picks = new Dictionary<(uint, uint), float>();
        (int xLo, int xHi, int zLo, int zHi) = Bounds(area, settings.Spacing);
        for (int z = zLo; z <= zHi; z++)
            for (int x = xLo; x <= xHi; x++)
            {
                GroundCoverChannels c = ChannelsAt(x, z, settings.Seed);
                picks[(Bits(WorldX(c, settings.Spacing)), Bits(WorldZ(c, settings.Spacing)))] = c.Density;
            }
        return (x, z) =>
        {
            float density = picks[(Bits(x), Bits(z))];
            for (int i = 0; i < bias; i++) density = MathF.BitIncrement(density);
            return new GroundCoverSample(1f, Vector3.UnitY, Math.Min(density, 1f));
        };
    }

    public static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    public static float Float(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    /// <summary>The seven channel bits in salt order.</summary>
    public static uint[] ChannelBits(in GroundCoverChannels c) =>
        [Bits(c.JitterX), Bits(c.JitterZ), Bits(c.Density), Bits(c.Model), Bits(c.Scale), Bits(c.Yaw), Bits(c.Rank)];

    /// <summary>Position then the sixteen transform components, as bits.</summary>
    public static uint[] PoseBits(in GroundCoverInstance instance)
    {
        Matrix4x4 m = instance.Transform;
        return
        [
            Bits(instance.Position.X), Bits(instance.Position.Y), Bits(instance.Position.Z),
            Bits(m.M11), Bits(m.M12), Bits(m.M13), Bits(m.M14),
            Bits(m.M21), Bits(m.M22), Bits(m.M23), Bits(m.M24),
            Bits(m.M31), Bits(m.M32), Bits(m.M33), Bits(m.M34),
            Bits(m.M41), Bits(m.M42), Bits(m.M43), Bits(m.M44),
        ];
    }

    /// <summary>An exact text key over model, pose and rank bits, for multiset comparisons.</summary>
    public static string Key(in GroundCoverInstance instance) =>
        instance.ModelId + "|" + string.Join(",", PoseBits(instance)) + "|" + Bits(instance.ThinningRank);

    /// <summary>Formats rows as C# literals for freezing in <see cref="ReleasedBits"/>.</summary>
    public static string Literal(IEnumerable<GroundCoverReleasedRow> rows)
    {
        var text = new StringBuilder();
        foreach (GroundCoverReleasedRow row in rows)
        {
            text.Append(CultureInfo.InvariantCulture, $"        new({row.X}, {row.Z}, \"{row.Model}\",\n");
            text.Append("            [").Append(Hex(row.Channels)).Append("],\n");
            text.Append("            [").Append(Hex(row.Pose[..7])).Append(",\n");
            text.Append("             ").Append(Hex(row.Pose[7..])).Append("]),\n");
        }
        return text.ToString();
    }

    static string Hex(uint[] bits)
    {
        var parts = new string[bits.Length];
        for (int i = 0; i < bits.Length; i++) parts[i] = "0x" + bits[i].ToString("X8", CultureInfo.InvariantCulture) + "u";
        return string.Join(", ", parts);
    }

    /// <summary>Released Generate rows for <see cref="Basic"/> over <see cref="BasicArea"/> on <see cref="Flat"/>,
    /// captured once from the released generator at b12a5977c before any kernel edit and frozen here. Never
    /// regenerate these to bless a mismatch.</summary>
    public static IReadOnlyList<GroundCoverReleasedRow> ReleasedBits { get; } =
    [
        new(-2, -4, "reed-a",
            [0x3E159A80u, 0x3F528ED3u, 0x3F6D2814u, 0x3E4576CCu, 0x3F42315Du, 0x3EDA03A6u, 0x3E2EE418u],
            [0xBF96A658u, 0x3F7D70A4u, 0xBFEB5C4Bu, 0x3F8F8421u, 0x00000000u, 0x3F106ABDu, 0x00000000u,
             0x00000000u, 0x3FA0A868u, 0x00000000u, 0x00000000u, 0xBF106ABDu, 0x00000000u, 0x3F8F8421u, 0x00000000u,
             0xBF96A658u, 0x3F7D70A4u, 0xBFEB5C4Bu, 0x3F800000u]),
        new(3, -4, "reed-a",
            [0x3EA772E6u, 0x3F3D5953u, 0x3F475BD9u, 0x3D8FCEC8u, 0x3F7DEDE9u, 0x3E91A916u, 0x3E6731B0u],
            [0x3FB4EE5Du, 0x3F7D70A4u, 0xBFF0A9ABu, 0x3E999A2Eu, 0x00000000u, 0x3FAE66B6u, 0x00000000u,
             0x00000000u, 0x3FB2942Cu, 0x00000000u, 0x00000000u, 0xBFAE66B6u, 0x00000000u, 0x3E999A2Eu, 0x00000000u,
             0x3FB4EE5Du, 0x3F7D70A4u, 0xBFF0A9ABu, 0x3F800000u]),
        new(-3, -3, "reed-b",
            [0x3F43D520u, 0x3F7BF3CDu, 0x3EAB22BCu, 0x3F61D935u, 0x3F1C00E6u, 0x3EE73E8Au, 0x3F0FD86Fu],
            [0xBFAF0AB8u, 0x3F7D70A4u, 0xBFA1030Du, 0x3F8E5E64u, 0x00000000u, 0x3EB2882Bu, 0x00000000u,
             0x00000000u, 0x3F953378u, 0x00000000u, 0x00000000u, 0xBEB2882Bu, 0x00000000u, 0x3F8E5E64u, 0x00000000u,
             0xBFAF0AB8u, 0x3F7D70A4u, 0xBFA1030Du, 0x3F800000u]),
        new(-2, -3, "reed-a",
            [0x3DB5C098u, 0x3E434F88u, 0x3E9A8ED8u, 0x3EFA53F8u, 0x3EB0E65Eu, 0x3DBA4800u, 0x3EF71E08u],
            [0xBF9A51FBu, 0x3F7D70A4u, 0xBFD3CB08u, 0xBF58E437u, 0x00000000u, 0x3F0B7B30u, 0x00000000u,
             0x00000000u, 0x3F80EF5Bu, 0x00000000u, 0x00000000u, 0xBF0B7B30u, 0x00000000u, 0xBF58E437u, 0x00000000u,
             0xBF9A51FBu, 0x3F7D70A4u, 0xBFD3CB08u, 0x3F800000u]),
        new(-1, -3, "reed-a",
            [0x3F14B6B4u, 0x3F246D65u, 0x3F0D13F3u, 0x3EDAF882u, 0x3E6D64E4u, 0x3EA77AF0u, 0x3EA68050u],
            [0xBEEB494Cu, 0x3F7D70A4u, 0xBFB6E4A7u, 0x3EDFF261u, 0x00000000u, 0x3F54BD8Fu, 0x00000000u,
             0x00000000u, 0x3F7068BCu, 0x00000000u, 0x00000000u, 0xBF54BD8Fu, 0x00000000u, 0x3EDFF261u, 0x00000000u,
             0xBEEB494Cu, 0x3F7D70A4u, 0xBFB6E4A7u, 0x3F800000u]),
        new(0, -3, "reed-a",
            [0x3F54464Cu, 0x3EE31418u, 0x3EC7277Cu, 0x3F14030Du, 0x3F388762u, 0x3E615E98u, 0x3F03F5F1u],
            [0x3E288C98u, 0x3F7D70A4u, 0xBFC39D7Du, 0xBE6BCEA4u, 0x00000000u, 0x3F9AFB04u, 0x00000000u,
             0x00000000u, 0x3F9DC237u, 0x00000000u, 0x00000000u, 0xBF9AFB04u, 0x00000000u, 0xBE6BCEA4u, 0x00000000u,
             0x3E288C98u, 0x3F7D70A4u, 0xBFC39D7Du, 0x3F800000u]),
        new(1, -3, "reed-a",
            [0x3EDCB2DAu, 0x3F29383Cu, 0x3EFE42DCu, 0x3D9DAA98u, 0x3E5ABFC8u, 0x3F6DF7FDu, 0x3E9193F4u],
            [0x3EEE596Cu, 0x3F7D70A4u, 0xBFB5B1F1u, 0xBF56B891u, 0x00000000u, 0xBECB83BAu, 0x00000000u,
             0x00000000u, 0x3F6D9CC4u, 0x00000000u, 0x00000000u, 0x3ECB83BAu, 0x00000000u, 0xBF56B891u, 0x00000000u,
             0x3EEE596Cu, 0x3F7D70A4u, 0xBFB5B1F1u, 0x3F800000u]),
        new(2, -3, "reed-b",
            [0x3F6B19EBu, 0x3E5D0368u, 0x3EB1407Eu, 0x3F44652Fu, 0x3F4568A5u, 0x3E89BFF2u, 0x3EF8B926u],
            [0x3F9AC67Bu, 0x3F7D70A4u, 0xBFD22FCAu, 0x3E1A5568u, 0x00000000u, 0x3FA07796u, 0x00000000u,
             0x00000000u, 0x3FA19F64u, 0x00000000u, 0x00000000u, 0xBFA07796u, 0x00000000u, 0x3E1A5568u, 0x00000000u,
             0x3F9AC67Bu, 0x3F7D70A4u, 0xBFD22FCAu, 0x3F800000u]),
        new(3, -3, "reed-a",
            [0x3F5D0D42u, 0x3F7805FEu, 0x3E291F1Cu, 0x3E257328u, 0x3E89389Cu, 0x3E4C9718u, 0x3F7303FAu],
            [0x3FD74350u, 0x3F7D70A4u, 0xBFA1FE80u, 0xBE989E1Eu, 0x00000000u, 0x3F69D472u, 0x00000000u,
             0x00000000u, 0x3F75F762u, 0x00000000u, 0x00000000u, 0xBF69D472u, 0x00000000u, 0xBE989E1Eu, 0x00000000u,
             0x3FD74350u, 0x3F7D70A4u, 0xBFA1FE80u, 0x3F800000u]),
        new(-3, -2, "reed-b",
            [0x3F0E4CCAu, 0x3F639037u, 0x3F300F3Fu, 0x3F4AF521u, 0x3F0DF5EBu, 0x3F557445u, 0x3F1FBA7Cu],
            [0xBFBC6CCEu, 0x3F7D70A4u, 0xBF4E37E4u, 0xBF11BB83u, 0x00000000u, 0xBF7AB21Bu, 0x00000000u,
             0x00000000u, 0x3F90FCFAu, 0x00000000u, 0x00000000u, 0x3F7AB21Bu, 0x00000000u, 0xBF11BB83u, 0x00000000u,
             0xBFBC6CCEu, 0x3F7D70A4u, 0xBF4E37E4u, 0x3F800000u]),
        new(-2, -2, "reed-a",
            [0x3F5B1978u, 0x3E6A132Cu, 0x3F4E6BFBu, 0x3BAD0600u, 0x3F7A2220u, 0x3F412291u, 0x3F16FC3Fu],
            [0xBF527344u, 0x3F7D70A4u, 0xBF915ECDu, 0xBD1E2887u, 0x00000000u, 0xBFB15F03u, 0x00000000u,
             0x00000000u, 0x3FB170A3u, 0x00000000u, 0x00000000u, 0x3FB15F03u, 0x00000000u, 0xBD1E2887u, 0x00000000u,
             0xBF527344u, 0x3F7D70A4u, 0xBF915ECDu, 0x3F800000u]),
        new(-1, -2, "reed-a",
            [0x3E351CFCu, 0x3F24CEFFu, 0x3F77554Eu, 0x3EC7C210u, 0x3F1B3B2Au, 0x3F5CF5C1u, 0x3F3EE8B8u],
            [0xBF295C60u, 0x3F7D70A4u, 0xBF6D9880u, 0xBF42623Eu, 0x00000000u, 0xBF61CAF6u, 0x00000000u,
             0x00000000u, 0x3F94F826u, 0x00000000u, 0x00000000u, 0x3F61CAF6u, 0x00000000u, 0xBF42623Eu, 0x00000000u,
             0xBF295C60u, 0x3F7D70A4u, 0xBF6D9880u, 0x3F800000u]),
        new(0, -2, "reed-a",
            [0x3F566AF2u, 0x3EED5BE4u, 0x3C97E8C0u, 0x3DFC4BA8u, 0x3F533563u, 0x3F0C5467u, 0x3ECF0F7Eu],
            [0x3E2CD5E4u, 0x3F7D70A4u, 0xBF825484u, 0x3F9E3AF5u, 0x00000000u, 0xBEC599F7u, 0x00000000u,
             0x00000000u, 0x3FA5C337u, 0x00000000u, 0x00000000u, 0x3EC599F7u, 0x00000000u, 0x3F9E3AF5u, 0x00000000u,
             0x3E2CD5E4u, 0x3F7D70A4u, 0xBF825484u, 0x3F800000u]),
        new(1, -2, "reed-a",
            [0x3EC824FEu, 0x3D129840u, 0x3ED11A04u, 0x3E3539A8u, 0x3F678536u, 0x3F572F7Eu, 0x3CC901C0u],
            [0x3EE41280u, 0x3F7D70A4u, 0xBF9DB59Fu, 0xBF3934A1u, 0x00000000u, 0xBF90C5E9u, 0x00000000u,
             0x00000000u, 0x3FABDB2Au, 0x00000000u, 0x00000000u, 0x3F90C5E9u, 0x00000000u, 0xBF3934A1u, 0x00000000u,
             0x3EE41280u, 0x3F7D70A4u, 0xBF9DB59Fu, 0x3F800000u]),
        new(2, -2, "reed-a",
            [0x3E9D981Cu, 0x3E1D7568u, 0x3F3068D6u, 0x3F20F1FAu, 0x3EABF740u, 0x3F004386u, 0x3EC395A6u],
            [0x3F676608u, 0x3F7D70A4u, 0xBF9628AAu, 0x3F803133u, 0x00000000u, 0xBBD47508u, 0x00000000u,
             0x00000000u, 0x3F8031E3u, 0x00000000u, 0x00000000u, 0x3BD47508u, 0x00000000u, 0x3F803133u, 0x00000000u,
             0x3F676608u, 0x3F7D70A4u, 0xBF9628AAu, 0x3F800000u]),
        new(3, -2, "reed-a",
            [0x3F024DFAu, 0x3F471FFDu, 0x3E33674Cu, 0x3D8E26C0u, 0x3F6179A6u, 0x3F1EA532u, 0x3DF78968u],
            [0x3FC0937Eu, 0x3F7D70A4u, 0xBF5C7002u, 0x3F78567Bu, 0x00000000u, 0xBF68596Eu, 0x00000000u,
             0x00000000u, 0x3FAA0AE5u, 0x00000000u, 0x00000000u, 0x3F68596Eu, 0x00000000u, 0x3F78567Bu, 0x00000000u,
             0x3FC0937Eu, 0x3F7D70A4u, 0xBF5C7002u, 0x3F800000u]),
        new(4, -2, "reed-a",
            [0x3ECC7AD0u, 0x3F24010Du, 0x3DD558D0u, 0x3D51D5D0u, 0x3DD90A70u, 0x3F5EBC85u, 0x3F3C31A5u],
            [0x3FF98F5Au, 0x3F7D70A4u, 0xBF6DFF7Au, 0xBF17673Bu, 0x00000000u, 0xBF211917u, 0x00000000u,
             0x00000000u, 0x3F5D13FCu, 0x00000000u, 0x00000000u, 0x3F211917u, 0x00000000u, 0xBF17673Bu, 0x00000000u,
             0x3FF98F5Au, 0x3F7D70A4u, 0xBF6DFF7Au, 0x3F800000u]),
        new(-3, -1, "reed-b",
            [0x3F6E8495u, 0x3F6E3A2Au, 0x3F39CF07u, 0x3F70474Fu, 0x3EF92A80u, 0x3F72145Cu, 0x3F46C1D4u],
            [0xBFA45EDBu, 0x3F7D70A4u, 0xBE91C5D6u, 0xBF83B227u, 0x00000000u, 0xBEBB53AFu, 0x00000000u,
             0x00000000u, 0x3F8BC660u, 0x00000000u, 0x00000000u, 0x3EBB53AFu, 0x00000000u, 0xBF83B227u, 0x00000000u,
             0xBFA45EDBu, 0x3F7D70A4u, 0xBE91C5D6u, 0x3F800000u]),
        new(-2, -1, "reed-a",
            [0x3E56D5C0u, 0x3F4D060Cu, 0x3F3D73A5u, 0x3F134EDEu, 0x3F4CB53Eu, 0x3EB37BF4u, 0x3EB1F7D0u],
            [0xBF9292A4u, 0x3F7D70A4u, 0xBEB2F9F4u, 0x3F417F08u, 0x00000000u, 0x3F8430BEu, 0x00000000u,
             0x00000000u, 0x3FA3CFF9u, 0x00000000u, 0x00000000u, 0xBF8430BEu, 0x00000000u, 0x3F417F08u, 0x00000000u,
             0xBF9292A4u, 0x3F7D70A4u, 0xBEB2F9F4u, 0x3F800000u]),
        new(-1, -1, "reed-a",
            [0x3F636592u, 0x3E9D5BBAu, 0x3F008D3Eu, 0x3F161338u, 0x3F45BA31u, 0x3F0BF9ACu, 0x3EDEA1AAu],
            [0xBE9C9A6Eu, 0x3F7D70A4u, 0xBF18A912u, 0x3F9AC879u, 0x00000000u, 0xBEBB66D8u, 0x00000000u,
             0x00000000u, 0x3FA1B7DBu, 0x00000000u, 0x00000000u, 0x3EBB66D8u, 0x00000000u, 0x3F9AC879u, 0x00000000u,
             0xBE9C9A6Eu, 0x3F7D70A4u, 0xBF18A912u, 0x3F800000u]),
        new(0, -1, "reed-b",
            [0x3F764CC2u, 0x3F161AF7u, 0x3E213DA4u, 0x3F4451C9u, 0x3A13C800u, 0x3E0687D0u, 0x3F302524u],
            [0x3E6C9984u, 0x3F7D70A4u, 0xBEE9E509u, 0xBF0AF4E6u, 0x00000000u, 0x3F16906Eu, 0x00000000u,
             0x00000000u, 0x3F4CE2F8u, 0x00000000u, 0x00000000u, 0xBF16906Eu, 0x00000000u, 0xBF0AF4E6u, 0x00000000u,
             0x3E6C9984u, 0x3F7D70A4u, 0xBEE9E509u, 0x3F800000u]),
        new(1, -1, "reed-a",
            [0x3EF8A74Eu, 0x3F1ECA76u, 0x3EF009F0u, 0x3D71FA00u, 0x3F69E697u, 0x3E8419F2u, 0x3E87DC72u],
            [0x3EFC53A8u, 0x3F7D70A4u, 0xBEE1358Au, 0x3D8AE971u, 0x00000000u, 0x3FAC5A08u, 0x00000000u,
             0x00000000u, 0x3FAC91FAu, 0x00000000u, 0x00000000u, 0xBFAC5A08u, 0x00000000u, 0x3D8AE971u, 0x00000000u,
             0x3EFC53A8u, 0x3F7D70A4u, 0xBEE1358Au, 0x3F800000u]),
        new(2, -1, "reed-a",
            [0x3F25645Bu, 0x3EC74246u, 0x3F7FFF7Au, 0x3EF97E24u, 0x3F141E4Du, 0x3EA056B8u, 0x3E9D9C00u],
            [0x3F895917u, 0x3F7D70A4u, 0xBF0E2F6Eu, 0x3EE30516u, 0x00000000u, 0x3F876C6Fu, 0x00000000u,
             0x00000000u, 0x3F92D5E4u, 0x00000000u, 0x00000000u, 0xBF876C6Fu, 0x00000000u, 0x3EE30516u, 0x00000000u,
             0x3F895917u, 0x3F7D70A4u, 0xBF0E2F6Eu, 0x3F800000u]),
        new(3, -1, "reed-a",
            [0x3F3D3130u, 0x3F5A2372u, 0x3E156EC8u, 0x3F01DB2Du, 0x3E897478u, 0x3E0C4B44u, 0x3F6561ADu],
            [0x3FCF4C4Cu, 0x3F7D70A4u, 0xBEA5DC8Eu, 0xBF205E35u, 0x00000000u, 0x3F3A9732u, 0x00000000u,
             0x00000000u, 0x3F760957u, 0x00000000u, 0x00000000u, 0xBF3A9732u, 0x00000000u, 0xBF205E35u, 0x00000000u,
             0x3FCF4C4Cu, 0x3F7D70A4u, 0xBEA5DC8Eu, 0x3F800000u]),
        new(4, -1, "reed-a",
            [0x3EF0A610u, 0x3EED740Cu, 0x3E662B68u, 0x3F2B0BBEu, 0x3F794B3Au, 0x3F2E3B96u, 0x3EA5AC56u],
            [0x3FFE14C2u, 0x3F7D70A4u, 0xBF04A2FDu, 0x3F15AED0u, 0x00000000u, 0xBFA09B3Au, 0x00000000u,
             0x00000000u, 0x3FB1302Bu, 0x00000000u, 0x00000000u, 0x3FA09B3Au, 0x00000000u, 0x3F15AED0u, 0x00000000u,
             0x3FFE14C2u, 0x3F7D70A4u, 0xBF04A2FDu, 0x3F800000u]),
        new(-4, 0, "reed-a",
            [0x3F64E954u, 0x3F39734Bu, 0x3F006B1Fu, 0x3F0495F2u, 0x3E1C91A8u, 0x3F218B4Fu, 0x3E0CEAC4u],
            [0xBFE6C5ABu, 0x3F7D70A4u, 0x3DE5CD2Cu, 0x3F1B3095u, 0x00000000u, 0xBF276C30u, 0x00000000u,
             0x00000000u, 0x3F64490Cu, 0x00000000u, 0x00000000u, 0x3F276C30u, 0x00000000u, 0x3F1B3095u, 0x00000000u,
             0xBFE6C5ABu, 0x3F7D70A4u, 0x3DE5CD2Cu, 0x3F800000u]),
        new(-3, 0, "reed-a",
            [0x3F4C7586u, 0x3F623F8Bu, 0x3E8B1E52u, 0x3F003148u, 0x3EF38A06u, 0x3F40B3B4u, 0x3F61D2F3u],
            [0xBFACE29Eu, 0x3F7D70A4u, 0x3E447F16u, 0xBC992FABu, 0x00000000u, 0xBF8AE907u, 0x00000000u,
             0x00000000u, 0x3F8AEE4Eu, 0x00000000u, 0x00000000u, 0x3F8AE907u, 0x00000000u, 0xBC992FABu, 0x00000000u,
             0xBFACE29Eu, 0x3F7D70A4u, 0x3E447F16u, 0x3F800000u]),
        new(-2, 0, "reed-a",
            [0x3EA253AAu, 0x3D0578F0u, 0x3F206B71u, 0x3C59B500u, 0x3EAACFB0u, 0x3F37E412u, 0x3F27D2E7u],
            [0xBF8BB58Bu, 0x3F7D70A4u, 0xBE6F50E2u, 0x3E4A7EC4u, 0x00000000u, 0xBF7AFD2Cu, 0x00000000u,
             0x00000000u, 0x3F80058Eu, 0x00000000u, 0x00000000u, 0x3F7AFD2Cu, 0x00000000u, 0x3E4A7EC4u, 0x00000000u,
             0xBF8BB58Bu, 0x3F7D70A4u, 0xBE6F50E2u, 0x3F800000u]),
        new(-1, 0, "reed-a",
            [0x3EA7AF14u, 0x3F0D0BCDu, 0x3D8D0DE0u, 0x3E8388A2u, 0x3F234935u, 0x3CF6BCC0u, 0x3E9AAF14u],
            [0xBF16143Bu, 0x3F7D70A4u, 0x3CD0BCD0u, 0xBF94AEDBu, 0x00000000u, 0x3E63D39Eu, 0x00000000u,
             0x00000000u, 0x3F9762C3u, 0x00000000u, 0x00000000u, 0xBE63D39Eu, 0x00000000u, 0xBF94AEDBu, 0x00000000u,
             0xBF16143Bu, 0x3F7D70A4u, 0x3CD0BCD0u, 0x3F800000u]),
        new(0, 0, "reed-a",
            [0x3F0B330Fu, 0x3ED38AF4u, 0x3E5F57B8u, 0x3CEAA9C0u, 0x3DBA01D8u, 0x3F450E87u, 0x3EE92E68u],
            [0x3CB330F0u, 0x3F7D70A4u, 0xBD31D430u, 0xBDD8A351u, 0x00000000u, 0xBF591164u, 0x00000000u,
             0x00000000u, 0x3F5AC024u, 0x00000000u, 0x00000000u, 0x3F591164u, 0x00000000u, 0xBDD8A351u, 0x00000000u,
             0x3CB330F0u, 0x3F7D70A4u, 0xBD31D430u, 0x3F800000u]),
        new(1, 0, "reed-a",
            [0x3D7B21E0u, 0x3F3A1C0Bu, 0x3DAF1AC8u, 0x3DE9A3A8u, 0x3E1C8090u, 0x3ECDF2F0u, 0x3ED9B536u],
            [0x3E8FB21Eu, 0x3F7D70A4u, 0x3DE8702Cu, 0x3F3A8D57u, 0x00000000u, 0x3F038F43u, 0x00000000u,
             0x00000000u, 0x3F64467Cu, 0x00000000u, 0x00000000u, 0xBF038F43u, 0x00000000u, 0x3F3A8D57u, 0x00000000u,
             0x3E8FB21Eu, 0x3F7D70A4u, 0x3DE8702Cu, 0x3F800000u]),
        new(2, 0, "reed-a",
            [0x3E4EE3B8u, 0x3F1AC662u, 0x3F222A92u, 0x3E02D5F0u, 0x3F388B07u, 0x3F592C82u, 0x3F7DB65Eu],
            [0x3F59DC78u, 0x3F7D70A4u, 0x3D563310u, 0xBF36C810u, 0x00000000u, 0xBF809884u, 0x00000000u,
             0x00000000u, 0x3F9DC34Fu, 0x00000000u, 0x00000000u, 0x3F809884u, 0x00000000u, 0xBF36C810u, 0x00000000u,
             0x3F59DC78u, 0x3F7D70A4u, 0x3D563310u, 0x3F800000u]),
        new(3, 0, "reed-a",
            [0x3F7014ECu, 0x3EA91CA8u, 0x3EBF8EF6u, 0x3EA1B26Cu, 0x3F4C0FF6u, 0x3EE4F5E6u, 0x3B3C6500u],
            [0x3FDC053Bu, 0x3F7D70A4u, 0xBDADC6B0u, 0x3F9AB179u, 0x00000000u, 0x3ED534FDu, 0x00000000u,
             0x00000000u, 0x3FA39E63u, 0x00000000u, 0x00000000u, 0xBED534FDu, 0x00000000u, 0x3F9AB179u, 0x00000000u,
             0x3FDC053Bu, 0x3F7D70A4u, 0xBDADC6B0u, 0x3F800000u]),
        new(-4, 1, "reed-a",
            [0x3F565A75u, 0x3E2DCF24u, 0x3F29EE8Au, 0x3DD202F8u, 0x3D938FC8u, 0x3F283101u, 0x3EC84D4Cu],
            [0xBFEA6963u, 0x3F7D70A4u, 0x3EAB73C8u, 0x3EEE2B5Fu, 0x00000000u, 0xBF340C68u, 0x00000000u,
             0x00000000u, 0x3F57DDFCu, 0x00000000u, 0x00000000u, 0x3F340C68u, 0x00000000u, 0x3EEE2B5Fu, 0x00000000u,
             0xBFEA6963u, 0x3F7D70A4u, 0x3EAB73C8u, 0x3F800000u]),
        new(-3, 1, "reed-a",
            [0x3EA0C474u, 0x3E97388Eu, 0x3E9CCD84u, 0x3E47DD40u, 0x3DE66290u, 0x3F07E431u, 0x3DD9B250u],
            [0xBFCBE772u, 0x3F7D70A4u, 0x3ECB9C48u, 0x3F59ED2Au, 0x00000000u, 0xBE2AFA75u, 0x00000000u,
             0x00000000u, 0x3F5E1431u, 0x00000000u, 0x00000000u, 0x3E2AFA75u, 0x00000000u, 0x3F59ED2Au, 0x00000000u,
             0xBFCBE772u, 0x3F7D70A4u, 0x3ECB9C48u, 0x3F800000u]),
        new(-2, 1, "reed-b",
            [0x3F4EB454u, 0x3E403148u, 0x3E38DC50u, 0x3F58F1D7u, 0x3F627CAFu, 0x3C9948E0u, 0x3F7A6BCFu],
            [0xBF58A5D6u, 0x3F7D70A4u, 0x3EB00C52u, 0xBFA92B90u, 0x00000000u, 0x3E1FD957u, 0x00000000u,
             0x00000000u, 0x3FAA589Au, 0x00000000u, 0x00000000u, 0xBE1FD957u, 0x00000000u, 0xBFA92B90u, 0x00000000u,
             0xBF58A5D6u, 0x3F7D70A4u, 0x3EB00C52u, 0x3F800000u]),
        new(-1, 1, "reed-b",
            [0x3EC630AEu, 0x3EEE24B0u, 0x3F42853Du, 0x3F4428AEu, 0x3E14C04Cu, 0x3E85BB82u, 0x3F1B18E6u],
            [0xBF0E73D4u, 0x3F7D70A4u, 0x3EF71258u, 0x3D7F6B5Au, 0x00000000u, 0x3F628D0Au, 0x00000000u,
             0x00000000u, 0x3F631CD8u, 0x00000000u, 0x00000000u, 0xBF628D0Au, 0x00000000u, 0x3D7F6B5Au, 0x00000000u,
             0xBF0E73D4u, 0x3F7D70A4u, 0x3EF71258u, 0x3F800000u]),
        new(0, 1, "reed-a",
            [0x3E3284ECu, 0x3E65DAB0u, 0x3F732DEAu, 0x3EF52186u, 0x3F169A8Du, 0x3E0950C4u, 0x3F490ED3u],
            [0xBE26BD8Au, 0x3F7D70A4u, 0x3EB976ACu, 0xBF447275u, 0x00000000u, 0x3F5C4B2Bu, 0x00000000u,
             0x00000000u, 0x3F9394C4u, 0x00000000u, 0x00000000u, 0xBF5C4B2Bu, 0x00000000u, 0xBF447275u, 0x00000000u,
             0xBE26BD8Au, 0x3F7D70A4u, 0x3EB976ACu, 0x3F800000u]),
        new(1, 1, "reed-b",
            [0x3E8442A2u, 0x3F273D09u, 0x3E87965Au, 0x3F4A677Cu, 0x3F7B0A92u, 0x3D682F30u, 0x3F4ACEB3u],
            [0x3EC22150u, 0x3F7D70A4u, 0x3F139E84u, 0xBFA68F2Bu, 0x00000000u, 0x3EF7DC96u, 0x00000000u,
             0x00000000u, 0x3FB1B65Fu, 0x00000000u, 0x00000000u, 0xBEF7DC96u, 0x00000000u, 0xBFA68F2Bu, 0x00000000u,
             0x3EC22150u, 0x3F7D70A4u, 0x3F139E84u, 0x3F800000u]),
        new(2, 1, "reed-a",
            [0x3E603B5Cu, 0x3F62EBD7u, 0x3E732728u, 0x3EAB94EEu, 0x3F15B731u, 0x3F60D82Du, 0x3F36BBCAu],
            [0x3F5C076Cu, 0x3F7D70A4u, 0x3F3175ECu, 0xBF549B71u, 0x00000000u, 0xBF4BF8DDu, 0x00000000u,
             0x00000000u, 0x3F93508Eu, 0x00000000u, 0x00000000u, 0x3F4BF8DDu, 0x00000000u, 0xBF549B71u, 0x00000000u,
             0x3F5C076Cu, 0x3F7D70A4u, 0x3F3175ECu, 0x3F800000u]),
        new(3, 1, "reed-a",
            [0x3EA7B9B2u, 0x3E6DB164u, 0x3F50281Cu, 0x3DD200D0u, 0x3EF85538u, 0x3F364DB8u, 0x3EB8933Cu],
            [0x3FB4F736u, 0x3F7D70A4u, 0x3EBB6C58u, 0x3E83AFF7u, 0x00000000u, 0xBF87B6C0u, 0x00000000u,
             0x00000000u, 0x3F8BA662u, 0x00000000u, 0x00000000u, 0x3F87B6C0u, 0x00000000u, 0x3E83AFF7u, 0x00000000u,
             0x3FB4F736u, 0x3F7D70A4u, 0x3EBB6C58u, 0x3F800000u]),
        new(-4, 2, "reed-b",
            [0x3F0CC426u, 0x3F13522Fu, 0x3E901C54u, 0x3F53E08Du, 0x3E58D6ECu, 0x3E9F74C0u, 0x3F22A857u],
            [0xBFFCCEF6u, 0x3F7D70A4u, 0x3F84D48Cu, 0x3EB2B5CEu, 0x00000000u, 0x3F5BDCA7u, 0x00000000u,
             0x00000000u, 0x3F6D5370u, 0x00000000u, 0x00000000u, 0xBF5BDCA7u, 0x00000000u, 0x3EB2B5CEu, 0x00000000u,
             0xBFFCCEF6u, 0x3F7D70A4u, 0x3F84D48Cu, 0x3F800000u]),
        new(-3, 2, "reed-a",
            [0x3F04D370u, 0x3E54C268u, 0x3EE4F4BCu, 0x3F2FC1AFu, 0x3E815C36u, 0x3EF5ECA8u, 0x3E3D93E8u],
            [0xBFBECB24u, 0x3F7D70A4u, 0x3F5A984Cu, 0x3F71BF8Eu, 0x00000000u, 0x3DF05AEDu, 0x00000000u,
             0x00000000u, 0x3F739BAAu, 0x00000000u, 0x00000000u, 0xBDF05AEDu, 0x00000000u, 0x3F71BF8Eu, 0x00000000u,
             0xBFBECB24u, 0x3F7D70A4u, 0x3F5A984Cu, 0x3F800000u]),
        new(-2, 2, "reed-a",
            [0x3E7DBD18u, 0x3F489AF3u, 0x3EB5F06Au, 0x3EC90902u, 0x3F2A814Fu, 0x3F31B5A7u, 0x3F08D20Au],
            [0xBF90242Eu, 0x3F7D70A4u, 0x3F9226BDu, 0x3ED30959u, 0x00000000u, 0xBF903401u, 0x00000000u,
             0x00000000u, 0x3F998D31u, 0x00000000u, 0x00000000u, 0x3F903401u, 0x00000000u, 0x3ED30959u, 0x00000000u,
             0xBF90242Eu, 0x3F7D70A4u, 0x3F9226BDu, 0x3F800000u]),
        new(-1, 2, "reed-a",
            [0x3F4C19F4u, 0x3F6EDD15u, 0x3EACEDB2u, 0x3ED7E878u, 0x3F19C542u, 0x3E922972u, 0x3F4D020Bu],
            [0xBEB3E60Cu, 0x3F7D70A4u, 0x3F9BB745u, 0x3E83532Eu, 0x00000000u, 0x3F90DB73u, 0x00000000u,
             0x00000000u, 0x3F9487FAu, 0x00000000u, 0x00000000u, 0xBF90DB73u, 0x00000000u, 0x3E83532Eu, 0x00000000u,
             0xBEB3E60Cu, 0x3F7D70A4u, 0x3F9BB745u, 0x3F800000u]),
        new(0, 2, "reed-a",
            [0x3E621A20u, 0x3F412FCAu, 0x3ED6DE98u, 0x3F00E74Bu, 0x3E0F43C0u, 0x3EA1E590u, 0x3F695B8Cu],
            [0xBE0EF2F0u, 0x3F7D70A4u, 0x3F904BF2u, 0x3EB6E11Fu, 0x00000000u, 0x3F4EFE07u, 0x00000000u,
             0x00000000u, 0x3F624A2Au, 0x00000000u, 0x00000000u, 0xBF4EFE07u, 0x00000000u, 0x3EB6E11Fu, 0x00000000u,
             0xBE0EF2F0u, 0x3F7D70A4u, 0x3F904BF2u, 0x3F800000u]),
        new(1, 2, "reed-a",
            [0x3EF09284u, 0x3F18CAC4u, 0x3ECBAA7Cu, 0x3EC87B7Eu, 0x3F06DC97u, 0x3F2DF169u, 0x3F221059u],
            [0x3EF84942u, 0x3F7D70A4u, 0x3F8632B1u, 0x3EF50AB9u, 0x00000000u, 0xBF810E97u, 0x00000000u,
             0x00000000u, 0x3F8EDBC7u, 0x00000000u, 0x00000000u, 0x3F810E97u, 0x00000000u, 0x3EF50AB9u, 0x00000000u,
             0x3EF84942u, 0x3F7D70A4u, 0x3F8632B1u, 0x3F800000u]),
        new(2, 2, "reed-b",
            [0x3F0310C0u, 0x3DD32780u, 0x3E3E6F28u, 0x3F6D87ACu, 0x3F7D5943u, 0x3F0E1CDEu, 0x3F4DD6E1u],
            [0x3F80C430u, 0x3F7D70A4u, 0x3F4D3278u, 0x3FA7CF0Eu, 0x00000000u, 0xBEF244FDu, 0x00000000u,
             0x00000000u, 0x3FB26794u, 0x00000000u, 0x00000000u, 0x3EF244FDu, 0x00000000u, 0x3FA7CF0Eu, 0x00000000u,
             0x3F80C430u, 0x3F7D70A4u, 0x3F4D3278u, 0x3F800000u]),
        new(3, 2, "reed-a",
            [0x3F57ABB6u, 0x3E5BD238u, 0x3F0AE026u, 0x3D342A30u, 0x3F133B3Eu, 0x3E7F6844u, 0x3F6FD392u],
            [0x3FD5EAEEu, 0x3F7D70A4u, 0x3F5B7A48u, 0xBB887569u, 0x00000000u, 0x3F929186u, 0x00000000u,
             0x00000000u, 0x3F9291C6u, 0x00000000u, 0x00000000u, 0xBF929186u, 0x00000000u, 0xBB887569u, 0x00000000u,
             0x3FD5EAEEu, 0x3F7D70A4u, 0x3F5B7A48u, 0x3F800000u]),
        new(4, 2, "reed-a",
            [0x3E8F6FAAu, 0x3F6E3F65u, 0x3F351D69u, 0x3F3CC432u, 0x3F280D4Du, 0x3F105B95u, 0x3ED2E3BEu],
            [0x3FF1EDF6u, 0x3F7D70A4u, 0x3F9B8FD9u, 0x3F8CAA0Eu, 0x00000000u, 0xBEEEDE98u, 0x00000000u,
             0x00000000u, 0x3F98D0CAu, 0x00000000u, 0x00000000u, 0x3EEEDE98u, 0x00000000u, 0x3F8CAA0Eu, 0x00000000u,
             0x3FF1EDF6u, 0x3F7D70A4u, 0x3F9B8FD9u, 0x3F800000u]),
        new(-3, 3, "reed-a",
            [0x3EA79820u, 0x3F6FC7B2u, 0x3EF2FF42u, 0x3F34484Fu, 0x3E53A040u, 0x3EAA591Cu, 0x3F33EFA3u],
            [0xBFCB0CFCu, 0x3F7D70A4u, 0x3FDBF1ECu, 0x3EEAF0BAu, 0x00000000u, 0x3F4D5059u, 0x00000000u,
             0x00000000u, 0x3F6C8B3Du, 0x00000000u, 0x00000000u, 0xBF4D5059u, 0x00000000u, 0x3EEAF0BAu, 0x00000000u,
             0xBFCB0CFCu, 0x3F7D70A4u, 0x3FDBF1ECu, 0x3F800000u]),
        new(-2, 3, "reed-a",
            [0x3F5F722Du, 0x3E7551D0u, 0x3DEF59C0u, 0x3DC795A8u, 0x3E85C682u, 0x3E3D82B0u, 0x3EEF617Cu],
            [0xBF5046EAu, 0x3F7D70A4u, 0x3FAF551Du, 0xBEC25ABEu, 0x00000000u, 0x3F60D483u, 0x00000000u,
             0x00000000u, 0x3F74EEC1u, 0x00000000u, 0x00000000u, 0xBF60D483u, 0x00000000u, 0xBEC25ABEu, 0x00000000u,
             0xBF5046EAu, 0x3F7D70A4u, 0x3FAF551Du, 0x3F800000u]),
        new(-1, 3, "reed-a",
            [0x3DBB8130u, 0x3EF21184u, 0x3F4A2C4Cu, 0x3EE976ACu, 0x3ED70740u, 0x3F1114AFu, 0x3F2A04B0u],
            [0xBF3447EDu, 0x3F7D70A4u, 0x3FBE4230u, 0x3F75FCD0u, 0x00000000u, 0xBEDB3EF3u, 0x00000000u,
             0x00000000u, 0x3F86A77Du, 0x00000000u, 0x00000000u, 0x3EDB3EF3u, 0x00000000u, 0x3F75FCD0u, 0x00000000u,
             0xBF3447EDu, 0x3F7D70A4u, 0x3FBE4230u, 0x3F800000u]),
        new(0, 3, "reed-a",
            [0x3F791E6Fu, 0x3F2870AEu, 0x3F0B6A94u, 0x3ED7C26Cu, 0x3F76BB5Cu, 0x3F335737u, 0x3D5DA310u],
            [0x3E723CDEu, 0x3F7D70A4u, 0x3FCA1C2Cu, 0x3ED7BF6Bu, 0x00000000u, 0xBFA7F8E3u, 0x00000000u,
             0x00000000u, 0x3FB06B68u, 0x00000000u, 0x00000000u, 0x3FA7F8E3u, 0x00000000u, 0x3ED7BF6Bu, 0x00000000u,
             0x3E723CDEu, 0x3F7D70A4u, 0x3FCA1C2Cu, 0x3F800000u]),
        new(1, 3, "reed-a",
            [0x3EE6F2E6u, 0x3F021632u, 0x3F02C33Cu, 0x3E288334u, 0x3F29022Au, 0x3F000E1Du, 0x3E1CB9A4u],
            [0x3EF37974u, 0x3F7D70A4u, 0x3FC0858Cu, 0x3F991A37u, 0x00000000u, 0xBAD4230Cu, 0x00000000u,
             0x00000000u, 0x3F991A40u, 0x00000000u, 0x00000000u, 0x3AD4230Cu, 0x00000000u, 0x3F991A37u, 0x00000000u,
             0x3EF37974u, 0x3F7D70A4u, 0x3FC0858Cu, 0x3F800000u]),
        new(2, 3, "reed-a",
            [0x3F515592u, 0x3F1C711Cu, 0x3F54F686u, 0x3EDF2ADEu, 0x3E071290u, 0x3F1E4382u, 0x3F09AC74u],
            [0x3F945564u, 0x3F7D70A4u, 0x3FC71C47u, 0x3F25C703u, 0x00000000u, 0xBF1837C2u, 0x00000000u,
             0x00000000u, 0x3F610F96u, 0x00000000u, 0x00000000u, 0x3F1837C2u, 0x00000000u, 0x3F25C703u, 0x00000000u,
             0x3F945564u, 0x3F7D70A4u, 0x3FC71C47u, 0x3F800000u]),
        new(3, 3, "reed-a",
            [0x3F655D93u, 0x3F26DBABu, 0x3F566188u, 0x3F35CF9Au, 0x3DE1F7F8u, 0x3F4EA30Fu, 0x3EBE41AAu],
            [0x3FD95765u, 0x3F7D70A4u, 0x3FC9B6EBu, 0xBE9BEB22u, 0x00000000u, 0xBF4F9784u, 0x00000000u,
             0x00000000u, 0x3F5DBF66u, 0x00000000u, 0x00000000u, 0x3F4F9784u, 0x00000000u, 0xBE9BEB22u, 0x00000000u,
             0x3FD95765u, 0x3F7D70A4u, 0x3FC9B6EBu, 0x3F800000u]),
        new(4, 3, "reed-a",
            [0x3D2423E0u, 0x3EF490ACu, 0x3F3E8BFCu, 0x3E8E22AAu, 0x3F127452u, 0x3EDB7BF2u, 0x3F4346DBu],
            [0x3FE29090u, 0x3F7D70A4u, 0x3FBE9216u, 0x3F83E343u, 0x00000000u, 0x3EFD9CA5u, 0x00000000u,
             0x00000000u, 0x3F925618u, 0x00000000u, 0x00000000u, 0xBEFD9CA5u, 0x00000000u, 0x3F83E343u, 0x00000000u,
             0x3FE29090u, 0x3F7D70A4u, 0x3FBE9216u, 0x3F800000u]),
        new(-3, 4, "reed-a",
            [0x3C3AE580u, 0x3EDCF4F0u, 0x3E1EC520u, 0x3E2837E8u, 0x3EF42B78u, 0x3EA6B71Eu, 0x3E17EA34u],
            [0xBFDF451Au, 0x3F7D70A4u, 0x3FFB9E9Eu, 0x3EFE615Du, 0x00000000u, 0x3F774141u, 0x00000000u,
             0x00000000u, 0x3F8B0685u, 0x00000000u, 0x00000000u, 0xBF774141u, 0x00000000u, 0x3EFE615Du, 0x00000000u,
             0xBFDF451Au, 0x3F7D70A4u, 0x3FFB9E9Eu, 0x3F800000u]),
        new(-1, 4, "reed-a",
            [0x3EFCD93Eu, 0x3C9831A0u, 0x3F12038Cu, 0x3E3C3654u, 0x3E98EA50u, 0x3ECA52C2u, 0x3EA9174Eu],
            [0xBF00C9B0u, 0x3F7D70A4u, 0x3FE13064u, 0x3F463A8Cu, 0x00000000u, 0x3F19700Fu, 0x00000000u,
             0x00000000u, 0x3F7AACB2u, 0x00000000u, 0x00000000u, 0xBF19700Fu, 0x00000000u, 0x3F463A8Cu, 0x00000000u,
             0xBF00C9B0u, 0x3F7D70A4u, 0x3FE13064u, 0x3F800000u]),
        new(3, 4, "reed-a",
            [0x3E12CDF4u, 0x3EBF6ABEu, 0x3E20C478u, 0x3F26C741u, 0x3E9DD4C0u, 0x3F0C0B28u, 0x3E87089Cu],
            [0x3FA92CDFu, 0x3F7D70A4u, 0x3FF7ED58u, 0x3F7136A3u, 0x00000000u, 0xBE92E805u, 0x00000000u,
             0x00000000u, 0x3F7C263Au, 0x00000000u, 0x00000000u, 0x3E92E805u, 0x00000000u, 0x3F7136A3u, 0x00000000u,
             0x3FA92CDFu, 0x3F7D70A4u, 0x3FF7ED58u, 0x3F800000u]),
    ];
}
