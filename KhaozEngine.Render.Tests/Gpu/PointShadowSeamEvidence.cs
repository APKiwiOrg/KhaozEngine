using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The least-squares line the seam case takes out of its reach series, kept so the evidence carries the
/// coefficients and sums its residuals were measured against.</summary>
internal readonly record struct LeastSquaresLine(float MeanX, float MeanY, float Sxx, float Sxy, float Slope);

/// <summary>Where the binaries that rendered came from: the two required source identities, whether the assembly
/// metadata agrees with them, and every assembly that took part.</summary>
internal sealed record SeamProvenance(string SourceCommit, string SourceTree, string RevisionAgreement,
    IReadOnlyList<SeamAssembly> Assemblies);

/// <summary>One loaded assembly: its informational version as built, the source revision that version carries
/// (null when it carries none), its module version id and the SHA-256 of its file (null when it has none).</summary>
internal sealed record SeamAssembly(string Name, string? InformationalVersion, string? SourceRevision, Guid Mvid,
    string? FileSha256);

/// <summary>The device the captures ran on, as the device itself reports it.</summary>
internal sealed record SeamDevice(string Backend, string SelectionSource, string? RequestedOverride,
    string? RequestedBackend, string Adapter, bool? SoftwareAdapter, string? DeviceLossReason, bool ClipSpaceYInverted,
    bool DepthRangeZeroToOne, string DeviceType);

/// <summary>One test catalog program: the SHA-256 of its UTF-8 GLSL observed by ShippedShaderPrograms. Const
/// sources are inlined into the test assembly at build, static readonly sources are read from loaded renderer
/// assemblies. This is not a record of this frame's bound programs or device HLSL/DXBC.</summary>
internal sealed record SeamShader(string Program, string VertexGlslSha256, string FragmentGlslSha256);

/// <summary>The camera both captures were framed by. <c>View</c> is relative to <c>RenderOrigin</c>, exactly as
/// the camera reports it.</summary>
internal readonly record struct SeamCamera(Matrix4x4 View, Matrix4x4 Projection, Matrix4x4 ViewProjection, Vector3 Eye,
    Vector3 RenderOrigin);

/// <summary>What the case asked for: the light, its shadow row, the framing, the scan geometry and the budget.</summary>
internal sealed record SeamScene(Vector3 Light, float Radius, float Intensity, int ShadowId, Vector3 FrameCentre,
    float FrameHalfExtent, Vector3 Across, float Window, PointShadowSettings Settings);

/// <summary>Everything the evidence says about the run that is not a probe: provenance, device, shaders, camera,
/// scene, the resolved atlas and the two captures with how many lights each reported as shadowed.</summary>
internal sealed record SeamHeader(SeamProvenance Provenance, SeamDevice Device, IReadOnlyList<SeamShader> Shaders,
    SeamCamera Camera, SeamScene Scene, PointShadowResolution Resolved, byte[] Plain, byte[] Soft,
    int PlainShadowedLights, int SoftShadowedLights);

/// <summary>One file the evidence put on disk.</summary>
internal sealed record WrittenFile(string Name, long Bytes, string Sha256);

/// <summary>
/// THE COMPLETE RECORD OF ONE RUN OF THE CUBE-FACE SEAM PROBE, so a failure on a software leg can be read from what
/// it rendered rather than from the rounded reach list in its message
/// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1024">#1024</see>,
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1157">#1157</see>,
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1190">#1190</see>).
/// <para>
/// IT RECORDS, IT DOES NOT MEASURE. Every probe value arrives from the case after the case computed it, so the
/// sample positions, the truncation onto a texel and the quadrature stay the case's own. The record adds one check
/// of its own: every red byte it was handed must be the byte the capture holds at the texel it names, which is what
/// makes the stored pixel coordinates evidence rather than a second derivation.
/// </para>
/// <para>
/// THE GRID IS FIXED at 13 stations of 61 probes, and the manifest always carries all 793 slots in station then
/// probe order, with null for any slot a failed run never reached. A COMPLETE record refuses to serialize with any
/// slot, station summary, fit or header missing. Floats are JSON numbers in their shortest round-trip form, so
/// parsing one back gives the same bits. A non-finite float, which no finished run produces, is written as its
/// invariant name in a string. Matrices are the sixteen <see cref="Matrix4x4"/> fields in row-major order. The two
/// captures are written beside the manifest as raw RGBA exactly as read back, indexed <c>(y * width + x) * 4</c>.
/// </para>
/// </summary>
internal sealed class PointShadowSeamEvidence
{
    public const string Schema = "khaozengine.point-shadow-seam-evidence";
    public const int SchemaVersion = 1;
    public const int Stations = 13;
    public const int ProbesPerStation = 61;
    public const string ManifestFile = "point-shadow-seam.json";
    public const string PlainFile = "point-shadow-seam.plain.rgba";
    public const string SoftFile = "point-shadow-seam.soft.rgba";

