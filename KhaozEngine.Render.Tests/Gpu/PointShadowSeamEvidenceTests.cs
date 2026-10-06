using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The seam probe's evidence record, proved on SYNTHETIC inputs with no device. The GPU case that fills it runs
/// only on a GPU leg, and its whole value is that what it writes can be read back exactly, so the shape, the
/// indexing, the number round trip and every refusal are held here where they run on every leg.
/// </summary>
public sealed class PointShadowSeamEvidenceTests
{
    const int W = PointShadowScene.Width, H = PointShadowScene.Height;
    const string Commit = "0123456789abcdef0123456789abcdef01234567";
    const string Tree = "89abcdef0123456789abcdef0123456789abcdef";

    static readonly float[] Awkward =
    [
        0f, -0f, float.Epsilon, 1f / 3f, 0.008f, float.MaxValue, -float.MaxValue, MathF.BitIncrement(1f),
        1e-38f, 0.1f + 0.2f, -0.0087f, 0.0085f,
    ];

    // Probe (s, i) reads pixel (i, 3s), so all 793 land on distinct pixels and the byte check can see each one.
    static Vector2 Screen(int s, int i) => new(i + 0.75f, 3 * s + 0.25f);
    static int PlainRed(int s, int i) => 25 + 10 * s + (i % 7);
    static int ShadowRed(int s, int i) => (4 * i + s) % 256;

    static (PointShadowSeamEvidence Evidence, byte[] Plain, byte[] Soft) Filled(Func<int, int, bool>? skip = null)
    {
        var plain = new byte[W * H * 4];
        var soft = new byte[W * H * 4];
        var evidence = new PointShadowSeamEvidence(W, H);
        for (int s = 0; s < PointShadowSeamEvidence.Stations; s++)
        {
            for (int i = 0; i < PointShadowSeamEvidence.ProbesPerStation; i++)
            {
                if (skip?.Invoke(s, i) == true) continue;
                int index = ((3 * s) * W + i) * 4;
                plain[index] = (byte)PlainRed(s, i);
                soft[index] = (byte)ShadowRed(s, i);
                evidence.RecordProbe(s, i, new Vector3(s, 0f, i), Screen(s, i), PlainRed(s, i), ShadowRed(s, i),
                    ShadowRed(s, i) / (float)PlainRed(s, i));
            }
            evidence.RecordStation(s, new Vector3(s, 0f, 1.5f * s), new Vector3(s - 1f, 0f, s), new Vector3(s + 1f, 0f, s),
                0.25f * s, 0.9f + 0.001f * s);
        }
        var residual = new float[PointShadowSeamEvidence.Stations];
        for (int s = 0; s < residual.Length; s++) residual[s] = 0.001f * (s - 6);
        evidence.RecordFit(new LeastSquaresLine(6f, 0.906f, 182f, -0.5f, -0.5f / 182f), residual, -0.0087f, 0.0085f);
        evidence.SetHeader(Header(plain, soft));
        return (evidence, plain, soft);
    }

    static SeamHeader Header(byte[] plain, byte[] soft) => new(
        new SeamProvenance(Commit, Tree, "absent",
            [new SeamAssembly("Synthetic.Assembly", "1.0.0", null, Guid.Empty, null)]),
        new SeamDevice("Direct3D11Native", "EnvironmentOverride", "direct3d11-native", null,
            "Synthetic Render Driver", true, null, false, true, "Synthetic.Device"),
        [new SeamShader("Model", new string('a', 64), new string('b', 64))],
        new SeamCamera(Matrix4x4.Identity, Matrix4x4.CreateScale(2f), Matrix4x4.CreateTranslation(1f, 2f, 3f),
            new Vector3(1f, 2f, 3f), Vector3.Zero),
        new SeamScene(new Vector3(0f, 5f, 0f), 30f, 1f, 307, new Vector3(3.6f, 0f, 5.4f), 1.8f,
            Vector3.Normalize(new Vector3(1.5f, 0f, -1f)), 0.9f,
            new PointShadowSettings { Filter = PointShadowFilter.Soft, LightSizeMetres = 0.5f, MaxPenumbraTexels = 16f }),
        default, plain, soft, 0, 1);

