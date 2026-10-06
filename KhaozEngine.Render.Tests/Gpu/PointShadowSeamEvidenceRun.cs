using System;
using System.Collections.Generic;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// One configured run of the seam probe: the record it fills, and the one step that gathers what the device and the
/// loaded binaries say about themselves and writes it all out.
/// <para>
/// <see cref="Emit"/> NEVER THROWS. It returns what went wrong instead, so the case can still evaluate its own
/// assertion and report both outcomes. A run whose evidence failed is never allowed to look like a passing one: the
/// case turns a returned failure into its own.
/// </para>
/// </summary>
internal sealed class PointShadowSeamEvidenceRun
{
    readonly PointShadowSeamEvidenceOptions _options;
    readonly PointShadowScene _fixture;
    readonly PointShadowScene.Shot _plain, _soft;
    readonly SeamScene _scene;
    readonly ITestOutputHelper _output;

    public PointShadowSeamEvidenceRun(PointShadowSeamEvidenceOptions options, PointShadowScene fixture,
        PointShadowScene.Shot plain, PointShadowScene.Shot soft, SeamScene scene, ITestOutputHelper output)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
        _plain = plain ?? throw new ArgumentNullException(nameof(plain));
        _soft = soft ?? throw new ArgumentNullException(nameof(soft));
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public PointShadowSeamEvidence Record { get; } = new(PointShadowScene.Width, PointShadowScene.Height);

    /// <summary>Whether <see cref="Emit"/> has run, so a failure after the evidence was written does not write it
    /// a second time.</summary>
    public bool Emitted { get; private set; }

    /// <summary>The probe sink for one station, which records each probe as the case read it. The screen position
    /// is the fixture's own projection of the same world point, and the record checks it against the bytes.</summary>
    public Action<int, Vector3, int, int, float> Probes(int station) =>
        (probe, world, lit, shade, ratio) =>
            Record.RecordProbe(station, probe, world, _fixture.Pixel(world), lit, shade, ratio);

    /// <summary>Gather, serialize and write. <paramref name="failure"/> is the exception that stopped the case, or
    /// null when it reached its assertion. Returns null on success and the problem otherwise.</summary>
    public Exception? Emit(Exception? failure)
    {
        Emitted = true;
        try
        {
            Record.SetHeader(Gather());
            IReadOnlyList<WrittenFile> written = Record.Write(_options.OutputDirectory, failure);
            var line = new StringBuilder($"seam evidence: {(failure is null ? "complete" : "incomplete")} record in ")
                .Append(_options.OutputDirectory);
            foreach (WrittenFile file in written)
                line.Append($", {file.Name} {file.Bytes} bytes sha256 {file.Sha256}");
            _output.WriteLine(line.ToString());
            return null;
        }
        catch (Exception problem)
        {
            _output.WriteLine($"seam evidence: NOT WRITTEN. {problem.GetType().FullName}: {problem.Message}");
            return problem;
        }
    }

    SeamHeader Gather()
    {
        GpuDeviceContext gpu = _fixture.DeviceContext;
        GpuBackendSelection selection = gpu.Selection;
        GpuDeviceDiagnostics diagnostics = gpu.Diagnostics;
        Type deviceType = gpu.GpuDevice.GetType();
        var device = new SeamDevice(gpu.Backend.ToString(), selection.Source.ToString(), selection.RequestedOverride,
            selection.RequestedBackend?.ToString(), gpu.AdapterDescription, diagnostics.SoftwareAdapter,
            diagnostics.DeviceLossReason, gpu.Capabilities.ClipSpaceYInverted, gpu.Capabilities.DepthRangeZeroToOne,
            deviceType.FullName ?? deviceType.Name);

        var assemblies = new List<SeamAssembly>();
        foreach (Type owner in new[] { deviceType, typeof(Scene3D), typeof(IGpuDevice), typeof(PointShadowSeamEvidence) })
        {
            SeamAssembly described = PointShadowSeamEvidenceOptions.Describe(owner.Assembly);
            if (!assemblies.Exists(a => a.Mvid == described.Mvid)) assemblies.Add(described);
        }

        (Matrix4x4 view, Matrix4x4 projection, Matrix4x4 viewProjection, Vector3 eye, Vector3 origin) =
            _fixture.CameraState;
        return new SeamHeader(_options.Provenance(assemblies), device, Shaders(),
            new SeamCamera(view, projection, viewProjection, eye, origin), _scene, _fixture.Resolved, _plain.Rgba, _soft.Rgba,
            _plain.ShadowedLights, _soft.ShadowedLights);
    }

    // Passive hashes of every catalog graphics program, not the programs this frame bound or device bytecode.
    static List<SeamShader> Shaders()
    {
        var shaders = new List<SeamShader>();
        foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
            shaders.Add(new SeamShader(program.Name, Sha256(program.VertexGlsl), Sha256(program.FragmentGlsl)));
        return shaders;
    }

    static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
