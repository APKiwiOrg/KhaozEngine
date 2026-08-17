using System;
using System.Numerics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The live depth field's shape as the shaders need it: whether one is bound at all, the world rectangle it
    /// covers, and its texel size. A pure value so <c>WaterRenderer.PackUbo</c> stays testable without a device,
    /// exactly as <c>WaterRenderer.OceanMaps</c> does for the cascades.
    /// </summary>
    /// <param name="Active">True when a field is uploaded and the shaders should read it.</param>
    /// <param name="Rect"><c>xy</c> = world min corner on XZ, <c>zw</c> = reciprocal world size.</param>
    /// <param name="TexelMetres">World size of one texel, the spacing the surf band's up-slope difference uses.</param>
    internal readonly record struct ShoreMaps(bool Active, Vector4 Rect, float TexelMetres);

    /// <summary>
    /// The GPU side of <see cref="WaterBathymetry"/>: one sampled depth texture, its clamped sampler, and the
    /// rectangle constants the water shaders map world XZ through. Owned by <see cref="WaterRenderer"/>, which
    /// hands it the frame's settings and binds <see cref="Texture"/> unconditionally.
    /// <para>
    /// <b>Uploads are gated on WHICH field and WHICH version of it, and that is the whole cost model.</b> A depth
    /// field is a property of the world, not of the frame: a consumer bakes it once at load and never touches it
    /// again, so the steady state here is zero work per frame. <see cref="WaterBathymetry.MarkChanged"/> is what
    /// re-uploads a field in place, so a game with a tide or a destructible coast pays exactly when it changes
    /// something, and REPLACING <see cref="WaterSettings.Bathymetry"/> with another field re-uploads too.
    /// </para>
    /// <para>
    /// <b>The identity half of that gate is not optional (#645).</b>
    /// <see cref="WaterBathymetry.Revision"/> is a per-instance counter starting at 0, so two freshly built fields
    /// both sit at 1 after one <see cref="WaterBathymetry.FillFromGround"/>. Comparing the revision alone made a
    /// same-resolution replacement a no-op and left the PREVIOUS field's depths on the GPU, with
    /// <see cref="Active"/> true and a plausible-looking picture rather than an error. Comparing the field by
    /// reference alongside its revision is the same shape <see cref="WaterRenderer"/> already binds its resource
    /// set with (<c>ReferenceEquals(_bound, res) &amp;&amp; res.Generation == _boundGen</c>), and it costs one
    /// reference compare on a path that already early-outs. The map holds that reference, so the last uploaded
    /// field stays alive as long as the texture built from it does.
    /// </para>
    /// <para>
    /// <b>rgba16f rather than a single-channel float.</b> <see cref="GpuPixelFormat.R32Float"/> is documented as
    /// not linearly filterable on Metal, and this field is read bilinearly in both stages - a point-sampled depth
    /// would put the texel grid straight into the surf band's edge. rgba16f is the format the ocean maps already
    /// prove filterable on all three backends; depth rides in <c>.r</c> and the other three channels are unused.
    /// Half precision is a non-issue for what this drives: the step is under a millimetre in the first metre of
    /// depth and about 60 mm at 100 m, and nothing here reacts to 100 m of water at all.
    /// </para>
    /// <para>
    /// A 1x1 PLACEHOLDER is created up front and bound whenever no field is set, so the water pipeline's resource
    /// layout is the same shape in both cases and needs no second pipeline - the same trick
    /// <see cref="OceanFftProducer"/>'s idle map plays for the cascades.
    /// </para>
    /// </summary>
    internal sealed class WaterBathymetryMap : IDisposable
    {
        readonly IGpuDevice _gd;
        IGpuTexture? _idle;
        IGpuSampler? _sampler;
        IGpuTexture? _map;
        int _resolution;
        WaterBathymetry? _uploaded;
        int _revision = int.MinValue;
        byte[] _scratch = Array.Empty<byte>();

        public WaterBathymetryMap(IGpuDevice gd) => _gd = gd;

        /// <summary>True once <see cref="Update"/> has a live field uploaded. False when the consumer set no
        /// <see cref="WaterSettings.Bathymetry"/>, which is the default.</summary>
        public bool Active { get; private set; }

        /// <summary>The depth texture, always bindable (a 1x1 placeholder when inactive).</summary>
        public IGpuTexture Texture => _map ?? _idle!;

        /// <summary>The CLAMPED bilinear sampler the field is read through. Clamping matters: unlike a cascade the
        /// rectangle has edges, and wrapping one would put the far shore's depth against the near one. The shaders
        /// also range-check the coordinate and report deep water outside, so the clamped edge value is never what
        /// an out-of-rect fragment actually sees.</summary>
        public IGpuSampler Sampler => _sampler!;

        /// <summary><c>xy</c> = the field's world min corner on XZ, <c>zw</c> = the reciprocal of its world size:
        /// the shader's <c>BathyRect</c>, so a world position becomes normalized coordinates with one
        /// multiply-add.</summary>
        public Vector4 Rect { get; private set; }

        /// <summary>World size of one texel, metres. The surf band's up-slope difference is taken over it, so it
        /// is the finest coastline feature the surge direction can see.</summary>
        public float TexelMetres { get; private set; }

        /// <summary>Uploads the last <see cref="Update"/> performed. 0 on a steady frame, which is the point.
        /// Internal, for the test that pins that as a measured number rather than a claim.</summary>
        public int LastUploads { get; private set; }

        /// <summary>
        /// Bring the depth texture up to date with the frame's settings. Cheap and idempotent: with no field set,
        /// or with the SAME field still at the same <see cref="WaterBathymetry.Revision"/>, it does nothing but
        /// report. A different field always uploads, whatever its revision reads.
        /// </summary>
        /// <param name="field">The consumer's field, or null.</param>
        /// <returns>True when a field is live and the shaders should read it.</returns>
        public bool Update(WaterBathymetry? field)
        {
            EnsureIdle();
            LastUploads = 0;
            if (field == null)
            {
                // The texture and what it holds are BOTH kept across an inactive stretch, deliberately: a consumer
                // that switches shoaling off and back on with the same field pays nothing, and the picture is
                // right because only another field could have overwritten the texture meanwhile.
                Active = false;
                return false;
            }

            if (_map == null || _resolution != field.Resolution)
            {
                // Drain before dropping: the previous texture may still be referenced by the frame in flight, and
                // disposing a live resource against a busy device is the teardown race the seam already knows.
                if (_map != null) _gd.WaitForIdle();
                _map?.Dispose();
                _resolution = field.Resolution;
                _map = _gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                    (uint)_resolution, (uint)_resolution, GpuPixelFormat.R16G16B16A16Float,
                    GpuTextureUsage.Sampled));
                _scratch = new byte[_resolution * _resolution * 8];
                _revision = int.MinValue;
            }

            // Identity FIRST, then version. A revision only means anything within one field: the resize branch
            // above catches a replacement that changes the resolution, and this catches the ordinary case where it
            // does not (#645). Note that leaves the branch above resetting a revision the reference compare would
            // have caught anyway, which is belt and braces rather than the thing doing the work.
            if (!ReferenceEquals(_uploaded, field) || _revision != field.Revision)
            {
                Pack(field.Depths, _scratch, _resolution);
                _gd.UpdateTexture(_map, _scratch, 0, 0, (uint)_resolution, (uint)_resolution);
                _uploaded = field;
                _revision = field.Revision;
                LastUploads = 1;
            }

            Rect = new Vector4(
                field.CenterX - field.HalfExtentX, field.CenterZ - field.HalfExtentZ,
                1f / (2f * field.HalfExtentX), 1f / (2f * field.HalfExtentZ));
            TexelMetres = MathF.Max(field.TexelSizeX, field.TexelSizeZ);
            Active = true;
            return true;
        }

        /// <summary>Snapshot what the shaders need, as a device-free value.</summary>
        public ShoreMaps Snapshot() => Active ? new ShoreMaps(true, Rect, TexelMetres) : default;

        /// <summary>Depth metres into rgba16f bytes, red channel only. The other three are left at zero: they cost
        /// bandwidth on the one upload and nothing per frame, and a packed single-channel format is not an option
        /// (see the class note on filtering).</summary>
        internal static void Pack(float[] depths, byte[] destination, int resolution)
        {
            int texels = resolution * resolution;
            Array.Clear(destination, 0, texels * 8);
            for (int i = 0; i < texels; i++)
            {
                short bits = BitConverter.HalfToInt16Bits((Half)depths[i]);
                destination[i * 8] = (byte)(bits & 0xFF);
                destination[i * 8 + 1] = (byte)((bits >> 8) & 0xFF);
            }
        }

        void EnsureIdle()
        {
            if (_sampler != null) return;
            IGpuResourceFactory f = _gd.Factory;
            _sampler = f.CreateSampler(new GpuSamplerDescription(GpuSamplerFilter.MinLinearMagLinearMipLinear,
                GpuSamplerAddress.Clamp, GpuSamplerAddress.Clamp, GpuSamplerAddress.Clamp));
            _idle = f.CreateTexture(GpuTextureDescription.Texture2D(1, 1,
                GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Sampled));
        }

        public void Dispose()
        {
            _map?.Dispose();
            _idle?.Dispose();
            _sampler?.Dispose();
        }
    }
}