    static JsonElement Parse(byte[] manifest) => JsonDocument.Parse(manifest).RootElement;

    static Func<string, string?> Env(params (string Key, string? Value)[] pairs)
    {
        var table = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach ((string key, string? value) in pairs) table[key] = value;
        return name => table.TryGetValue(name, out string? value) ? value : null;
    }

    static string AbsoluteScratch() => Path.Combine(Path.GetTempPath(), "ke-seam-evidence-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EveryOneOfTheThirteenBySixtyOneProbesLandsAtItsOwnStationAndIndex()
    {
        JsonElement root = Parse(Filled().Evidence.Serialize(failure: null));

        Assert.Equal(PointShadowSeamEvidence.Schema, root.GetProperty("schema").GetString());
        Assert.Equal(PointShadowSeamEvidence.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("complete", root.GetProperty("status").GetString());
        Assert.Equal(13, root.GetProperty("grid").GetProperty("stations").GetInt32());
        Assert.Equal(61, root.GetProperty("grid").GetProperty("probesPerStation").GetInt32());

        JsonElement stations = root.GetProperty("stations");
        Assert.Equal(13, stations.GetArrayLength());
        for (int s = 0; s < 13; s++)
        {
            JsonElement station = stations[s];
            Assert.Equal(s, station.GetProperty("index").GetInt32());
            JsonElement probes = station.GetProperty("probes");
            Assert.Equal(61, probes.GetArrayLength());
            for (int i = 0; i < 61; i++)
            {
                JsonElement probe = probes[i];
                Assert.Equal(i, probe.GetProperty("index").GetInt32());
                Assert.Equal(i, probe.GetProperty("pixel")[0].GetInt32());
                Assert.Equal(3 * s, probe.GetProperty("pixel")[1].GetInt32());
                Assert.Equal(((3 * s) * W + i) * 4, probe.GetProperty("byteIndex").GetInt32());
                Assert.Equal(PlainRed(s, i), probe.GetProperty("plainRed").GetInt32());
                Assert.Equal(ShadowRed(s, i), probe.GetProperty("shadowRed").GetInt32());
                Assert.Equal((float)s, probe.GetProperty("world")[0].GetSingle());
                Assert.Equal((float)i, probe.GetProperty("world")[2].GetSingle());
                Assert.Equal(i / 60f, probe.GetProperty("t").GetSingle());
            }
        }
        Assert.Equal(13, root.GetProperty("fit").GetProperty("residuals").GetArrayLength());
        Assert.Equal(new[] { 3, 4, 5 }, Ints(root.GetProperty("statistic").GetProperty("bumpStations")));
    }

    [Fact]
    public void FloatsRoundTripBitForBitAndBytesKeepTheirExtremes()
    {
        var plain = new byte[W * H * 4];
        var soft = new byte[W * H * 4];
        var evidence = new PointShadowSeamEvidence(W, H);
        for (int i = 0; i < Awkward.Length; i++)
        {
            int index = i * 4;
            plain[index] = 255;
            evidence.RecordProbe(0, i, new Vector3(Awkward[i], -Awkward[i], Awkward[i]), new Vector2(i + 0.5f, 0.5f),
                255, 0, Awkward[i]);
        }
        evidence.RecordStation(0, Vector3.Zero, Vector3.Zero, Vector3.Zero, 1f / 3f, 0.008f);
        evidence.SetHeader(Header(plain, soft));

        JsonElement root = Parse(evidence.Serialize(new InvalidOperationException("synthetic")));
        JsonElement probes = root.GetProperty("stations")[0].GetProperty("probes");
        for (int i = 0; i < Awkward.Length; i++)
        {
            JsonElement probe = probes[i];
            AssertBits(Awkward[i], probe.GetProperty("ratio").GetSingle());
            AssertBits(Awkward[i], probe.GetProperty("world")[0].GetSingle());
            AssertBits(-Awkward[i], probe.GetProperty("world")[1].GetSingle());
            Assert.Equal(255, probe.GetProperty("plainRed").GetInt32());
            Assert.Equal(0, probe.GetProperty("shadowRed").GetInt32());
        }
        AssertBits(1f / 3f, root.GetProperty("stations")[0].GetProperty("sum").GetSingle());
        AssertBits(0.008f, root.GetProperty("stations")[0].GetProperty("reach").GetSingle());
    }

    [Fact]
    public void MetadataLabelsCameraMatricesAndSettingsRoundTripExactly()
    {
        JsonElement root = Parse(Filled().Evidence.Serialize(failure: null));
        Assert.Equal("evidence completeness only", root.GetProperty("statusMeaning").GetString());
        Assert.Equal("evaluated after this record was written, see the test result in TRX",
            root.GetProperty("assertion").GetString());
        JsonElement shaders = root.GetProperty("shaders");
        Assert.Equal("SHA-256 of the UTF-8 GLSL observed by the ShippedShaderPrograms test catalog. "
            + "Const sources are inlined into the test assembly at build, static readonly sources are read from "
            + "loaded renderer assemblies. This is not a list of this frame's bound programs or device HLSL/DXBC",
            shaders.GetProperty("glsl").GetString());
        Assert.Equal("not recorded", shaders.GetProperty("deviceBytecode").GetString());
        Assert.False(shaders.TryGetProperty("hlsl", out _));
        JsonElement shader = shaders.GetProperty("programs")[0];
        Assert.Equal("Model", shader.GetProperty("program").GetString());
        Assert.Equal(new string('a', 64), shader.GetProperty("vertexGlslSha256").GetString());
        Assert.Equal(new string('b', 64), shader.GetProperty("fragmentGlslSha256").GetString());
        Assert.False(shader.TryGetProperty("vertexHlslSha256", out _));
        Assert.False(shader.TryGetProperty("fragmentHlslSha256", out _));
        JsonElement camera = root.GetProperty("camera");
        Assert.Equal("view coordinates are relative to renderOrigin, subtract it from world coordinates before view",
            camera.GetProperty("viewCoordinates").GetString());
        JsonElement projection = camera.GetProperty("projection");
        Assert.Equal(16, projection.GetArrayLength());
        AssertBits(2f, projection[0].GetSingle());
        AssertBits(1f, projection[15].GetSingle());
        JsonElement viewProjection = root.GetProperty("camera").GetProperty("viewProjection");
        AssertBits(3f, viewProjection[14].GetSingle());   // M43, the z translation, in row-major order
        JsonElement settings = root.GetProperty("scene").GetProperty("settings");
        Assert.Equal("Soft", settings.GetProperty("filter").GetString());
        AssertBits(0.5f, settings.GetProperty("lightSizeMetres").GetSingle());
        AssertBits(16f, settings.GetProperty("maxPenumbraTexels").GetSingle());
        Assert.Equal(307, root.GetProperty("scene").GetProperty("shadowId").GetInt32());
    }

    [Fact]
    public void ACompleteRecordWithAMissingProbeIsRefusedByName()
    {
        (PointShadowSeamEvidence evidence, _, _) = Filled(skip: (s, i) => s == 7 && i == 30);
        var refused = Assert.Throws<InvalidOperationException>(() => evidence.Serialize(failure: null));
        Assert.Contains("station 7 probe 30", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompleteRecordWithoutItsFitIsRefused()
    {
        var plain = new byte[W * H * 4];
        var evidence = new PointShadowSeamEvidence(W, H);
        for (int s = 0; s < 13; s++)
        {
            for (int i = 0; i < 61; i++) evidence.RecordProbe(s, i, Vector3.Zero, new Vector2(0.5f, 0.5f), 0, 0, 0f);
            evidence.RecordStation(s, Vector3.Zero, Vector3.Zero, Vector3.Zero, 0f, 0f);
        }
        evidence.SetHeader(Header(plain, plain));
        var refused = Assert.Throws<InvalidOperationException>(() => evidence.Serialize(failure: null));
        Assert.Contains("fit", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncompleteRecordKeepsEverySlotAndCarriesTheFailure()
    {
        (PointShadowSeamEvidence evidence, _, _) = Filled(skip: (s, _) => s > 0);
        JsonElement root = Parse(evidence.Serialize(new InvalidOperationException("synthetic precondition")));

        Assert.Equal("incomplete", root.GetProperty("status").GetString());
        Assert.Equal(typeof(InvalidOperationException).FullName, root.GetProperty("failure").GetProperty("type").GetString());
        Assert.Equal("synthetic precondition", root.GetProperty("failure").GetProperty("message").GetString());
        JsonElement stations = root.GetProperty("stations");
        Assert.Equal(13, stations.GetArrayLength());
        Assert.Equal(JsonValueKind.Object, stations[0].GetProperty("probes")[60].ValueKind);
        Assert.Equal(61, stations[1].GetProperty("probes").GetArrayLength());
        foreach (JsonElement probe in stations[1].GetProperty("probes").EnumerateArray())
            Assert.Equal(JsonValueKind.Null, probe.ValueKind);
        Assert.Equal(61, root.GetProperty("recordedProbes").GetInt32());
    }

    [Fact]
    public void ACompleteRecordCarriesNoFailure()
    {
        JsonElement root = Parse(Filled().Evidence.Serialize(failure: null));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("failure").ValueKind);
        Assert.Equal(793, root.GetProperty("recordedProbes").GetInt32());
    }

    [Fact]
    public void ARecordedByteTheCaptureDoesNotHoldIsRefused()
    {
        var plain = new byte[W * H * 4];
        var evidence = new PointShadowSeamEvidence(W, H);
        evidence.RecordProbe(0, 0, Vector3.Zero, new Vector2(0.5f, 0.5f), 30, 0, 0f);
        evidence.SetHeader(Header(plain, plain));
        var refused = Assert.Throws<InvalidOperationException>(() => evidence.Serialize(new InvalidOperationException("x")));
        Assert.Contains("station 0 probe 0", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProbeOutsideTheGridOrTheCaptureOrRecordedTwiceIsRefused()
    {
        var evidence = new PointShadowSeamEvidence(W, H);
        var inside = new Vector2(0.5f, 0.5f);
        Assert.Throws<ArgumentOutOfRangeException>(() => evidence.RecordProbe(13, 0, Vector3.Zero, inside, 0, 0, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => evidence.RecordProbe(0, 61, Vector3.Zero, inside, 0, 0, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => evidence.RecordProbe(-1, 0, Vector3.Zero, inside, 0, 0, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => evidence.RecordProbe(0, 0, Vector3.Zero, new Vector2(W + 0.5f, 0.5f), 0, 0, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => evidence.RecordProbe(0, 0, Vector3.Zero, new Vector2(0.5f, -0.5f - H), 0, 0, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => evidence.RecordProbe(0, 0, Vector3.Zero, inside, 256, 0, 0f));
        evidence.RecordProbe(0, 0, Vector3.Zero, inside, 0, 0, 0f);
        Assert.Throws<InvalidOperationException>(() => evidence.RecordProbe(0, 0, Vector3.Zero, inside, 0, 0, 0f));
        Assert.Throws<ArgumentException>(() => evidence.RecordFit(default, new float[12], 0f, 0f));
        Assert.Throws<ArgumentException>(() => evidence.SetHeader(Header(new byte[16], new byte[W * H * 4])));
    }

    [Fact]
    public void WritingKeepsTheRawCapturesByteForByteAndNeverOverwrites()
    {
        string directory = AbsoluteScratch();
        try
        {
            (PointShadowSeamEvidence evidence, byte[] plain, byte[] soft) = Filled();
            IReadOnlyList<WrittenFile> written = evidence.Write(directory, failure: null);

            Assert.Equal(new[] { PointShadowSeamEvidence.PlainFile, PointShadowSeamEvidence.SoftFile,
                PointShadowSeamEvidence.ManifestFile }, Names(written));
            Assert.Equal(plain, File.ReadAllBytes(Path.Combine(directory, PointShadowSeamEvidence.PlainFile)));
            Assert.Equal(soft, File.ReadAllBytes(Path.Combine(directory, PointShadowSeamEvidence.SoftFile)));

            JsonElement root = Parse(File.ReadAllBytes(Path.Combine(directory, PointShadowSeamEvidence.ManifestFile)));
            JsonElement captures = root.GetProperty("captures");
            Assert.Equal(2, captures.GetArrayLength());
            Assert.Equal(PointShadowSeamEvidence.PlainFile, captures[0].GetProperty("file").GetString());
            Assert.Equal(Sha256(plain), captures[0].GetProperty("sha256").GetString());
            Assert.Equal(Sha256(soft), captures[1].GetProperty("sha256").GetString());
            Assert.Equal(W * H * 4, captures[1].GetProperty("bytes").GetInt32());
            Assert.Equal(W, captures[1].GetProperty("width").GetInt32());
            Assert.Equal(H, captures[1].GetProperty("height").GetInt32());
            Assert.Equal(Sha256(plain), written[0].Sha256);

            Assert.Throws<IOException>(() => evidence.Write(directory, failure: null));
            Assert.Equal(plain, File.ReadAllBytes(Path.Combine(directory, PointShadowSeamEvidence.PlainFile)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ARefusedRecordWritesNothing()
    {
        string directory = AbsoluteScratch();
        try
        {
            (PointShadowSeamEvidence evidence, _, _) = Filled(skip: (s, i) => s == 0 && i == 0);
            Assert.Throws<InvalidOperationException>(() => evidence.Write(directory, failure: null));
            Assert.False(Directory.Exists(directory) && Directory.GetFiles(directory).Length > 0);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheManifestStaysInsideItsSizeBound()
    {
        byte[] manifest = Filled().Evidence.Serialize(failure: null);
        Assert.InRange(manifest.Length, 1, PointShadowSeamEvidence.MaxManifestBytes);
        var huge = new InvalidOperationException(new string('x', 100_000));
        (PointShadowSeamEvidence partial, _, _) = Filled(skip: (s, _) => s > 0);
        JsonElement root = Parse(partial.Serialize(huge));
        Assert.True(root.GetProperty("failure").GetProperty("message").GetString()!.Length <= 8192);
    }

    // ---- configuration and provenance ------------------------------------------------------------------

    [Fact]
    public void AnUnsetOrEmptyDirectoryLeavesTheProbeUnconfigured()
    {
        Assert.Null(PointShadowSeamEvidenceOptions.Read(Env()));
        Assert.Null(PointShadowSeamEvidenceOptions.Read(Env((PointShadowSeamEvidenceOptions.DirectoryVariable, ""))));
        Assert.Null(PointShadowSeamEvidenceOptions.Read(Env(
            (PointShadowSeamEvidenceOptions.CommitVariable, Commit), (PointShadowSeamEvidenceOptions.TreeVariable, Tree))));
    }

    [Fact]
    public void AConfiguredDirectoryWithoutItsSourceIdentityIsRefusedNamingBoth()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => PointShadowSeamEvidenceOptions.Read(Env(
            (PointShadowSeamEvidenceOptions.DirectoryVariable, AbsoluteScratch()))));
        Assert.Contains(PointShadowSeamEvidenceOptions.CommitVariable, refused.Message, StringComparison.Ordinal);
        Assert.Contains(PointShadowSeamEvidenceOptions.TreeVariable, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("   ")]
    [InlineData("dotted")]
    public void ARelativeBlankOrDottedDirectoryIsRefused(string shape)
    {
        string directory = shape == "dotted" ? Path.Combine(Path.GetTempPath(), "..", "evidence") : shape;
        var refused = Assert.Throws<InvalidOperationException>(() => PointShadowSeamEvidenceOptions.Read(Env(
            (PointShadowSeamEvidenceOptions.DirectoryVariable, directory),
            (PointShadowSeamEvidenceOptions.CommitVariable, Commit), (PointShadowSeamEvidenceOptions.TreeVariable, Tree))));
        Assert.Contains(PointShadowSeamEvidenceOptions.DirectoryVariable, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0123456789ABCDEF0123456789abcdef01234567")]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]
    [InlineData("0123456789abcdef0123456789abcdef012345678")]
    [InlineData("0123456789abcdef0123456789abcdef0123456g")]
    [InlineData("HEAD")]
    public void ASourceIdentityMustBeAFullLowerCaseObjectId(string bad)
    {
        Assert.Throws<InvalidOperationException>(() => PointShadowSeamEvidenceOptions.Read(Env(
            (PointShadowSeamEvidenceOptions.DirectoryVariable, AbsoluteScratch()),
            (PointShadowSeamEvidenceOptions.CommitVariable, bad), (PointShadowSeamEvidenceOptions.TreeVariable, Tree))));
        Assert.Throws<InvalidOperationException>(() => PointShadowSeamEvidenceOptions.Read(Env(
            (PointShadowSeamEvidenceOptions.DirectoryVariable, AbsoluteScratch()),
            (PointShadowSeamEvidenceOptions.CommitVariable, Commit), (PointShadowSeamEvidenceOptions.TreeVariable, bad))));
    }

    [Fact]
    public void AValidConfigurationKeepsItsValuesVerbatim()
    {
        string directory = AbsoluteScratch();
        var options = Assert.IsType<PointShadowSeamEvidenceOptions>(PointShadowSeamEvidenceOptions.Read(Env(
            (PointShadowSeamEvidenceOptions.DirectoryVariable, directory),
            (PointShadowSeamEvidenceOptions.CommitVariable, Commit), (PointShadowSeamEvidenceOptions.TreeVariable, Tree))));
        Assert.Equal(Path.GetFullPath(directory), options.OutputDirectory);
        Assert.Equal(Commit, options.SourceCommit);
        Assert.Equal(Tree, options.SourceTree);
        Assert.False(Directory.Exists(directory));   // reading the configuration touches nothing on disk
    }

    [Theory]
    [InlineData("20.1.0+abcdef1234567", "abcdef1234567")]
    [InlineData("20.1.0+ABCDEF1234567", "abcdef1234567")]
    [InlineData("20.1.0+0123456789abcdef0123456789abcdef01234567", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("20.1.0", null)]
    [InlineData("20.1.0+local", null)]
    [InlineData("20.1.0+abc", null)]
    [InlineData(null, null)]
    public void ASourceRevisionIsReadOnlyFromHexBuildMetadata(string? informational, string? revision)
        => Assert.Equal(revision, PointShadowSeamEvidenceOptions.SourceRevision(informational));

    [Fact]
    public void AnAssemblyBuiltFromAnotherCommitIsRefusedAndAgreementIsStated()
    {
        PointShadowSeamEvidenceOptions options = PointShadowSeamEvidenceOptions.Read(Env(
            (PointShadowSeamEvidenceOptions.DirectoryVariable, AbsoluteScratch()),
            (PointShadowSeamEvidenceOptions.CommitVariable, Commit), (PointShadowSeamEvidenceOptions.TreeVariable, Tree)))!;

        var other = new SeamAssembly("KhaozEngine.Render3D", "1.0.0+fedcba9", "fedcba9", Guid.Empty, null);
        var refused = Assert.Throws<InvalidOperationException>(() => options.Provenance([other]));
        Assert.Contains("fedcba9", refused.Message, StringComparison.Ordinal);

        var agreeing = new SeamAssembly("KhaozEngine.Render3D", "1.0.0+0123456", "0123456", Guid.Empty, null);
        var bare = new SeamAssembly("KhaozEngine.Render.Tests", "1.0.0", null, Guid.Empty, null);
        Assert.Equal("agree", options.Provenance([agreeing, bare]).RevisionAgreement);
        Assert.Equal("absent", options.Provenance([bare]).RevisionAgreement);
        Assert.Equal(Tree, options.Provenance([bare]).SourceTree);
    }

    static void AssertBits(float want, float got)
        => Assert.Equal(BitConverter.SingleToInt32Bits(want), BitConverter.SingleToInt32Bits(got));

    static int[] Ints(JsonElement array)
    {
        var values = new int[array.GetArrayLength()];
        for (int i = 0; i < values.Length; i++) values[i] = array[i].GetInt32();
        return values;
    }

    static string[] Names(IReadOnlyList<WrittenFile> files)
    {
        var names = new string[files.Count];
        for (int i = 0; i < names.Length; i++) names[i] = files[i].Name;
        return names;
    }

    static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
