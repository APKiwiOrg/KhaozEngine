using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Native work only. The accepted session owns the sole production frame loop and timestamp/fence policy.
internal sealed class RendererPerfControlWorkload : IRendererPerfNativeWork
{
    readonly IGpuDevice _device;
    readonly RendererPerfControl _control;
    readonly RendererPerfControlFacts _facts;
    readonly List<(string Kind, IDisposable Resource)> _owned = [];
    readonly List<IGpuBuffer> _outputs = [];
    readonly List<IGpuResourceSet> _sets = [];
    readonly List<IGpuTexture> _colors = [];
    readonly IGpuTexture _host;
    IGpuBuffer? _input;
    IGpuComputePipeline? _compute;
    IGpuPipeline? _graphics;
    IGpuFramebuffer? _workTarget;
    IGpuTexture? _depth;
    bool _cold, _disposed;
    int _first, _recorded;
    internal RendererPerfControlWorkload(IGpuDevice device, RendererPerfControl control, RendererPerfControlFacts facts)
    {
        _device = device; _control = control; _facts = facts;
        try
        {
            _host = Own(device.Factory.CreateTexture(GpuTextureDescription.Texture2D(RendererPerfControls.Width,
                RendererPerfControls.Height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled)));
            Target = Own(device.Factory.CreateFramebuffer(null, _host));
            if (control.Id is >= 4 and <= 10) CreateCompute();
            else if (control.Id != 1) CreateGraphics();
        }
        catch (Exception error)
        {
            var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
            RendererPerfFailures.Add(failures, "renderer.control.construct", error);
            RendererPerfFailures.Release(failures, "renderer.session.dispose.control", Dispose);
            throw new GpuDiagnosticFailureException(failures.ToImmutable());
        }
    }
    T Own<T>(T resource) where T : IDisposable { _owned.Add((typeof(T).Name, resource)); return resource; }
    public IGpuFramebuffer Target { get; } = null!;
    public Color ClearColor => new(0f, 0f, 0f, 1f);
    public RendererPerfRecipeReport? Recipe => null;
    public string? ControlStateSha256 { get; private set; }
    internal RendererPerfControlFacts Facts => _facts;
    public ImmutableDictionary<string, RendererPerfMetric> ControlMetrics => new Dictionary<string, RendererPerfMetric>
    {
        ["control.readBytes"] = RendererPerfDriver.Value(_facts.ReadBytes, "bytes", "logical kernel reads, excludes common host clear"),
        ["control.writeBytes"] = RendererPerfDriver.Value(_facts.WriteBytes, "bytes", "logical kernel writes, excludes attachment initialization"),
        ["control.operations"] = RendererPerfDriver.Value(_facts.Operations, "operations", _facts.InstructionClass + ", verified intermediate emission"),
        ["control.dispatches"] = RendererPerfDriver.Value(_facts.Dispatches, "count", "fixed control definition"),
        ["control.serialEdges"] = RendererPerfDriver.Value(_facts.SerialEdges, "count", "combined dispatch/dependency overhead, not isolated barrier time"),
        ["control.draws"] = RendererPerfDriver.Value(_facts.Draws, "count", "fixed control definition"),
        ["control.outputValidated"] = ControlStateSha256 is null
            ? new(null, "count", "cold final readback", RendererPerfMetricStatus.NotExecuted, "The final output has not been checked.")
            : RendererPerfDriver.Value(1, "count", "every final output element checked outside timing")
    }.ToImmutableDictionary();

