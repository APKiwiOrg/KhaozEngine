using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>Full-file goldens of serial Contact navigation bakes over the tile medium sampler, the producer that
/// implements the water boundary reach certificate. A bake always takes the full water solve, so these files must not
/// change when runtime movement answers a proof from the certificate.</summary>
public class ContactTileWaterNavigationBaselineTests(ITestOutputHelper output)
{
    const string World = "probe-world";

    // SHA-256 of the whole bake file: header, identity and payload. Every value was baked at e937df491, the commit
    // before the water boundary certificate, whose engine version was 24.2.0. The identity block names the engine
    // version and a terrain Drop payload binds a digest of that block, so the test rebinds both to 24.2.0 before
    // hashing and the pins stay the base commit's raw file hashes across version bumps. A failing row means the bake
    // or one of these fixtures changed. Do not re-pin it to the new output. Restore the fixture, or treat the row as a
    // bake change. The 30 rows without terrain Drops are one file on Arm64 and x64. A terrain Drop row holds its Arm64
    // file here and its x64 file in X64TerrainDrops.
    const string BaseEngineVersion = "24.2.0";
    public static TheoryData<string, WaterTraversalMode, string, string> Baselines => new()
    {
        { "census-bank", WaterTraversalMode.SurfaceSwimmer, "no-drop", "006b1de5f28998c5fbfe186135c2ed11d72e1e4539843704e12c66f05113e0ae" },
        { "census-bank", WaterTraversalMode.SurfaceSwimmer, "drop", "693943dba03b4391daf355b8de22d83c6187db4abb23a7c87bfe37726c10117e" },
        { "census-bank", WaterTraversalMode.SurfaceSwimmer, "terrain-drop", "859bc5371d0a4ac94206778cfb34b02a954c17292eebb28a71c29c9c8c8ec2d2" },
        { "census-bank", WaterTraversalMode.WadeOnly, "no-drop", "3c8fc6264a9874bfa2c4e620249dce0efcacb076f5ab833f3d42325201674c92" },
        { "census-bank", WaterTraversalMode.WadeOnly, "drop", "2e3f2e6350b594053ee9d95ed074afb6ac27fe075625f711172fad4b1e44ea0d" },
        { "census-bank", WaterTraversalMode.WadeOnly, "terrain-drop", "8d16fd41be3356cc49b9816a78ac0c8e788571adeecc237884f0ea005aa01330" },
        { "census-bank", WaterTraversalMode.DryOnly, "no-drop", "78841c5b3f20fd8dfeccd8f08b2c42036e7be4bfa92d261f44504b93bc695d7a" },
        { "census-bank", WaterTraversalMode.DryOnly, "drop", "fbc9b2a71befd69f15161c2f1ab49057992c571b37682edbf68d48ad43af5fee" },
        { "census-bank", WaterTraversalMode.DryOnly, "terrain-drop", "2e9545eda1889d4756ba43ec70008df3a195f361238b1b826cab1b66c8019ba8" },
        { "shore-1697", WaterTraversalMode.SurfaceSwimmer, "no-drop", "04f4992e14935d9e5e1b0cc07be1b29513e3af5b50535c4cdae12f6346f5a995" },
        { "shore-1697", WaterTraversalMode.SurfaceSwimmer, "drop", "c14ad6069ba1b4cbd666c9339f4fca64fa51f21e0158c4be9ac90831509d2d1a" },
        { "shore-1697", WaterTraversalMode.SurfaceSwimmer, "terrain-drop", "b61f19405b29b9d9c4fe084e88e54d161af5ed2c673bcc52ae9ca925a58381f0" },
        { "shore-1697", WaterTraversalMode.WadeOnly, "no-drop", "16e78219c8b2d68c3722fef6b2a7126fe838c482977ee5bf00773cdfd70bcc44" },
        { "shore-1697", WaterTraversalMode.WadeOnly, "drop", "3b6a441c4babb8c0706ca9b0286bb6182080eb2884a81ff44f55326d67356f8d" },
        { "shore-1697", WaterTraversalMode.WadeOnly, "terrain-drop", "2b3aac00a20f7d4c5e1b263cb5063206cb32171ee2f64a17e823010349005155" },
        { "shore-1697", WaterTraversalMode.DryOnly, "no-drop", "745da3139eb58b38c645e254bbf571ed5ac3629607f3119ff4324c87521acc7d" },
        { "shore-1697", WaterTraversalMode.DryOnly, "drop", "add8a95949db73425c292cb4478957cb514d563252985980b64c6a58e6623fdd" },
        { "shore-1697", WaterTraversalMode.DryOnly, "terrain-drop", "9410ed90ab6b12046acf2e06ee7c745b1d8469775cb9c9cd8a98d5484e936563" },
        { "drawn-bed-sloped", WaterTraversalMode.SurfaceSwimmer, "no-drop", "57b6810c2fa232d50ca8665c9371b1323d1745e47d63712ae554fc0505980fe1" },
        { "drawn-bed-sloped", WaterTraversalMode.SurfaceSwimmer, "drop", "a0238d22cf2b3fb87799534dade1ac1cdcb7e852a853f2ae4017acc6b38a230e" },
        { "drawn-bed-sloped", WaterTraversalMode.SurfaceSwimmer, "terrain-drop", "df44a9ec90506a508e0449f3fd49366918522d50bad64d472d03e36186f67b25" },
        { "drawn-bed-sloped", WaterTraversalMode.WadeOnly, "no-drop", "6007ce41e680715ea12f563d81843c9168208f19697414cd28d5aebfe169380d" },
        { "drawn-bed-sloped", WaterTraversalMode.WadeOnly, "drop", "91530ed84c6c014deea55a20b710b0b2eb098c020bdf0642e70da52c1c156d98" },
        { "drawn-bed-sloped", WaterTraversalMode.WadeOnly, "terrain-drop", "484c63640e26a9c5af91498de381d1086d6573cb28bb396f27460ed5712ca86c" },
        { "drawn-bed-sloped", WaterTraversalMode.DryOnly, "no-drop", "31363db75d5642da9ebe40568d6706983306b9c75fe601bdeb48e708b1cddc7a" },
        { "drawn-bed-sloped", WaterTraversalMode.DryOnly, "drop", "4f26974b8322128233007482dc5cdba50c4c3432c6a64d28d497f81111852aa1" },
        { "drawn-bed-sloped", WaterTraversalMode.DryOnly, "terrain-drop", "5ac12387eddacdd082746fb93e7739f4ff861fca179d2af1b1db6f39399a7443" },
        { "drawn-bed-level", WaterTraversalMode.SurfaceSwimmer, "no-drop", "9dd35e345cbccb07346d064eca27d1671cd20c8ec9b2c8c75ce8cc47fa53a7fb" },
        { "drawn-bed-level", WaterTraversalMode.SurfaceSwimmer, "drop", "e2e68c454f563d67c93172837c869febabb82dc56f9447de5cf3f074617ea6ca" },
        { "drawn-bed-level", WaterTraversalMode.SurfaceSwimmer, "terrain-drop", "0fc017d299b4cea2854d0ccdd4ab39172d33cbd0b044370af142b4644916d1c1" },
        { "drawn-bed-level", WaterTraversalMode.WadeOnly, "no-drop", "63fc2cd3dcc388433b7b0054f63ad8dd67f9792f07b198c9d962e3de5a1cac5b" },
        { "drawn-bed-level", WaterTraversalMode.WadeOnly, "drop", "38c85c900fa3ea1b3545721f225848a3bcb05056343cdcfa1d135a2a351b51bf" },
        { "drawn-bed-level", WaterTraversalMode.WadeOnly, "terrain-drop", "12c001726c90a0f4e4afe50bdb7a753ba4c83936cb37135b4083951330440d17" },
        { "drawn-bed-level", WaterTraversalMode.DryOnly, "no-drop", "2527804d0e292f33e5b472a65319862df34ae74512ab270f802b2e485f9c8af9" },
        { "drawn-bed-level", WaterTraversalMode.DryOnly, "drop", "9070dd5869a1e1b46cec9a6ad789dc98d3a5bee3f6fd740c9a541f032f089804" },
        { "drawn-bed-level", WaterTraversalMode.DryOnly, "terrain-drop", "c0eceafd8939c7e564e17ede6f56a75bd1007b90f2a4add8ed6c261418efb994" },
        { "terrain-pool", WaterTraversalMode.SurfaceSwimmer, "no-drop", "76877b5fdda526d77f54abd985a2e999e7d35c6cb7c87394ac2b04fd5af12e9d" },
        { "terrain-pool", WaterTraversalMode.SurfaceSwimmer, "drop", "0794fe007262ec25b3c11d43a09841e90c7ca00f6757ad38250f5098d7210bed" },
        { "terrain-pool", WaterTraversalMode.SurfaceSwimmer, "terrain-drop", "33667b4d4cb133cd9d9fcb0aafd6595b8da030102c97ccb7fb8533a9e93d3eb8" },
        { "terrain-pool", WaterTraversalMode.WadeOnly, "no-drop", "5c7bdaa004bcdc61c5da50f6d4874655d2b9aeeb7e26bc4098c46f1c7762cdd4" },
        { "terrain-pool", WaterTraversalMode.WadeOnly, "drop", "cba5047bdba77b09f103e1ec3a9babbba834d3234d80558bba71b20a69fcdb09" },
        { "terrain-pool", WaterTraversalMode.WadeOnly, "terrain-drop", "8821445efef18273a3d1f5654cb37be7a42a6478890865acc14da135caf38cf7" },
        { "terrain-pool", WaterTraversalMode.DryOnly, "no-drop", "3b7786fb3af8c47dcf8dc6eb07e755ad99786561f3b2ce127cecaa2999803e31" },
        { "terrain-pool", WaterTraversalMode.DryOnly, "drop", "00d22397a7365de9461eefcc0c889de62dca83dd60e2829b03ef40d100d714fb" },
        { "terrain-pool", WaterTraversalMode.DryOnly, "terrain-drop", "9d6fa4b523071cb9824016602448bc09a2b64b5b00b32ddfbbf2cae5c060302e" },
    };

