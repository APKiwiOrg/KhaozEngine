using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>
/// A grow-only queue of invalid actor histories, rasterised into an internal-resolution R8 mask. Absolute boxes
/// are projected against each view's render origin. Old boxes cover both the old screen site and the site to which
/// a static old surface reprojects now. Current boxes cover the replacement. No global history reset is involved.
/// </summary>
internal sealed class TemporalHistoryRejection : IDisposable
{
    readonly IGpuDevice _gd;
    readonly GpuRetireQueue _retired;
    readonly bool _ownsRetired;
    (MeshBounds? Current, MeshBounds? Previous)[] _regions = [];
    Vector2[] _vertices = [];
    uint[] _indices = [];
    int _count, _vertexCount, _width, _height;
    IGpuTexture? _maskTexture;
    IGpuBuffer? _vb, _ib;
    IGpuFramebuffer? _framebuffer;
    IGpuShaderSet? _shaders;
    IGpuPipeline? _pipeline;

    public TemporalHistoryRejection(IGpuDevice gd) : this(gd, GpuRetireQueue.Create(gd)) => _ownsRetired = true;

    internal TemporalHistoryRejection(IGpuDevice gd, GpuRetireQueue retired) => (_gd, _retired) = (gd, retired);

    public IGpuTexture? Texture { get; private set; }
    internal int DrawCalls { get; private set; }

