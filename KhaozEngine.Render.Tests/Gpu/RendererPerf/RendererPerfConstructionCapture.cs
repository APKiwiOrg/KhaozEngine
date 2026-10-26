using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Append-only during work. Operation-local native lookup storage is cleared even on failed teardown.
internal sealed partial class RendererPerfConstructionCapture
{
    readonly ImmutableArray<string> _operations;
    readonly RendererPerfConstructionAdmission _admission = new();
    readonly RendererPerfShaderRecipe?[] _shaders = new RendererPerfShaderRecipe[RendererPerfConstructionAdmission.FactoryAttempts];
    readonly RendererPerfLayoutRecipe?[] _layouts = new RendererPerfLayoutRecipe[RendererPerfConstructionAdmission.FactoryAttempts];
    readonly RendererPerfPipelineRecipe?[] _recipes = new RendererPerfPipelineRecipe[RendererPerfConstructionAdmission.PipelineAttempts];
    readonly RendererPerfFactoryEvent?[] _events = new RendererPerfFactoryEvent[RendererPerfConstructionAdmission.FactoryAttempts];
    readonly RendererPerfPipelineOccurrence?[] _occurrences = new RendererPerfPipelineOccurrence[RendererPerfConstructionAdmission.PipelineAttempts];
    readonly object?[] _native = new object[RendererPerfConstructionAdmission.FactoriesPerOperation];
    readonly int[] _nativeIds = new int[RendererPerfConstructionAdmission.FactoriesPerOperation];
    readonly RendererPerfFactoryKind[] _nativeKinds = new RendererPerfFactoryKind[RendererPerfConstructionAdmission.FactoriesPerOperation];
    readonly int[] _attempts = new int[4], _successes = new int[4], _creationOrdinals = new int[4];
    int _operation = -1, _nextOperation, _operationFactories, _operationPipelines, _nativeCount;
    int _shaderCount, _layoutCount, _recipeCount, _eventCount, _occurrenceCount, _omitted;
    RendererPerfConstructionStage _stage;
    int? _frame;
    string? _failure;
    RendererPerfCompilerInventory? _frozen;
    internal bool HasMeasuredCreation { get; private set; }
    internal string? Failure => _failure ?? (HasMeasuredCreation ? "First pipeline creation occurred during a measured frame." : null);

    internal RendererPerfConstructionCapture(RendererPerfManifest manifest)
    {
        RendererPerfConstructionAdmission.Count(checked(1 + manifest.Blocks.Length + manifest.Controls.Length), 43, "operations");
        var operations = ImmutableArray.CreateBuilder<string>();
        operations.Add("conditioning:S3/TotalOnly/0");
        foreach (var b in manifest.Blocks) operations.Add($"{b.SceneId}/{b.Layout}/{b.Iteration}");
        foreach (var c in manifest.Controls) operations.Add($"control:{c.Id}/TotalOnly/0");
        RendererPerfConstructionAdmission.Count(operations.Count, 43, "operations");
        _operations = operations.ToImmutable();
        _admission.Reserve(0, 0, checked(94 + RendererPerfConstructionAdmission.CauseReserveBytes + RendererPerfConstructionAdmission.Text("khaozengine.renderer-compiler-inventory/1") + _operations.Sum(s => RendererPerfConstructionAdmission.Text(s))));
    }
    internal void BeginOperation(int operationOrdinal)
    {
        Open();
        if (_operation >= 0 || operationOrdinal != _nextOperation || (uint)operationOrdinal >= (uint)_operations.Length)
            throw new InvalidOperationException("Construction operation is foreign, repeated or out of order.");
        _operation = operationOrdinal; _nextOperation = checked(_nextOperation + 1); _operationFactories = _operationPipelines = 0;
        Array.Clear(_creationOrdinals); _stage = RendererPerfConstructionStage.Cold; _frame = null;
    }
    internal void EndOperation()
    {
        Open();
        if (_operation < 0) throw new InvalidOperationException("No construction operation is active.");
        Array.Clear(_native); Array.Clear(_nativeIds); Array.Clear(_nativeKinds); _nativeCount = 0;
        _operation = -1; _frame = null; _stage = RendererPerfConstructionStage.Cold;
    }
    internal void EnterFrame(RendererPerfConstructionStage stage, int frameIndex)
    {
        Open();
        if (_operation < 0 || _frame.HasValue || !Enum.IsDefined(stage)
            || stage == RendererPerfConstructionStage.Cold && _operation != 0
            || (stage == RendererPerfConstructionStage.Warmup) != (frameIndex < 0))
            throw new InvalidOperationException("Invalid construction frame boundary.");
        _stage = stage; _frame = frameIndex;
    }
    internal void LeaveFrame() { Open(); _stage = RendererPerfConstructionStage.Cold; _frame = null; }
    void Open() { if (_frozen is not null) throw new InvalidOperationException("Construction capture is frozen."); }
    void Refuse(string reason)
    {
        if (RendererPerfConstructionEncoding.Utf8.GetByteCount(reason) > RendererPerfConstructionAdmission.CauseReserveBytes - 4)
            reason = "Construction observer refusal exceeded its reserved cause envelope.";
        _failure ??= reason;
    }
    void Refuse(Exception error) => Refuse(error switch
    {
        RendererPerfConstructionRefusalException => error.Message,
        EncoderFallbackException => "Construction input is not valid UTF-16.",
        OverflowException => "Construction encoding or count overflowed.",
        _ => "Construction observer could not retain a valid input or backend witness."
    });