    // A bake payload carries the baking machine's float decisions, and a terrain Drop payload adds canonical physical
    // evidence, so the 15 terrain Drop files differ between Arm64 and x64 while the other 30 do not. These are the x64
    // files, baked at the same commit e937df491 as the Arm64 rows above.
    static readonly Dictionary<(string World, WaterTraversalMode Mode), string> X64TerrainDrops = new()
    {
        [("census-bank", WaterTraversalMode.SurfaceSwimmer)] = "d8eb923011e42b58bdb2a9ef48a947147a968cc3a308e448808b943137fda2fb",
        [("census-bank", WaterTraversalMode.WadeOnly)] = "d3c98c1c9dcff17fbeb457e2e44f545a53d08a7c279af2a83270e4df9b1bb485",
        [("census-bank", WaterTraversalMode.DryOnly)] = "bc3b08b1c7f5bd59c30eddf6723b40ff997bfcbc05c60f6e542a3fc7ebf484cf",
        [("shore-1697", WaterTraversalMode.SurfaceSwimmer)] = "bfc118326a15132689266b99856ff1f29ef3c658a04e127431eaaa3d82eb586c",
        [("shore-1697", WaterTraversalMode.WadeOnly)] = "96a9a4176550ae710bb8fb3106c099b7d9daca1ce428a7965acc7474a120fe14",
        [("shore-1697", WaterTraversalMode.DryOnly)] = "e407a5f808a8baa8ee4771e96463bb21ef730e926b5ad38ae1ffdd2bf84e6799",
        [("drawn-bed-sloped", WaterTraversalMode.SurfaceSwimmer)] = "9256aa4df7a9559adef6c4d3be04872296858bd4196ee4fbe1bfc036f5a770c5",
        [("drawn-bed-sloped", WaterTraversalMode.WadeOnly)] = "36e5ccd4bd7caaf6e2fbb48d8dc5d25c9e868fface64f54441687aecbcee1009",
        [("drawn-bed-sloped", WaterTraversalMode.DryOnly)] = "a77665e09c89f60d65658df798311b67d5c07008a88243541b75d673e4760af9",
        [("drawn-bed-level", WaterTraversalMode.SurfaceSwimmer)] = "4d306dd53c59bf79c3af61357e8469d5aade489adda48c07c0f56256feabde8b",
        [("drawn-bed-level", WaterTraversalMode.WadeOnly)] = "75ff6cd7092eb7c9a758d73040cde70b49de57f14a1753ea2215e703eb8dc790",
        [("drawn-bed-level", WaterTraversalMode.DryOnly)] = "f13517a5ab48b8d656f25c37e5cadb6e0ac8f0568a1de560d393b1ae1e22490e",
        [("terrain-pool", WaterTraversalMode.SurfaceSwimmer)] = "e05b47d24ebc82dfdb0df984875b32b3701de5e51daa377478cc33532a4eb1ed",
        [("terrain-pool", WaterTraversalMode.WadeOnly)] = "2bef0e0a28a4f837537b732947dae4f351924e65fe2b47985487fb865632e097",
        [("terrain-pool", WaterTraversalMode.DryOnly)] = "df4e84735bd3d01ee94146f605bb7752c1214f163c68e59a30f6259ceb9f512e",
    };