    public void Reserve(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_regions.Length < count) Array.Resize(ref _regions, Math.Max(count, checked(_regions.Length * 2)));
    }

    public void Clear() => _count = 0;

    public void Add(MeshBounds? currentWorld, MeshBounds? previousWorld)
    {
        if (currentWorld is null && previousWorld is null) return;
        Reserve(checked(_count + 1));
        _regions[_count++] = (currentWorld, previousWorld);
    }

    void EnsureSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_maskTexture is { } current && current.Width == width && current.Height == height) return;
        Free(ref _framebuffer, _retired);
        _retired.Retire(_maskTexture);
        _maskTexture = _gd.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height,
            GpuPixelFormat.R8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _framebuffer = _gd.Factory.CreateFramebuffer(null, _maskTexture);
    }

    public void Prepare(IGpuCommandList commands, in FrameView current, FrameView? previous)
    {
        Select(current, previous);
        Record(commands);
    }

    // Select before the resolve binds inputs, but record only immediately before accumulation. An empty selection
    // uses the resolve's persistent zero texel. The retained full-size target can contain an older active mask.
    internal void Select(in FrameView current, FrameView? previous)
    {
        if (_ownsRetired) _retired.BeginFrame();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(current.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(current.Height);
        _width = current.Width;
        _height = current.Height;
        ReserveGeometry(checked(_count * 3));
        _vertexCount = 0;
        for (int i = 0; i < _count; i++)
        {
            var region = _regions[i];
            if (region.Current is { } now) Project(now, current);
            if (region.Previous is not { } old) continue;
            Project(old, current);
            if (previous is { } last) Project(old, last);
        }
        DrawCalls = _vertexCount > 0 ? 1 : 0;
        Texture = null;
        if (_vertexCount == 0) return;
        EnsureSize(_width, _height);
        Texture = _maskTexture;
    }

    internal void Record(IGpuCommandList commands)
    {
        if (_vertexCount == 0) return;
        EnsurePipeline();
        EnsureGeometry();
        commands.UpdateBuffer<Vector2>(_vb!, 0, _vertices.AsSpan(0, _vertexCount));
        commands.UpdateBuffer<uint>(_ib!, 0, _indices.AsSpan(0, _vertexCount / 4 * 6));
        commands.SetFramebuffer(_framebuffer!);
        commands.ClearColorTarget(0, new Color(0f, 0f, 0f, 0f));
        commands.SetPipeline(_pipeline!);
        commands.SetVertexBuffer(0, _vb!);
        commands.SetIndexBuffer(_ib!, GpuIndexFormat.UInt32);
        commands.DrawIndexed((uint)(_vertexCount / 4 * 6), 1, 0, 0, 0);
    }

    void ReserveGeometry(int rectangles)
    {
        int vertices = checked(rectangles * 4);
        if (_vertices.Length >= vertices) return;
        vertices = Math.Max(vertices, checked(_vertices.Length * 2));
        Array.Resize(ref _vertices, vertices);
        Array.Resize(ref _indices, checked(vertices / 4 * 6));
        for (uint r = 0; r < vertices / 4; r++)
        {
            uint v = r * 4;
            int at = (int)r * 6;
            _indices[at] = v; _indices[at + 1] = v + 1; _indices[at + 2] = v + 2;
            _indices[at + 3] = v; _indices[at + 4] = v + 2; _indices[at + 5] = v + 3;
        }
    }

    void EnsureGeometry()
    {
        uint bytes = checked((uint)_vertices.Length * 8);
        if (_vb is not null && _vb.SizeInBytes >= bytes) return;
        Free(ref _vb, _retired);
        Free(ref _ib, _retired);
        _vb = _gd.Factory.CreateBuffer(new GpuBufferDescription(bytes, GpuBufferUsage.VertexBuffer));
        _ib = _gd.Factory.CreateBuffer(new GpuBufferDescription(checked((uint)_indices.Length * 4), GpuBufferUsage.IndexBuffer));
    }

    void EnsurePipeline()
    {
        if (_pipeline is not null) return;
        _shaders = _gd.Factory.CreateShadersFromSpirv(ShaderSources.HistoryRejectionVert, ShaderSources.HistoryRejectionFrag);
        _pipeline = _gd.Factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                depthClipEnabled: false, scissorTestEnabled: false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [],
            ShaderSet = _shaders,
            VertexLayouts = new List<GpuVertexLayoutDescription>
            {
                new(new GpuVertexElement("Position", GpuVertexElementFormat.Float2)),
            },
            Outputs = new GpuOutputDescription(null, GpuPixelFormat.R8UNorm),
        });
    }

    // Clip the transformed box at the near plane BEFORE division. A clipped convex box has vertices at admitted
    // corners and the intersections of its twelve edges. Projective extrema with positive W occur at those vertices.
    // Expanding the clip plane and each projected coordinate by the transform error also covers near-plane rounding.
    void Project(in MeshBounds bounds, in FrameView view)
    {
        if (!Finite(bounds.Min) || !Finite(bounds.Max) || bounds.Min.X > bounds.Max.X
            || bounds.Min.Y > bounds.Max.Y || bounds.Min.Z > bounds.Max.Z)
        {
            Rectangle(-1, -1, 1, 1);
            return;
        }
        Span<Clip> corners = stackalloc Clip[8];
        double magnitude = 1;
        Matrix4x4 m = view.ViewProjection;
        for (int i = 0; i < 8; i++)
        {
            double x = (double)((i & 1) == 0 ? bounds.Min.X : bounds.Max.X) - view.RenderOrigin.X;
            double y = (double)((i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y) - view.RenderOrigin.Y;
            double z = (double)((i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z) - view.RenderOrigin.Z;
            corners[i] = new Clip(Coordinate(x, y, z, m.M11, m.M21, m.M31, m.M41, ref magnitude),
                Coordinate(x, y, z, m.M12, m.M22, m.M32, m.M42, ref magnitude),
                Coordinate(x, y, z, m.M13, m.M23, m.M33, m.M43, ref magnitude),
                Coordinate(x, y, z, m.M14, m.M24, m.M34, m.M44, ref magnitude));
            if (!corners[i].Finite)
            {
                Rectangle(-1, -1, 1, 1);
                return;
            }
        }
        // 64 binary32 units cover the four-product transform, render-relative subtraction and double clipping.
        // The extra two internal pixels cover rasterisation, jitter and the reconstruction neighbourhood at an edge.
        double error = magnitude * (64.0 / 16777216.0);
        Span<Clip> clipped = stackalloc Clip[20];
        int count = 0;
        bool beyondFar = true;
        for (int i = 0; i < 8; i++)
        {
            Clip a = corners[i];
            beyondFar &= a.Z > a.W + 2 * error;
            if (a.Z >= -error) clipped[count++] = a;
            for (int axis = 1; axis <= 4; axis *= 2)
            {
                if ((i & axis) != 0) continue;
                Clip b = corners[i | axis];
                if ((a.Z >= -error) == (b.Z >= -error)) continue;
                double t = (-error - a.Z) / (b.Z - a.Z);
                clipped[count++] = new Clip(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y), -error,
                    a.W + t * (b.W - a.W));
            }
        }
        if (count == 0 || beyondFar) return;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            Clip c = clipped[i];
            if (!c.Finite || c.W <= error)
            {
                Rectangle(-1, -1, 1, 1);
                return;
            }
            Include(c.X, c.W, error, ref minX, ref maxX);
            Include(c.Y, c.W, error, ref minY, ref maxY);
        }
        int width = _width, height = _height;
        double left = Math.Floor((minX * .5 + .5) * width) - 2;
        double right = Math.Ceiling((maxX * .5 + .5) * width) + 2;
        double bottom = Math.Floor((minY * .5 + .5) * height) - 2;
        double top = Math.Ceiling((maxY * .5 + .5) * height) + 2;
        Rectangle(left * 2 / width - 1, bottom * 2 / height - 1, right * 2 / width - 1, top * 2 / height - 1);
    }

    static double Coordinate(double x, double y, double z, float a, float b, float c, float d, ref double magnitude)
    {
        magnitude = Math.Max(magnitude, Math.Abs(x * a) + Math.Abs(y * b) + Math.Abs(z * c) + Math.Abs(d));
        return ((x * a + y * b) + z * c) + d;
    }

    static void Include(double value, double w, double error, ref double min, ref double max)
    {
        double low = value - error, high = value + error;
        min = Math.Min(min, low / (low < 0 ? w - error : w + error));
        max = Math.Max(max, high / (high > 0 ? w - error : w + error));
    }

    void Rectangle(double left, double bottom, double right, double top)
    {
        left = Math.Max(-1, left); bottom = Math.Max(-1, bottom);
        right = Math.Min(1, right); top = Math.Min(1, top);
        if (left >= right || bottom >= top) return;
        float flip = _gd.Capabilities.ClipSpaceYInverted ? -1 : 1;
        _vertices[_vertexCount++] = new((float)left, (float)bottom * flip);
        _vertices[_vertexCount++] = new((float)right, (float)bottom * flip);
        _vertices[_vertexCount++] = new((float)right, (float)top * flip);
        _vertices[_vertexCount++] = new((float)left, (float)top * flip);
    }

    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    readonly record struct Clip(double X, double Y, double Z, double W)
    {
        internal bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z) && double.IsFinite(W);
    }

    public void Dispose()
    {
        if (_ownsRetired) _retired.Dispose();
        _framebuffer?.Dispose();
        _maskTexture?.Dispose();
        _vb?.Dispose(); _ib?.Dispose();
        _pipeline?.Dispose(); _shaders?.Dispose();
        Texture = null;
    }
}
