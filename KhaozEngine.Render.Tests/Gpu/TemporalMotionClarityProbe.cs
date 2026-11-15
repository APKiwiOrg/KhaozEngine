using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Re-evaluate the production decision over the frame's unchanged resolve inputs, without feedback.</summary>
internal sealed class TemporalMotionClarityProbe : IDisposable
{
    internal sealed record Diagnostics(double Motion, double CurrentWeight, double Released, double Veto,
        double Shimmer, double Clipped, double Disoccluded, double Reactive,
        double Confidence, double Lock, double Drift, double Swing, double StillShare, double FineShare);
    readonly IGpuDevice _device;
    readonly bool _fine;
    readonly IGpuResourceLayout? _detailLayout;
    readonly IGpuTexture _decisions, _flags;
    readonly IGpuFramebuffer _target;
    readonly IGpuCommandList _commands;
    readonly TemporalVisibilityPipelinePair _programs;

    internal TemporalMotionClarityProbe(TemporalFixture fx, TemporalDetailGate gate = TemporalDetailGate.Production)
    {
        _device = fx.Device;
        IGpuResourceFactory factory = _device.Factory;
        IGpuTexture Texture() => factory.CreateTexture(GpuTextureDescription.Texture2D(
            TemporalMotionClarityStage.W, TemporalMotionClarityStage.H, GpuPixelFormat.R16G16B16A16Float,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _decisions = Texture();
        _flags = Texture();
        _target = factory.CreateFramebuffer(null, _decisions, _flags);
        _commands = factory.CreateCommandList();
        TemporalResolveRenderer renderer = fx.Scene.TemporalResolveRendererForTests!;
        _fine = fx.Scene.TemporalHistory.DetailStore is not null;
        if (_fine) _detailLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            T("DetailColor"), T("DetailClassification")));
        var precision = TemporalResolvePrecisionPolicy.For(_device);
        string source = _fine ? ShaderSources.TemporalDetailResolveFragment(precision)
            : ShaderSources.TemporalResolveFragment(precision);
        _programs = new TemporalVisibilityPipelinePair(_device, source,
            _fine ? [renderer.ResolveLayout, _detailLayout!] : [renderer.ResolveLayout],
            _fine ? [renderer.FullyLitResolveLayout, _detailLayout!] : [renderer.FullyLitResolveLayout],
            _target.Outputs, text => Instrument(TemporalDetailGateVariants.Rewrite(text, gate), _fine));
    }

    static string Instrument(string source, bool fine)
    {
        source = Replace(source,
            "float alpha; float drift; float swing; float shimmer;",
            "float alpha; float drift; float swing; float shimmer; "
            + "float proofMotion; float proofWeight; float proofReleased; float proofVeto;");
        source = Replace(source, "    return result;\n}",
            "    result.proofMotion = motionPixels;\n    result.proofWeight = currentWeight;\n"
            + "    result.proofReleased = released ? 1.0 : 0.0;\n"
            + "    result.proofVeto = shadingFlickerVeto ? 1.0 : 0.0;\n    return result;\n}");
        source = Replace(source,
            "oColor = vec4(p.color, p.alpha + vUv.x * 1.0e-30);",
            "oColor = vec4(p.proofMotion, p.proofWeight, p.proofReleased, p.proofVeto"
                + (fine ? " + 2.0 * detailTransport.w" : "") + " + vUv.x * 1.0e-30);");
        if (fine)
        {
            for (int child = 0; child < 4; child++)
                source = Replace(source, $"layout(location={child + 2}) out vec4 oChild{child};", "");
            source = Replace(source, "    vec4 r = detailStoredResult(vec4(p.color, p.alpha));\n"
                + "    detailChildren(r, detailMean, detailA, detailB, detailC, detailD, detailTransport.z,\n"
                + "        oChild0, oChild1, oChild2, oChild3);", "");
        }
        return Replace(source,
            "oState = vec4(p.confidence, temporalStoreLock(p.stability, p.moved), p.drift, p.swing);",
            "oState = vec4(p.shimmer, p.clip, p.disocclusion, p.reactive);");
    }

    internal Diagnostics Read(TemporalFixture fx)
    {
        TemporalResolveRenderer renderer = fx.Scene.TemporalResolveRendererForTests!;
        using (GpuRecording.Open(_device, _commands, nameof(TemporalMotionClarityProbe)))
        {
            // A new recording can bind another uniform-ring segment. Re-upload this frame's exact values before use.
            var uniforms = renderer.LastUniforms;
            renderer.PrepareUniforms(_commands, uniforms,
                new TemporalDepthStoreUniforms { CurrentDepth = uniforms.CurrentDepth });
            _commands.SetFramebuffer(_target);
            _commands.SetPipeline(_programs.Select(renderer.LastVisibilityDecisionSkipped));
            _commands.SetGraphicsResourceSet(0, renderer.CurrentSet!);
            if (_fine) _commands.SetGraphicsResourceSet(1, fx.Scene.TemporalHistory.DetailStore!.ReadSet(
                fx.Scene.TemporalHistory.WriteIndex));
            _commands.Draw(3);
        }
        _device.Submit(_commands);
        _device.WaitForIdle();
        float[] decisions = TemporalTextureIo.Read(_device, _decisions);
        float[] flags = TemporalTextureIo.Read(_device, _flags);
        float[] state = TemporalTextureIo.Read(_device,
            fx.Scene.TemporalHistory.Confidence(fx.Scene.TemporalHistory.WriteIndex));
        var sum = new double[13];
        var region = TemporalMotionClarityMeasure.DetailRegion;
        for (int y = region.Y0; y < region.Y1; y++)
            for (int x = region.X0; x < region.X1; x++)
            {
                int p = (y * TemporalMotionClarityStage.W + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    sum[c] += c == 3 ? decisions[p + c] % 2f : decisions[p + c];
                    sum[c + 4] += flags[p + c];
                }
                sum[8] += state[p];
                float stored = state[p + 1];
                sum[9] += stored < -0.5f ? (-1f - stored) % 2f : stored;
                sum[10] += state[p + 2];
                sum[11] += state[p + 3];
                if (decisions[p] < TemporalResolveTuning.ShimmerStillPixels) sum[12]++;
            }
        for (int c = 0; c < sum.Length; c++) sum[c] /= region.Area;
        int finePixels = 0;
        for (int p = 3; p < decisions.Length; p += 4) if (decisions[p] >= 2f) finePixels++;
        double fineShare = finePixels / (double)(decisions.Length / 4);
        return new Diagnostics(sum[0], sum[1], sum[2], sum[3], sum[4], sum[5], sum[6], sum[7],
            sum[8], sum[9], sum[10], sum[11], sum[12], fineShare);
    }

    static string Replace(string source, string before, string after)
    {
        int at = source.IndexOf(before, StringComparison.Ordinal);
        if (at < 0 || source.IndexOf(before, at + before.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The motion measurement rewrite requires exactly one anchor.");
        return source.Replace(before, after, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _programs.Dispose();
        _detailLayout?.Dispose();
        _commands.Dispose();
        _target.Dispose();
        _decisions.Dispose();
        _flags.Dispose();
    }
}
