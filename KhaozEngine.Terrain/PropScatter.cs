using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>One scattered prop instance: which kit <see cref="Id"/>, the world position (<see cref="X"/>,
    /// <see cref="Y"/>, <see cref="Z"/> with Y from the field), and the per-instance <see cref="Scale"/>,
    /// <see cref="Yaw"/> (radians), and <see cref="Variant"/> (the chosen kind index). Render-free placement
    /// data - the 3D arm (KhaozEngine.Terrain.Render3D) turns it into instanced draws.</summary>
    public readonly struct PropPlacement
    {
        public string Id { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public float Scale { get; }
        public float Yaw { get; }
        public int Variant { get; }
        public PropPlacement(string id, float x, float y, float z, float scale, float yaw, int variant)
        {
            Id = id; X = x; Y = y; Z = z; Scale = scale; Yaw = yaw; Variant = variant;
        }
    }

    /// <summary>One weighted kit id in a biome's kind mix.</summary>
    public readonly struct PropKind
    {
        public string Id { get; }
        public float Weight { get; }
        public PropKind(string id, float weight) { Id = id; Weight = weight; }
    }

    /// <summary>Per-biome scatter rule: how densely to place (the keep probability) and which weighted kit ids to
    /// pick from. A cell whose dominant biome has no rule places nothing.</summary>
    public sealed class BiomeScatterRule
    {
        public BiomeId Biome;
        /// <summary>Keep probability per candidate cell, 0..1 (the greybox <c>forest_keep</c>).</summary>
        public float Density = 0.55f;
        public PropKind[] Kinds = Array.Empty<PropKind>();
    }

    /// <summary>An axis-aligned XZ query window for <see cref="PropScatter.Generate"/>. Cells are included by their
    /// (un-jittered) centre using half-open intervals [Min, Max), so tiling the world into adjacent areas
    /// produces each cell exactly once (streaming-ready).</summary>
    public readonly struct RectArea
    {
        public float MinX { get; }
        public float MinZ { get; }
        public float MaxX { get; }
        public float MaxZ { get; }
        public RectArea(float minX, float minZ, float maxX, float maxZ)
        {
            MinX = minX; MinZ = minZ; MaxX = maxX; MaxZ = maxZ;
        }
    }

    /// <summary>Data-driven inputs for <see cref="PropScatter.Generate"/>: a jittered grid with per-biome density +
    /// kind mix, exclusions (below water / inside a clearing radius / above a height cap), and per-instance
    /// scale/yaw/variant from independent coordinate hashes. <see cref="ForestRing"/> reproduces the greybox
    /// clearing's forest ring (tools/blender/make_clearing_greybox.py).</summary>
    public sealed class ScatterConfig
    {
        public int Seed = 1337;
        /// <summary>Grid spacing in metres (the greybox <c>forest_step_m</c> = 4.5).</summary>
        public float CellSize = 4.5f;
        /// <summary>Max per-cell positional jitter in metres (the greybox uniform(-1.6, 1.6)).</summary>
        public float Jitter = 1.6f;
        /// <summary>Radius (m) around <see cref="ClearingCenter"/> kept free of props (the open clearing/road).</summary>
        public float ClearingRadius = 26f;
        public Vector2 ClearingCenter = Vector2.Zero;
        /// <summary>Skip candidates whose ground height exceeds this (keeps props off the mountain); null = no cap.</summary>
        public float? MaxHeight = 6f;
        public float ScaleMin = 0.8f;
        public float ScaleMax = 1.35f;
        public BiomeScatterRule[] Biomes = Array.Empty<BiomeScatterRule>();

        /// <summary>Defaults reproducing the greybox forest ring: a single Meadow rule (density 0.55) over the
        /// committed CC0 kit (pines dominant, oaks fewer, rocks sparse), clearing radius 26 m, off-mountain at
        /// height &gt; 6 m, scale 0.8..1.35.</summary>
        public static ScatterConfig ForestRing(int seed = 1337) => new ScatterConfig
        {
            Seed = seed,
            CellSize = 4.5f,
            Jitter = 1.6f,
            ClearingRadius = 26f,
            ClearingCenter = Vector2.Zero,
            MaxHeight = 6f,
            ScaleMin = 0.8f,
            ScaleMax = 1.35f,
            Biomes = new[]
            {
                new BiomeScatterRule
                {
                    Biome = BiomeId.Meadow,
                    Density = 0.55f,
                    Kinds = new[]
                    {
                        new PropKind("pine_a", 0.26f),
                        new PropKind("pine_b", 0.20f),
                        new PropKind("pine_c", 0.16f),
                        new PropKind("oak_a", 0.12f),
                        new PropKind("oak_b", 0.10f),
                        new PropKind("rock_a", 0.09f),
                        new PropKind("rock_b", 0.07f),
                    },
                },
            },
        };
    }

    /// <summary>Inputs for <see cref="PropScatter.GenerateCompanions"/>: rings each host prop whose
    /// <see cref="PropPlacement.Id"/> is in <see cref="HostKinds"/> with a few small-foliage instances, so
    /// trees are dressed at the base instead of standing on bare ground. Every value (count, ring angle/radius,
    /// kind, scale, yaw) hashes off the host's centimetre-quantized world XZ + per-channel salts, so it is
    /// deterministic and tiling-invariant (the host set is tiling-invariant and each host maps independently to
    /// its companions). Render-only: companion ids carry no collider.</summary>
    public sealed class CompanionConfig
    {
        public int Seed = 1337;
        /// <summary>Host ids that spawn companions (e.g. the tree kit ids). A host whose Id is not here spawns none.</summary>
        public string[] HostKinds = Array.Empty<string>();
        /// <summary>Weighted companion kit ids (bush / fern / ...).</summary>
        public PropKind[] Kinds = Array.Empty<PropKind>();
        public int CountMin = 2;
        public int CountMax = 4;
        /// <summary>Ring offset from the host base, metres.</summary>
        public float RadiusMin = 0.6f;
        public float RadiusMax = 1.8f;
        public float ScaleMin = 0.7f;
        public float ScaleMax = 1.1f;
        /// <summary>Skip a companion whose resampled ground height exceeds this (same off-mountain exclusion as
        /// the host layer); null = no cap.</summary>
        public float? MaxHeight;
    }

    /// <summary>Deterministic coordinate-hash prop scatter over the analytic terrain field. Every placement for a
    /// grid cell depends only on (cell, seed) - never on call order or which neighbouring cells are queried - so
    /// <see cref="Generate"/> over a large area equals the union of <see cref="Generate"/> over its tiles
    /// (streaming-ready). Render-free: it needs only the field's height/biome/water and the shared
    /// <see cref="TerrainNoise.Hash2"/>.</summary>
    public static class PropScatter
    {
        // Independent hash channels (XOR salts) so position-jitter, density, kind, scale, and yaw are uncorrelated.
        const int SaltJitterX = 0x1A2B3C4D;
        const int SaltJitterZ = 0x5E6F7081;
        const int SaltDensity = 0x2351F7A9;
        const int SaltKind = 0x77C0FFEE;
        const int SaltScale = 0x0BADF00D;
        const int SaltYaw = 0x13579BDF;

        // Independent companion hash channels (distinct from the scatter salts above).
        const int SaltCompanionCount = 0x2C1B3A4D;
        const int SaltCompanionAngle = 0x6E5F7081;
        const int SaltCompanionRadius = 0x3461F8B2;
        const int SaltCompanionKind = 0x51C0FFEE;
        const int SaltCompanionScale = 0x1ADF00D5;
        const int SaltCompanionYaw = 0x24681357;

        /// <summary>Generate the placements whose cell centres fall in <paramref name="area"/>. Identical regardless
        /// of area tiling.</summary>
        public static IReadOnlyList<PropPlacement> Generate(TerrainField field, ScatterConfig config, RectArea area)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (config == null) throw new ArgumentNullException(nameof(config));

            float cs = config.CellSize;
            if (cs <= 0f) throw new ArgumentException("ScatterConfig.CellSize must be positive.", nameof(config));

            var result = new List<PropPlacement>();

            int gxLo = (int)MathF.Floor(area.MinX / cs);
            int gxHi = (int)MathF.Ceiling(area.MaxX / cs);
            int gzLo = (int)MathF.Floor(area.MinZ / cs);
            int gzHi = (int)MathF.Ceiling(area.MaxZ / cs);

            for (int gz = gzLo; gz <= gzHi; gz++)
            {
                float cz = gz * cs;
                if (cz < area.MinZ || cz >= area.MaxZ) continue;        // half-open -> tiling-invariant
                for (int gx = gxLo; gx <= gxHi; gx++)
                {
                    float cx = gx * cs;
                    if (cx < area.MinX || cx >= area.MaxX) continue;

                    float x = cx + Hash01(gx, gz, config.Seed, SaltJitterX) * 2f * config.Jitter - config.Jitter;
                    float z = cz + Hash01(gx, gz, config.Seed, SaltJitterZ) * 2f * config.Jitter - config.Jitter;

                    BiomeScatterRule? rule = RuleFor(config, field.SampleBiome(x, z));
                    if (rule == null || rule.Kinds.Length == 0) continue;

                    if (Hash01(gx, gz, config.Seed, SaltDensity) >= rule.Density) continue;

                    float y = field.SampleHeight(x, z);
                    if (y < field.WaterLevel) continue;
                    if (config.MaxHeight is float cap && y > cap) continue;

                    float dx = x - config.ClearingCenter.X, dz = z - config.ClearingCenter.Y;
                    if (dx * dx + dz * dz < config.ClearingRadius * config.ClearingRadius) continue;

                    int variant = PickKind(rule.Kinds, Hash01(gx, gz, config.Seed, SaltKind));
                    float scale = config.ScaleMin + Hash01(gx, gz, config.Seed, SaltScale) * (config.ScaleMax - config.ScaleMin);
                    float yaw = Hash01(gx, gz, config.Seed, SaltYaw) * MathF.Tau;

                    result.Add(new PropPlacement(rule.Kinds[variant].Id, x, y, z, scale, yaw, variant));
                }
            }
            return result;
        }

        /// <summary>Ring each host whose <see cref="PropPlacement.Id"/> is in <paramref name="config"/>'s
        /// <see cref="CompanionConfig.HostKinds"/> with <c>Count</c> small-foliage companions in a jittered ring,
        /// Y resampled from the field. Pure per-host: count/angle/radius/kind/scale/yaw hash off the host's
        /// centimetre-quantized world XZ + per-channel salts (never the host's list index, which is not
        /// tiling-invariant), so the result is deterministic and the union over any tiling of the hosts equals
        /// the whole. Render-only - companion placements carry no collider.</summary>
        public static IReadOnlyList<PropPlacement> GenerateCompanions(
            TerrainField field, IReadOnlyList<PropPlacement> hosts, CompanionConfig config)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (hosts == null) throw new ArgumentNullException(nameof(hosts));
            if (config == null) throw new ArgumentNullException(nameof(config));

            var result = new List<PropPlacement>();
            if (config.Kinds.Length == 0 || config.HostKinds.Length == 0 || config.CountMax < config.CountMin)
                return result;

            int span = config.CountMax - config.CountMin + 1;

            for (int i = 0; i < hosts.Count; i++)
            {
                PropPlacement host = hosts[i];
                if (!IsHostKind(config.HostKinds, host.Id)) continue;

                // Centimetre-quantized host position is the stable, tiling-invariant per-host hash key.
                int hx = (int)MathF.Round(host.X * 100f);
                int hz = (int)MathF.Round(host.Z * 100f);

                int count = config.CountMin + (int)(CompanionHash01(hx, hz, config.Seed, SaltCompanionCount, 0) * span);
                if (count > config.CountMax) count = config.CountMax;   // hash maps to [0,1]; keep count within the inclusive bound

                for (int j = 0; j < count; j++)
                {
                    float angle = CompanionHash01(hx, hz, config.Seed, SaltCompanionAngle, j) * MathF.Tau;
                    float radius = config.RadiusMin
                                   + CompanionHash01(hx, hz, config.Seed, SaltCompanionRadius, j) * (config.RadiusMax - config.RadiusMin);
                    float x = host.X + radius * MathF.Cos(angle);
                    float z = host.Z + radius * MathF.Sin(angle);

                    float y = field.SampleHeight(x, z);
                    if (config.MaxHeight is float cap && y > cap) continue;

                    int variant = PickKind(config.Kinds, CompanionHash01(hx, hz, config.Seed, SaltCompanionKind, j));
                    float scale = config.ScaleMin
                                  + CompanionHash01(hx, hz, config.Seed, SaltCompanionScale, j) * (config.ScaleMax - config.ScaleMin);
                    float yaw = CompanionHash01(hx, hz, config.Seed, SaltCompanionYaw, j) * MathF.Tau;

                    result.Add(new PropPlacement(config.Kinds[variant].Id, x, y, z, scale, yaw, variant));
                }
            }
            return result;
        }

        static bool IsHostKind(string[] hostKinds, string id)
        {
            for (int i = 0; i < hostKinds.Length; i++)
                if (string.Equals(hostKinds[i], id, StringComparison.Ordinal)) return true;
            return false;
        }

        // Per-host, per-companion hash channel: mixes the companion index j into seed^salt so a host's N
        // companions are uncorrelated. Returns [0, 1).
        static float CompanionHash01(int hx, int hz, int seed, int salt, int j)
        {
            unchecked
            {
                int mixed = (int)((uint)(seed ^ salt) ^ ((uint)j * 0x9E3779B1u));
                return TerrainNoise.Hash2(hx, hz, mixed) * 0.5f + 0.5f;
            }
        }

        // Hash2 returns [-1, 1); map to [0, 1).
        static float Hash01(int gx, int gz, int seed, int salt) => TerrainNoise.Hash2(gx, gz, seed ^ salt) * 0.5f + 0.5f;

        static BiomeScatterRule? RuleFor(ScatterConfig config, BiomeId biome)
        {
            BiomeScatterRule[] rules = config.Biomes;
            for (int i = 0; i < rules.Length; i++)
                if (rules[i].Biome == biome) return rules[i];
            return null;
        }

        // Weighted pick over the kind mix using u in [0,1); returns the chosen kind index.
        static int PickKind(PropKind[] kinds, float u)
        {
            float total = 0f;
            for (int i = 0; i < kinds.Length; i++) total += MathF.Max(0f, kinds[i].Weight);
            if (total <= 0f) return 0;

            float t = u * total, acc = 0f;
            for (int i = 0; i < kinds.Length; i++)
            {
                acc += MathF.Max(0f, kinds[i].Weight);
                if (t < acc) return i;
            }
            return kinds.Length - 1;
        }
    }
}