    static string TerrainDropPin(string world, WaterTraversalMode mode, string arm64) =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => arm64,
            Architecture.X64 => X64TerrainDrops[(world, mode)],
            var other => throw new InvalidOperationException($"No terrain Drop bake is pinned for {other}."),
        };

    [Theory]
    [MemberData(nameof(Baselines))]
    public void SerialBakeMatchesTheBaseCommitFullFile(string world, WaterTraversalMode mode, string drops, string sha256)
    {
        (TileWorldDocument document, PhysicsNavBakeOptions options, MovementBoundary boundary) = Create(world);
        float maxDrop = drops == "no-drop" ? 0 : 6;
        bool terrain = drops == "terrain-drop";
        options = options with { CaptureTerrainDescentEvidence = terrain };
        var profiles = new[] { new NavBakeProfile("probe", Tuning(mode), default)
            { MaxDropMetres = maxDrop, AllowTerrainDescents = terrain } };

        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        var colliders = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), World);
        using var installed = colliders.AddTo(physics);
        using var context = new GroundMoveContext(colliders.MovementGroundHeight, colliders.MovementGroundNormal, physics,
            null, colliders.Medium.MediumDelegate, null, colliders.Medium, installed.CreateMovementRegistration(World), boundary);
        var expectation = new NavBakeExpectation(options, new NavBakeSources().Add("colliders", colliders.Hash), profiles);
        GroundNavigationBake bake;
        using (var capture = PhysicsNavBake.Capture(context, options, _ => 0))
            bake = GroundNavigationBake.Create(capture, expectation.Sources, profiles, null);

        string actualHash = Convert.ToHexStringLower(SHA256.HashData(AtBaseVersion(bake, expectation, "probe")));
        output.WriteLine($"{world} {mode} {drops}: {actualHash}");
        Assert.Equal(terrain ? TerrainDropPin(world, mode, sha256) : sha256, actualHash);
    }

    static byte[] AtBaseVersion(GroundNavigationBake bake, NavBakeExpectation expectation, string profile)
    {
        using var stream = new MemoryStream();
        bake.WriteTo(stream);
        byte[] file = stream.ToArray();
        const int header = GroundNavigationBake.HeaderLength;
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        byte[] current = NavBakeIdentity.Encode(expectation, NavBakeIdentity.CurrentEngineVersion);
        Assert.Equal(current, file.AsSpan(header, length).ToArray());
        byte[] payload = file.AsSpan(header + length).ToArray();
        byte[] identity = NavBakeIdentity.Encode(expectation, BaseEngineVersion);
        if (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4)) == NavBakeIdentity.TerrainFormatVersion)
        {
            // Rebinding to the written identity must reproduce the written payload, which proves the walk below.
            Assert.Equal(payload, RebindTerrainDigest(payload, current, profile));
            payload = RebindTerrainDigest(payload, identity, profile);
        }
        byte[] normalized = [.. file.AsSpan(0, header), .. identity, .. payload];
        BinaryPrimitives.WriteUInt32LittleEndian(normalized.AsSpan(8), (uint)identity.Length);
        SHA256.HashData(payload, normalized.AsSpan(20, 32));
        return normalized;
    }

    // A format 4 payload: the capture, the terrain evidence, then for its one profile the profile bytes, the route
    // facts and the workload digest over the identity, evidence, name, profile and facts.
    static byte[] RebindTerrainDigest(byte[] source, byte[] identity, string profile)
    {
        byte[] payload = [.. source];
        int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at));
        int at = 24 + I32(0) * I32(4) + 12 * I32(20);
        at += 4 + 12 * I32(at);
        ReadOnlySpan<byte> evidence = payload.AsSpan(at + 4, I32(at));
        at += 4 + evidence.Length;
        ReadOnlySpan<byte> data = payload.AsSpan(at + 4, I32(at));
        at += 4 + data.Length;
        ReadOnlySpan<byte> facts = payload.AsSpan(at + 4, I32(at));
        at += 4 + facts.Length;
        Assert.Equal(payload.Length, at + 32);
        TerrainWorkload.Digest(identity, evidence, profile, data, facts).CopyTo(payload.AsSpan(at));
        return payload;
    }

    static MoveTuning Tuning(WaterTraversalMode mode) => new(2, 5, 0.75f, MathF.PI / 4, CapsuleRadius: 0.3f)
    {
        Controller = MovementControllerKind.Contact,
        Gravity = 25,
        StepHeight = 0.4f,
        FootRadiusFraction = 0.5f,
        SwimSpeed = 3,
        SwimEnterDepthFraction = 0.65f,
        SwimExitDepthFraction = 0.55f,
        SwimSurfaceSubmersionFraction = 0.6f,
        WadeMinSpeedScale = 0.6f,
        Water = new(mode),
    };

    static (TileWorldDocument, PhysicsNavBakeOptions, MovementBoundary) Create(string world)
    {
        float slope = MathF.PI / 4;
        return world switch
        {
            "census-bank" => (Bank(), new(107, -241, 114, -233, 0.5f, 10, 14, slope, 512, 2048),
                MovementBoundary.Rectangle(65, -319, 127, -193)),
            "shore-1697" => (ContactDropPrecisionTests.Shore(), new(96, -300, 103, -293, 0.5f, 10, 14, slope, 512, 2048),
                MovementBoundary.Rectangle(64, -320, 128, -256)),
            "drawn-bed-sloped" => (DrawnWaterBedTests.World(false), new(5, -12, 12, -6, 0.5f, 10, 14, slope, 512, 2048),
                MovementBoundary.Rectangle(0, -64, 64, 0)),
            "drawn-bed-level" => (DrawnWaterBedTests.World(true), new(5, -12, 12, -6, 0.5f, 10, 14, slope, 512, 2048),
                MovementBoundary.Rectangle(0, -64, 64, 0)),
            "terrain-pool" => (TerrainPool(), new(0, -3, 5, 0, 0.25f, 10, 14, slope, 256, 1024),
                MovementBoundary.Rectangle(0, -64, 64, 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(world)),
        };
    }

    // The census bank rows on this project's catalog: a -0.35 m lip at row 236, the -1.30 m shelf beyond and a
    // -0.37 m surface.
    static TileWorldDocument Bank()
    {
        var document = TileWorldPhysicsTestData.FlatWorld(RegionCoord.Of(110, 236));
        for (int z = 236; z <= 243; z++)
            for (int x = 106; x <= 116; x++)
            {
                document.SetCornerHeightCm(x, z, 0, (short)(z == 236 ? -35 : -130));
                if (x < 116 && z < 243) document.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
            }
        return document;
    }

    // A 4 m terrain descent onto ground that falls into a shallow pool beside its foot.
    static TileWorldDocument TerrainPool()
    {
        var document = TileWorldPhysicsTestData.FlatWorld();
        for (int z = 0; z <= 3; z++)
            for (int x = 0; x <= 5; x++)
            {
                float y = x <= 1 ? 4 : x >= 3 ? -0.5f : 0;
                document.SetCornerHeightCm(x, z, 0, checked((short)MathF.Round(y * 100)));
            }
        for (int z = 0; z < 3; z++)
            for (int x = 2; x < 5; x++) document.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
        return document;
    }
}