    /// <summary>The ceiling on the manifest. A complete one is a few hundred kilobytes, so reaching this means
    /// the record holds something it was never meant to.</summary>
    public const int MaxManifestBytes = 2 * 1024 * 1024;

    /// <summary>The failure message is the one free-form field, so it is the one that is cut to a length.</summary>
    public const int MaxFailureChars = 8192;

    /// <summary>The stations the case averages its crossing bump over.</summary>
    static readonly int[] BumpStations = [3, 4, 5];

    readonly record struct Probe(Vector3 World, Vector2 Screen, int X, int Y, int PlainRed, int ShadowRed, float Ratio);
    readonly record struct Station(Vector3 On, Vector3 From, Vector3 To, float Sum, float Reach);
    sealed record Fit(LeastSquaresLine Line, float[] Residuals, float Bump, float WorstElsewhere);

    readonly int _width, _height;
    readonly Probe?[] _probes = new Probe?[Stations * ProbesPerStation];
    readonly Station?[] _stations = new Station?[Stations];
    Fit? _fit;
    SeamHeader? _header;

    public PointShadowSeamEvidence(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _width = width;
        _height = height;
    }

    /// <summary>One probe as the case read it: where it stood, where that landed on the picture, the two red bytes
    /// and the ratio the case divided out of them. The texel is the same truncation the case's own read takes.</summary>
    public void RecordProbe(int station, int probe, Vector3 world, Vector2 screen, int plainRed, int shadowRed,
        float ratio)
    {
        int slot = Slot(station, probe);
        int x = (int)screen.X, y = (int)screen.Y;
        if (x < 0 || x >= _width || y < 0 || y >= _height)
            throw new ArgumentOutOfRangeException(nameof(screen),
                $"station {station} probe {probe} lands at {screen}, off a {_width} by {_height} capture");
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)plainRed, 255u, nameof(plainRed));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)shadowRed, 255u, nameof(shadowRed));
        if (_probes[slot] is not null)
            throw new InvalidOperationException($"station {station} probe {probe} was recorded twice");
        _probes[slot] = new Probe(world, screen, x, y, plainRed, shadowRed, ratio);
    }

    /// <summary>One station's scan line, the sum of its shadowed part and the reach the case integrated from it.</summary>
    public void RecordStation(int station, Vector3 on, Vector3 from, Vector3 to, float sum, float reach)
    {
        _ = Slot(station, 0);
        if (_stations[station] is not null)
            throw new InvalidOperationException($"station {station} was recorded twice");
        _stations[station] = new Station(on, from, to, sum, reach);
    }

    /// <summary>The detrend and the statistic the case asserts on.</summary>
    public void RecordFit(LeastSquaresLine line, float[] residuals, float bump, float worstElsewhere)
    {
        ArgumentNullException.ThrowIfNull(residuals);
        if (residuals.Length != Stations)
            throw new ArgumentException($"the fit carries {residuals.Length} residuals, not {Stations}", nameof(residuals));
        if (_fit is not null) throw new InvalidOperationException("the fit was recorded twice");
        _fit = new Fit(line, (float[])residuals.Clone(), bump, worstElsewhere);
    }

    public void SetHeader(SeamHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        int bytes = _width * _height * 4;
        if (header.Plain is null || header.Soft is null || header.Plain.Length != bytes || header.Soft.Length != bytes)
            throw new ArgumentException(
                $"both captures must be {_width} by {_height} RGBA, {bytes} bytes each", nameof(header));
        _header = header;
    }

    /// <summary>
    /// The manifest. <paramref name="failure"/> null means the run finished and the record must be complete.
    /// Anything else is the exception that stopped it, and the record is written as far as it got.
    /// </summary>
    public byte[] Serialize(Exception? failure)
    {
        SeamHeader header = _header ?? throw new InvalidOperationException(
            "the evidence has no header, so it has no captures to name");
        if (failure is null) RequireComplete();
        RequireBytesMatchCaptures(header);

        var buffer = new ArrayBufferWriter<byte>(256 * 1024);
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("schema", Schema);
            json.WriteNumber("schemaVersion", SchemaVersion);
            json.WriteString("status", failure is null ? "complete" : "incomplete");
            json.WriteString("statusMeaning", "evidence completeness only");
            json.WriteString("assertion", "evaluated after this record was written, see the test result in TRX");
            WriteFailure(json, failure);
            json.WriteStartObject("grid");
            json.WriteNumber("stations", Stations);
            json.WriteNumber("probesPerStation", ProbesPerStation);
            json.WriteString("order", "station-major, probe index along the scan from the lit side");
            json.WriteEndObject();
            json.WriteNumber("recordedProbes", Array.FindAll(_probes, p => p is not null).Length);
            WriteProvenance(json, header.Provenance);
            WriteDevice(json, header.Device);
            WriteShaders(json, header.Shaders);
            WriteCamera(json, header.Camera);
            WriteScene(json, header);
            WriteCaptures(json, header);
            WriteStations(json);
            WriteFit(json);
            json.WriteEndObject();
        }
        if (buffer.WrittenCount > MaxManifestBytes)
            throw new InvalidOperationException(
                $"the manifest is {buffer.WrittenCount} bytes, over its {MaxManifestBytes} byte bound");
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The two raw captures and the manifest, into <paramref name="directory"/> under their fixed names. The record
    /// is serialized first, so a refused record writes nothing. Every file is created new and never replaces one
    /// already there, so a stale artifact cannot pass for this run's. The manifest goes last: a directory holding
    /// captures without it is a write that failed part way.
    /// </summary>
    public IReadOnlyList<WrittenFile> Write(string directory, Exception? failure)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        byte[] manifest = Serialize(failure);
        SeamHeader header = _header!;
        Directory.CreateDirectory(directory);
        return
        [
            WriteNew(directory, PlainFile, header.Plain),
            WriteNew(directory, SoftFile, header.Soft),
            WriteNew(directory, ManifestFile, manifest),
        ];
    }

    static WrittenFile WriteNew(string directory, string name, byte[] bytes)
    {
        using (var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write,
                   FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        return new WrittenFile(name, bytes.Length, Sha256(bytes));
    }

    static int Slot(int station, int probe)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)station, (uint)Stations, nameof(station));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)probe, (uint)ProbesPerStation, nameof(probe));
        return station * ProbesPerStation + probe;
    }

    void RequireComplete()
    {
        var missing = new List<string>();
        for (int slot = 0; slot < _probes.Length; slot++)
            if (_probes[slot] is null) missing.Add($"station {slot / ProbesPerStation} probe {slot % ProbesPerStation}");
        for (int s = 0; s < Stations; s++)
            if (_stations[s] is null) missing.Add($"station {s} summary");
        if (_fit is null) missing.Add("fit");
        if (missing.Count == 0) return;
        string shown = string.Join(", ", missing.GetRange(0, Math.Min(missing.Count, 12)));
        throw new InvalidOperationException(
            $"a complete seam record is missing {missing.Count} part(s): {shown}{(missing.Count > 12 ? ", ..." : "")}");
    }

    void RequireBytesMatchCaptures(SeamHeader header)
    {
        for (int slot = 0; slot < _probes.Length; slot++)
        {
            if (_probes[slot] is not Probe p) continue;
            int index = (p.Y * _width + p.X) * 4;
            if (header.Plain[index] != p.PlainRed || header.Soft[index] != p.ShadowRed)
                throw new InvalidOperationException(
                    $"station {slot / ProbesPerStation} probe {slot % ProbesPerStation} recorded plain {p.PlainRed} "
                    + $"and shadow {p.ShadowRed} at texel ({p.X}, {p.Y}), where the captures hold "
                    + $"{header.Plain[index]} and {header.Soft[index]}");
        }
    }

    static void WriteFailure(Utf8JsonWriter json, Exception? failure)
    {
        if (failure is null)
        {
            json.WriteNull("failure");
            return;
        }
        string message = failure.Message;
        if (message.Length > MaxFailureChars) message = message[..MaxFailureChars];
        json.WriteStartObject("failure");
        json.WriteString("type", failure.GetType().FullName);
        json.WriteString("message", message);
        json.WriteEndObject();
    }

    static void WriteProvenance(Utf8JsonWriter json, SeamProvenance provenance)
    {
        json.WriteStartObject("provenance");
        json.WriteString("sourceCommit", provenance.SourceCommit);
        json.WriteString("sourceTree", provenance.SourceTree);
        json.WriteString("revisionAgreement", provenance.RevisionAgreement);
        json.WriteStartArray("assemblies");
        foreach (SeamAssembly assembly in provenance.Assemblies)
        {
            json.WriteStartObject();
            json.WriteString("name", assembly.Name);
            json.WriteString("informationalVersion", assembly.InformationalVersion);
            json.WriteString("sourceRevision", assembly.SourceRevision);
            json.WriteString("mvid", assembly.Mvid);
            json.WriteString("fileSha256", assembly.FileSha256);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    static void WriteDevice(Utf8JsonWriter json, SeamDevice device)
    {
        json.WriteStartObject("device");
        json.WriteString("backend", device.Backend);
        json.WriteString("selectionSource", device.SelectionSource);
        json.WriteString("requestedOverride", device.RequestedOverride);
        json.WriteString("requestedBackend", device.RequestedBackend);
        json.WriteString("adapter", device.Adapter);
        if (device.SoftwareAdapter is bool software) json.WriteBoolean("softwareAdapter", software);
        else json.WriteNull("softwareAdapter");
        json.WriteString("deviceLossReason", device.DeviceLossReason);
        json.WriteBoolean("clipSpaceYInverted", device.ClipSpaceYInverted);
        json.WriteBoolean("depthRangeZeroToOne", device.DepthRangeZeroToOne);
        json.WriteString("deviceType", device.DeviceType);
        json.WriteEndObject();
    }

    static void WriteShaders(Utf8JsonWriter json, IReadOnlyList<SeamShader> shaders)
    {
        json.WriteStartObject("shaders");
        json.WriteString("glsl", "SHA-256 of the UTF-8 GLSL observed by the ShippedShaderPrograms test catalog. "
            + "Const sources are inlined into the test assembly at build, static readonly sources are read from "
            + "loaded renderer assemblies. This is not a list of this frame's bound programs or device HLSL/DXBC");
        json.WriteString("deviceBytecode", "not recorded");
        json.WriteStartArray("programs");
        foreach (SeamShader shader in shaders)
        {
            json.WriteStartObject();
            json.WriteString("program", shader.Program);
            json.WriteString("vertexGlslSha256", shader.VertexGlslSha256);
            json.WriteString("fragmentGlslSha256", shader.FragmentGlslSha256);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    static void WriteCamera(Utf8JsonWriter json, SeamCamera camera)
    {
        json.WriteStartObject("camera");
        json.WriteString("matrixOrder", "System.Numerics row-major M11 to M44");
        json.WriteString("viewCoordinates",
            "view coordinates are relative to renderOrigin, subtract it from world coordinates before view");
        Matrix(json, "view", camera.View);
        Matrix(json, "projection", camera.Projection);
        Matrix(json, "viewProjection", camera.ViewProjection);
        Vector(json, "eye", camera.Eye);
        Vector(json, "renderOrigin", camera.RenderOrigin);
        json.WriteEndObject();
    }

    static void WriteScene(Utf8JsonWriter json, SeamHeader header)
    {
        SeamScene scene = header.Scene;
        PointShadowSettings settings = scene.Settings;
        PointShadowResolution resolved = header.Resolved;
        json.WriteStartObject("scene");
        Vector(json, "light", scene.Light);
        Number(json, "radius", scene.Radius);
        Number(json, "intensity", scene.Intensity);
        json.WriteNumber("shadowId", scene.ShadowId);
        Vector(json, "frameCentre", scene.FrameCentre);
        Number(json, "frameHalfExtent", scene.FrameHalfExtent);
        Vector(json, "across", scene.Across);
        Number(json, "window", scene.Window);
        json.WriteNumber("plainShadowedLights", header.PlainShadowedLights);
        json.WriteNumber("softShadowedLights", header.SoftShadowedLights);
        json.WriteStartObject("settings");
        json.WriteBoolean("enabled", settings.Enabled);
        json.WriteNumber("faceResolution", settings.FaceResolution);
        json.WriteNumber("maxShadowedLights", settings.MaxShadowedLights);
        json.WriteNumber("maxStaticRebuildsPerFrame", settings.MaxStaticRebuildsPerFrame);
        json.WriteNumber("maxDynamicLightsPerFrame", settings.MaxDynamicLightsPerFrame);
        Number(json, "bias", settings.Bias);
        Number(json, "slopeBias", settings.SlopeBias);
        json.WriteString("filter", settings.Filter.ToString());
        Number(json, "lightSizeMetres", settings.LightSizeMetres);
        Number(json, "maxPenumbraTexels", settings.MaxPenumbraTexels);
        json.WriteEndObject();
        json.WriteStartObject("resolved");
        json.WriteBoolean("enabled", resolved.Enabled);
        json.WriteNumber("faceResolution", resolved.FaceResolution);
        json.WriteNumber("maxShadowedLights", resolved.MaxShadowedLights);
        json.WriteBoolean("degraded", resolved.Degraded);
        json.WriteString("reason", resolved.Reason);
        json.WriteNumber("baseAtlasBytes", resolved.BaseAtlasBytes);
        json.WriteNumber("transientAtlasRows", resolved.TransientAtlasRows);
        json.WriteNumber("transientAtlasBytes", resolved.TransientAtlasBytes);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    void WriteCaptures(Utf8JsonWriter json, SeamHeader header)
    {
        json.WriteStartArray("captures");
        Capture(PlainFile, "plain", header.Plain);
        Capture(SoftFile, "soft", header.Soft);
        json.WriteEndArray();

        void Capture(string file, string name, byte[] rgba)
        {
            json.WriteStartObject();
            json.WriteString("name", name);
            json.WriteString("file", file);
            json.WriteNumber("width", _width);
            json.WriteNumber("height", _height);
            json.WriteString("format", "R8G8B8A8UNorm, as returned by GpuReadback.ToRgba, index (y * width + x) * 4");
            json.WriteNumber("bytes", rgba.Length);
            json.WriteString("sha256", Sha256(rgba));
            json.WriteEndObject();
        }
    }

    void WriteStations(Utf8JsonWriter json)
    {
        json.WriteStartArray("stations");
        for (int s = 0; s < Stations; s++)
        {
            json.WriteStartObject();
            json.WriteNumber("index", s);
            if (_stations[s] is Station station)
            {
                Vector(json, "on", station.On);
                Vector(json, "from", station.From);
                Vector(json, "to", station.To);
                Number(json, "sum", station.Sum);
                Number(json, "reach", station.Reach);
            }
            else
            {
                foreach (string name in new[] { "on", "from", "to", "sum", "reach" }) json.WriteNull(name);
            }
            json.WriteStartArray("probes");
            for (int i = 0; i < ProbesPerStation; i++)
            {
                if (_probes[s * ProbesPerStation + i] is not Probe p)
                {
                    json.WriteNullValue();
                    continue;
                }
                json.WriteStartObject();
                json.WriteNumber("index", i);
                Number(json, "t", i / (float)(ProbesPerStation - 1));
                Vector(json, "world", p.World);
                json.WriteStartArray("screen");
                Number(json, p.Screen.X);
                Number(json, p.Screen.Y);
                json.WriteEndArray();
                json.WriteStartArray("pixel");
                json.WriteNumberValue(p.X);
                json.WriteNumberValue(p.Y);
                json.WriteEndArray();
                json.WriteNumber("byteIndex", (p.Y * _width + p.X) * 4);
                json.WriteNumber("plainRed", p.PlainRed);
                json.WriteNumber("shadowRed", p.ShadowRed);
                Number(json, "ratio", p.Ratio);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    void WriteFit(Utf8JsonWriter json)
    {
        if (_fit is not Fit fit)
        {
            json.WriteNull("fit");
            json.WriteNull("statistic");
            return;
        }
        json.WriteStartObject("fit");
        Number(json, "meanX", fit.Line.MeanX);
        Number(json, "meanY", fit.Line.MeanY);
        Number(json, "sxx", fit.Line.Sxx);
        Number(json, "sxy", fit.Line.Sxy);
        Number(json, "slope", fit.Line.Slope);
        json.WriteStartArray("residuals");
        foreach (float r in fit.Residuals) Number(json, r);
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteStartObject("statistic");
        Number(json, "bump", fit.Bump);
        Number(json, "worstElsewhere", fit.WorstElsewhere);
        json.WriteStartArray("bumpStations");
        foreach (int s in BumpStations) json.WriteNumberValue(s);
        json.WriteEndArray();
        json.WriteEndObject();
    }

    static void Matrix(Utf8JsonWriter json, string name, Matrix4x4 m)
    {
        json.WriteStartArray(name);
        foreach (float v in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                     m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 })
            Number(json, v);
        json.WriteEndArray();
    }

    static void Vector(Utf8JsonWriter json, string name, Vector3 v)
    {
        json.WriteStartArray(name);
        Number(json, v.X);
        Number(json, v.Y);
        Number(json, v.Z);
        json.WriteEndArray();
    }

    static void Number(Utf8JsonWriter json, string name, float value)
    {
        json.WritePropertyName(name);
        Number(json, value);
    }

    static void Number(Utf8JsonWriter json, float value)
    {
        if (float.IsFinite(value)) json.WriteNumberValue(value);
        else json.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