    void CreateCompute()
    {
        if (!_device.Capabilities.SupportsCompute) throw new NotSupportedException("The authorized control requires compute.");
        var f = _device.Factory;
        string source = RendererPerfControlShaders.Source(RendererPerfControlShaders.ComputeName(_control.Id));
        var shader = Own(f.CreateComputeShaderFromSpirv(source));
        var layout = Own(f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Input", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new GpuResourceLayoutElement("Output", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute))));
        _compute = Own(f.CreateComputePipeline(new(shader, layout)));
        uint inputBytes = _control.Id is 7 or 8 ? (uint)_facts.ReadBytes : _control.Id is 9 or 10 ? RendererPerfControls.ArithmeticInvocations * 4u : 4u;
        _input = Own(f.CreateBuffer(new(inputBytes, GpuBufferUsage.StructuredBufferReadOnly, 4)));
        int outputs = _control.Id is 5 or 6 ? 36 : 1;
        for (int i = 0; i < outputs; i++)
        {
            uint bytes = _control.Id is 7 or 8 or 9 or 10 ? (uint)_facts.WriteBytes : 4u;
            var output = Own(f.CreateBuffer(new(bytes, GpuBufferUsage.StructuredBufferReadWrite, 4)));
            IGpuBuffer input = _control.Id == 5 && i != 0 ? _outputs[i - 1] : _input;
            _sets.Add(Own(f.CreateResourceSet(new(layout, input, output))));
            _outputs.Add(output);
        }
    }
    void CreateGraphics()
    {
        var f = _device.Factory;
        bool shadow = _control.Id == 3;
        uint w = shadow ? 2048u : RendererPerfControls.Width, h = shadow ? 2048u : RendererPerfControls.Height;
        _depth = Own(f.CreateTexture(GpuTextureDescription.Texture2D(w, h, GpuPixelFormat.D32FloatS8UInt, GpuTextureUsage.DepthStencil | GpuTextureUsage.Sampled)));
        if (!shadow)
            foreach (var format in RendererPerfControls.MrtFormats)
                _colors.Add(Own(f.CreateTexture(GpuTextureDescription.Texture2D(w, h, format, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled))));
        _workTarget = Own(f.CreateFramebuffer(_depth, _colors.ToArray()));
        if (_control.Id < 11) return;
        var shaders = Own(f.CreateShadersFromSpirv(RendererPerfControlShaders.Source("fullscreen.vert"),
            RendererPerfControlShaders.Source("fullscreen.frag")));
        bool blended = _control.Id == 12;
        _graphics = Own(f.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            ShaderSet = shaders,
            ResourceLayouts = [],
            VertexLayouts = [],
            Outputs = _workTarget.Outputs,
            BlendAttachments = blended ? [GpuBlendAttachment.AlphaBlend, GpuBlendAttachment.PreserveDestination,
                GpuBlendAttachment.PreserveDestination, GpuBlendAttachment.PreserveDestination]
                : Enumerable.Repeat(GpuBlendAttachment.OverrideBlend, 4).ToArray(),
            DepthStencil = blended ? GpuDepthStencilState.DepthTestLessEqualNoWrite : GpuDepthStencilState.Disabled,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.CounterClockwise, true, false),
            Topology = GpuPrimitiveTopology.TriangleList
        }));
    }
    public void PrepareCold(int firstFrameIndex)
    {
        if (_cold || firstFrameIndex != -_control.WarmupFrames) throw new InvalidOperationException("Control preparation must match its one fixed block.");
        _first = firstFrameIndex;
        if (_input is { } input)
        {
            if (_control.Id is 9 or 10)
                _device.UpdateBuffer<float>(input, 0, RendererPerfControls.ArithmeticSeeds);
            else
            {
                uint[] chunk = Enumerable.Repeat(RendererPerfControls.Seed, (int)Math.Min(input.SizeInBytes / 4, 16384u)).ToArray();
                for (uint offset = 0; offset < input.SizeInBytes; offset += (uint)chunk.Length * 4)
                    _device.UpdateBuffer(input, offset, chunk);
            }
        }
        _cold = true;
    }
    public void Prepare(int frameIndex)
    {
        if (!_cold || _disposed || frameIndex != _first + _recorded) throw new InvalidOperationException("Control frame order differs from the finite schedule.");
    }
    public void Record(IGpuCommandList commands, int frameIndex)
    {
        Prepare(frameIndex);
        if (_recorded == _control.WarmupFrames + _control.MeasuredFrames) throw new InvalidOperationException("The finite control is complete.");
        if (_compute is { } compute)
        {
            commands.SetComputePipeline(compute);
            foreach (var set in _sets)
            {
                commands.SetComputeResourceSet(0, set);
                commands.Dispatch(_facts.GroupsX, _facts.GroupsY, 1);
            }
        }
        else if (_workTarget is { } target)
        {
            commands.SetFramebuffer(target);
            commands.SetFullScissorRects();
            // Initialization is part of the control frame and its raw outer interval, never subtracted.
            if (_control.Id != 11)
            {
                for (uint i = 0; i < _colors.Count; i++) commands.ClearColorTarget(i,
                    i == 3 ? new Color(65504f, 65504f, 0f, 0f) : new Color(.02f, .03f, .06f, 0f));
                commands.ClearDepthStencil(1f);
            }
            if (_graphics is { } graphics)
            {
                commands.SetPipeline(graphics);
                for (int i = 0; i < _facts.Draws; i++) commands.Draw(3);
            }
        }
        _recorded++;
    }
    public void ValidateOutput()
    {
        if (!_cold || _recorded != _control.WarmupFrames + _control.MeasuredFrames || ControlStateSha256 is not null)
            throw new InvalidOperationException("Output checks require the completed finite control and run once outside frame brackets.");
        if (_outputs.Count != 0)
        {
            if (_control.Id is 9 or 10)
                ControlStateSha256 = RendererPerfControls.ValidateArithmetic(_control.Id,
                    GpuReadback.ReadBuffer<float>(_device, _outputs[0], (int)_facts.WriteBytes / 4), RendererPerfControls.ArithmeticInvocations);
            else
            {
                uint[] words = _outputs.Count == 1
                    ? GpuReadback.ReadBuffer<uint>(_device, _outputs[0], (int)_outputs[0].SizeInBytes / 4)
                    : _outputs.SelectMany(buffer => GpuReadback.ReadBuffer<uint>(_device, buffer, (int)buffer.SizeInBytes / 4)).ToArray();
                ControlStateSha256 = RendererPerfControls.ValidateWords(_control.Id, words);
            }
        }
        else ControlStateSha256 = RendererPerfControlReadback.Validate(_device, _control.Id, _host, _colors, _depth);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        for (int i = _owned.Count - 1; i >= 0; i--)
            RendererPerfFailures.Release(failures, "renderer.session.dispose.control." + _owned[i].Kind + "." + i,
                _owned[i].Resource.Dispose);
        if (failures.Count != 0) throw new GpuDiagnosticFailureException(failures.ToImmutable());
    }
}