    // Only observer faults are caught here. The wrapper's native call has its own, unchanged throw path.
    int Start(RendererPerfFactoryKind kind, Func<int> input)
    {
        Open();
        _attempts[(int)kind] = checked(_attempts[(int)kind] + 1);
        int ordinal = _operationFactories; _operationFactories = checked(_operationFactories + 1);
        if (kind is RendererPerfFactoryKind.GraphicsPipeline or RendererPerfFactoryKind.ComputePipeline)
        {
            _operationPipelines = checked(_operationPipelines + 1); _creationOrdinals[(int)kind] = checked(_creationOrdinals[(int)kind] + 1);
            if (_stage == RendererPerfConstructionStage.Measured) HasMeasuredCreation = true;
        }
        try
        {
            if (_operation < 0) throw new RendererPerfConstructionRefusalException("Factory call is outside a construction operation.");
            RendererPerfConstructionAdmission.Count(_operationFactories, 1024, "factory attempts per operation");
            RendererPerfConstructionAdmission.Count(_operationPipelines, 256, "pipeline attempts per operation");
            if (_failure is not null) { _omitted = checked(_omitted + 1); return -1; }
            _admission.Reserve(0, 0, 22);
            int id = input();
            int token = _eventCount++;
            _events[token] = new(_operation, ordinal, kind, id, null, null);
            return token;
        }
        catch (Exception e) { Refuse(e); _omitted = checked(_omitted + 1); return -1; }
    }
    internal int Shader(string vertex, string? fragment, GpuBackendKind backend) => Start(RendererPerfFactoryKind.Shader, () =>
    {
        long bytes = RendererPerfConstructionAdmission.Text(vertex, 1048576);
        if (fragment is not null) bytes = checked(bytes + RendererPerfConstructionAdmission.Text(fragment, 1048576));
        _admission.Reserve(bytes, fragment is null ? 39 : backend == GpuBackendKind.Direct3D11Native ? 67 : 70, 0);
        int id = _shaderCount++;
        _shaders[id] = new(id, fragment is null ? [new("compute", "main", vertex, 0, 0, 0)]
            : [new("vertex", "main", vertex, 0, 0, 0), new(backend == GpuBackendKind.Direct3D11Native ? "pixel" : "fragment", "main", fragment, 0, 0, 0)]);
        return id;
    });
    internal int Layout(GpuResourceLayoutDescription d) => Start(RendererPerfFactoryKind.Layout, () =>
    {
        _admission.Reserve(0, RendererPerfConstructionAdmission.Layout(d), 0);
        int id = _layoutCount++; _layouts[id] = new(id, [.. d.Elements]); return id;
    });
    internal int Graphics(GpuPipelineDescription d) => Start(RendererPerfFactoryKind.GraphicsPipeline, () =>
    {
        long bytes = RendererPerfConstructionAdmission.Graphics(d);
        int shader = Find(d.ShaderSet, RendererPerfFactoryKind.Shader);
        _admission.Reserve(0, checked(bytes + 81 + RendererPerfConstructionAdmission.Digest(MissingCode)), 0);
        var layouts = LayoutIds(d.ResourceLayouts);
        int id = _recipeCount++;
        _recipes[id] = new(id, RendererPerfPipelineKind.Graphics, shader, layouts,
            new(d.BlendFactor, [.. d.BlendAttachments], d.DepthStencil, d.Rasterizer, d.Topology,
                [.. d.VertexLayouts.Select(v => new RendererPerfVertexRecipe(v.Stride, v.InstanceStepRate, [.. v.Elements]))],
                d.Outputs.Depth, [.. d.Outputs.Colour], d.Outputs.SampleCount), MissingCode, "");
        return id;
    });
    internal int Compute(GpuComputePipelineDescription d) => Start(RendererPerfFactoryKind.ComputePipeline, () =>
    {
        RendererPerfConstructionAdmission.Count(d.ResourceLayouts.Length, 32, "layout sets");
        int shader = Find(d.Shader, RendererPerfFactoryKind.Shader);
        _admission.Reserve(0, checked(85 + 4L * d.ResourceLayouts.Length + RendererPerfConstructionAdmission.Digest(MissingCode)), 0);
        var layouts = LayoutIds(d.ResourceLayouts);
        int id = _recipeCount++; _recipes[id] = new(id, RendererPerfPipelineKind.Compute, shader, layouts, null, MissingCode, "");
        return id;
    });
    static readonly GpuDiagnosticDigest MissingCode = new(null, "khaozengine.pipeline-code-generation/1", GpuDiagnosticStatus.Invalid,
        "Factory attempt has no successful construction witness.");
    ImmutableArray<int> LayoutIds(IGpuResourceLayout[] layouts)
    {
        RendererPerfConstructionAdmission.Count(layouts.Length, 32, "layout sets");
        // Validate every reference before allocating the detached reference array.
        foreach (var layout in layouts) Find(layout, RendererPerfFactoryKind.Layout);
        var ids = ImmutableArray.CreateBuilder<int>(layouts.Length);
        foreach (var layout in layouts) ids.Add(Find(layout, RendererPerfFactoryKind.Layout));
        return ids.MoveToImmutable();
    }
    int Find(object resource, RendererPerfFactoryKind kind)
    {
        for (int i = 0; i < _nativeCount; i++) if (ReferenceEquals(_native[i], resource) && _nativeKinds[i] == kind) return _nativeIds[i];
        throw new RendererPerfConstructionRefusalException("Construction input has no successful operation-local factory association.");
    }
    internal int Success(int token, object resource)
    {
        if (token < 0) return -1;
        var e = _events[token]!; _successes[(int)e.Kind] = checked(_successes[(int)e.Kind] + 1);
        try
        {
            int output = e.InputId;
            if (e.Kind is RendererPerfFactoryKind.GraphicsPipeline or RendererPerfFactoryKind.ComputePipeline)
            {
                if (resource is not IGpuPipelineConstructionDiagnostics diagnostics)
                    throw new RendererPerfConstructionRefusalException("Successful pipeline has no construction witness.");
                var witness = diagnostics.ConstructionIdentity;
                _admission.Reserve(0, checked(RendererPerfConstructionAdmission.Digest(witness.CodeGenerationSha256) - RendererPerfConstructionAdmission.Digest(MissingCode)),
                    checked(33 + (_frame.HasValue ? 4 : 0) + RendererPerfConstructionAdmission.Witness(witness)));
                witness = witness with { Stages = [.. witness.Stages] };
                var recipe = _recipes[e.InputId]!;
                _recipes[e.InputId] = recipe with { CodeGenerationSha256 = witness.CodeGenerationSha256 };
                output = _occurrenceCount++;
                _occurrences[output] = new(output, e.OperationOrdinal, _creationOrdinals[(int)e.Kind] - 1,
                    _stage, _frame, recipe.Kind, recipe.Id, witness, 0);
            }
            else if (e.Kind == RendererPerfFactoryKind.Shader && resource is IGpuComputeShader computeShader)
            {
                var shader = _shaders[e.InputId]!;
                _shaders[e.InputId] = shader with { Stages = [shader.Stages[0] with { LocalX = computeShader.ThreadGroupSizeX, LocalY = computeShader.ThreadGroupSizeY, LocalZ = computeShader.ThreadGroupSizeZ }] };
            }
            if (_nativeCount == _native.Length) throw new RendererPerfConstructionRefusalException("Native association capacity exceeded.");
            for (int i = 0; i < _nativeCount; i++)
                if (ReferenceEquals(_native[i], resource)) throw new RendererPerfConstructionRefusalException("Native factory return was duplicated.");
            _native[_nativeCount] = resource; _nativeIds[_nativeCount] = output; _nativeKinds[_nativeCount++] = e.Kind;
            _events[token] = e with { OutputId = output };
            return e.Kind is RendererPerfFactoryKind.GraphicsPipeline or RendererPerfFactoryKind.ComputePipeline ? output : -1;
        }
        catch (Exception error) { Refuse(error); _events[token] = null; _omitted = checked(_omitted + 1); return -1; }
    }
    internal void Failed(int token, Exception error)
    {
        if (token < 0) return;
        var e = _events[token]!;
        try
        {
            _admission.Reserve(0, 0, checked(RendererPerfConstructionAdmission.Text(error.Message, 16777216) - 4));
            _events[token] = e with { Failure = error.Message };
        }
        catch (Exception capacity) { Refuse(capacity); _events[token] = null; _omitted = checked(_omitted + 1); }
        Refuse("An actual factory attempt failed.");
    }
    internal void OmittedSuccess(RendererPerfFactoryKind kind) => _successes[(int)kind] = checked(_successes[(int)kind] + 1);
    internal void Bound(int occurrence, RendererPerfPipelineKind kind)
    {
        if (_frozen is not null) throw new InvalidOperationException("Construction capture is frozen.");
        if (_operation < 0 || (uint)occurrence >= (uint)_occurrenceCount || _occurrences[occurrence] is not { } row
            || row.OperationOrdinal != _operation || row.Kind != kind)
        { Refuse("Pipeline bind has no captured successful operation-local native return."); return; }
        // Replace no record on this path. A preallocated numeric table owns the mutable bind count.
        _binds[occurrence] = checked(_binds[occurrence] + 1);
    }
    readonly int[] _forwarded = new int[4];
    internal void Forwarded(RendererPerfFactoryKind kind) => _forwarded[(int)kind] = checked(_forwarded[(int)kind] + 1);
    readonly long[] _binds = new long[RendererPerfConstructionAdmission.PipelineAttempts];
}
