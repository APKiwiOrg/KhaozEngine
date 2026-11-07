using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Packed <c>CausticBody</c> block (<see cref="ShaderSources"/>). Twelve vec4 in one 256-byte slot.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WaterCausticUniforms
{
    public Vector4 CausticWindow;   // minX, minZ, inverse extent, cell metres
    public Vector4 CausticAtlas;    // layer index, atlas layer capacity, clip Y sign, 0
    public Vector4 CausticRect;     // render-relative min XZ, max XZ
    public Vector4 SunTravel;       // normalized LightDir, strength
    public Vector4 StillRay;        // t0, D
    public Vector4 WaveParams;
    public Vector4 DetailParams;
    public Vector4 SwellParams;
    public Vector4 SwellShape;
    public Vector4 RippleSpectrum;
    public Vector4 FootprintParams;
    public Vector4 RenderOrigin;
}

/// <summary>
/// Draws the frame's surface field and caustic map from a <see cref="WaterCausticPlan"/>. Exists only once a body has
/// participated, because a scene that never does owns none of this. Uploads precede the first framebuffer, and both
/// atlases clear once per frame as a whole, since a layer's column is a scissor rather than its own target.
/// </summary>
internal sealed partial class WaterCausticRenderer : IDisposable
{
    internal const uint SlotBytes = 256;

    /// <summary>Draws one body records: its field quad, its subtract quad and its sheet.</summary>
    internal const int DrawCallsPerBody = 3;

    /// <summary>Stamp every slot and every patch into the CPU images, upload both, then draw both atlases.</summary>
    internal void Record(IGpuCommandList cl, WaterCausticPlan plan, ReadOnlySpan<WaterPlane> planes,
        WaterSettings scene, Vector3 lightDirection, float timeSeconds, Vector3 renderOrigin)
    {
        ReadOnlySpan<WaterCausticBody> bodies = plan.Bodies;
        if (bodies.IsEmpty) return;
        EnsureSlots(bodies.Length);

        Vector3 sun = Vector3.Normalize(lightDirection);
        Vector3 still = WaterRefractionShaderSource.SunTravel(sun);
        WaterSurfaceWindow window = plan.Window;
        int instances = 0;
        for (int i = 0; i < bodies.Length; i++)
        {
            ref readonly WaterCausticBody body = ref bodies[i];
            WaterLook? look = planes[body.PlaneIndex].Look;
            WaterSettings settings = look is null ? scene : look.ResolveInto(_effective, scene);
            WaterCausticUniforms uniforms = Pack(body, window, sun, still, settings, timeSeconds, renderOrigin);
            MemoryMarshal.Write(_image.AsSpan(i * (int)SlotBytes, (int)SlotBytes), in uniforms);

            ReserveInstances(instances + WaterCausticPlan.MaxInstancesPerBody);
            int count = WaterCausticPlan.WritePatches(body, window, _instances.AsSpan(instances));
            _firstInstance[i] = instances;
            _instanceCount[i] = count;
            instances += count;
        }
        EnsureInstanceBuffer(instances);

        cl.UpdateBuffer(_uniformBuffer, 0, (ReadOnlySpan<byte>)_image);
        cl.UpdateBuffer<Vector3>(_instanceBuffer!, 0, _instances.AsSpan(0, instances));

        DrawField(cl, bodies);
        DrawMap(cl, bodies);
        cl.SetFullScissorRects();
    }

    WaterCausticUniforms Pack(in WaterCausticBody body, in WaterSurfaceWindow window, Vector3 sun, Vector3 still,
        WaterSettings settings, float timeSeconds, Vector3 renderOrigin)
    {
        WaterWaveUniforms waves = WaterWaveUniforms.From(settings, timeSeconds);
        return new WaterCausticUniforms
        {
            CausticWindow = new Vector4(window.MinX, window.MinZ, 1f / WaterSurfaceWindow.ExtentMetres,
                WaterSurfaceWindow.CellMetres),
            CausticAtlas = new Vector4(body.Layer, AtlasLayers, _clipYSign, 0f),
            CausticRect = new Vector4(body.RectMin.X, body.RectMin.Y, body.RectMax.X, body.RectMax.Y),
            SunTravel = new Vector4(sun, body.Strength),
            StillRay = new Vector4(still, body.Depth),
            WaveParams = waves.WaveParams,
            DetailParams = waves.DetailParams,
            SwellParams = waves.SwellParams,
            SwellShape = waves.SwellShape,
            RippleSpectrum = waves.RippleSpectrum,
            FootprintParams = waves.FootprintParams,
            RenderOrigin = new Vector4(renderOrigin, 0f),
        };
    }

    void DrawField(IGpuCommandList cl, ReadOnlySpan<WaterCausticBody> bodies)
    {
        cl.SetFramebuffer(_fieldFramebuffer!);
        cl.ClearColorTarget(0, new Color(0f, 0f, 0f, 0f));
        cl.SetPipeline(_fieldPipeline);
        for (int i = 0; i < bodies.Length; i++)
        {
            cl.SetScissorRect(0, (uint)bodies[i].Layer * FieldSide, 0, FieldSide, FieldSide);
            cl.SetGraphicsResourceSet(0, _fieldSet, (uint)i * SlotBytes);
            cl.Draw(QuadVertices);
        }
    }

    void DrawMap(IGpuCommandList cl, ReadOnlySpan<WaterCausticBody> bodies)
    {
        cl.SetFramebuffer(_mapFramebuffer!);
        cl.ClearColorTarget(0, Color.White);
        for (int i = 0; i < bodies.Length; i++)
        {
            uint slot = (uint)i * SlotBytes;
            cl.SetScissorRect(0, (uint)bodies[i].Layer * MapSide, 0, MapSide, MapSide);

            // The map starts at 1. Taking the rectangle's unit off first is what leaves an entry past its reach at 1.
            cl.SetPipeline(_subtractPipeline);
            cl.SetGraphicsResourceSet(0, _subtractSet, slot);
            cl.Draw(QuadVertices);

            cl.SetPipeline(_sheetPipeline);
            cl.SetGraphicsResourceSet(0, _sheetSet!, slot);
            cl.SetVertexBuffer(0, _instanceBuffer!);
            cl.Draw(PatchVertexCount, (uint)_instanceCount[i], 0, (uint)_firstInstance[i]);
        }
    }
}
