using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public partial class UboLayoutTests
    {
        // ---- Water surface pass UBO (WaterRenderer) ----

        [Fact]
        public void WaterBlock_Preserves1280ByteSlots()
        {
            Assert.Equal(1136, Marshal.SizeOf<WaterRenderer.WaterUbo>());
            Assert.Equal(1056, Marshal.OffsetOf<WaterRenderer.WaterUbo>("OpticsRect").ToInt32());
            Assert.Equal(1072, Marshal.OffsetOf<WaterRenderer.WaterUbo>("OpticsParams").ToInt32());
            Assert.Equal(1088, Marshal.OffsetOf<WaterRenderer.WaterUbo>(nameof(WaterRenderer.WaterUbo.ColumnExtinction)).ToInt32());
            Assert.Equal(1104, Marshal.OffsetOf<WaterRenderer.WaterUbo>(nameof(WaterRenderer.WaterUbo.ColumnScattering)).ToInt32());
            Assert.Equal(1120, Marshal.OffsetOf<WaterRenderer.WaterUbo>(nameof(WaterRenderer.WaterUbo.ColumnAlbedo)).ToInt32());
            Assert.Equal(1280u, WaterRenderer.SlotBytes);
            Assert.Equal((int)WaterRenderer.PayloadBytes, Marshal.SizeOf<WaterRenderer.WaterUbo>());
            Assert.Equal(2 * 64 + (39 + 3 * SkySettings.MaxDiscs) * 16, (int)WaterRenderer.PayloadBytes);
            Assert.Equal(0u, WaterRenderer.SlotBytes % 256u);
            Assert.True(WaterRenderer.SlotBytes >= WaterRenderer.PayloadBytes);
            var matrix = System.Numerics.Matrix4x4.Identity;
            var sky = new SkySettings();
            var water = new WaterSettings();
            var legacy = WaterRenderer.PackUbo(matrix, matrix, -System.Numerics.Vector3.UnitY,
                KhaozEngine.Primitives.Color.White, System.Numerics.Vector3.UnitY, water, sky, 0);
            var disabled = WaterRenderer.PackUbo(matrix, matrix, -System.Numerics.Vector3.UnitY,
                KhaozEngine.Primitives.Color.White, System.Numerics.Vector3.UnitY, water, sky, 0,
                default, atmosphereEnabled: false);
            Assert.Equal(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref legacy, 1)).ToArray(),
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref disabled, 1)).ToArray());
            var enabled = WaterRenderer.PackUbo(matrix, matrix, -System.Numerics.Vector3.UnitY,
                KhaozEngine.Primitives.Color.White, System.Numerics.Vector3.UnitY, water, sky, 0,
                default, atmosphereEnabled: true);
            Assert.Equal(0, legacy.SkyParams.Y);
            Assert.Equal(1, enabled.SkyParams.Y);
            Assert.Equal(sky.SunEnabled ? 1 : 0, enabled.SkyDiscHalo[0].Z);
        }

        [Fact]
        public void WaterFrag_DeclaresAllMembers()
        {
            // Assert every member of the Water block is present (in the fragment that reads it), so a
            // rename/reorder on the shader side that silently changes the layout trips here. Other half:
            // ShaderSources.WaterFrag.
            foreach (var member in new[] { "mat4 ViewProj;", "mat4 InvViewProj;", "vec4 LightDir;", "vec4 LightColor;",
                "vec4 CameraPos;", "vec4 DeepColor;", "vec4 ShallowColor;", "vec4 HorizonColor;", "vec4 WaveParams;",
                "vec4 ShoreGlint;", "vec4 DetailParams;", "vec4 SkyHorizon;", "vec4 SkyZenith;",
                "vec4 SkyParams;", "vec4 ReflectGlint;", "vec4 SwellParams;", "vec4 SwellShape;", "vec4 Absorption;",
                "vec4 FoamColor;", "vec4 FoamParams;", "vec4 RippleSpectrum;", "vec4 FootprintParams;",
                "vec4 FftParams;", "vec4 FftTiles;", "vec4 FftVariance;", "vec4 FftFocus;", "vec4 FftRotCos;",
                "vec4 FftRotSin;", "vec4 FftSector;", "vec4 FftWave;", "vec4 BathyRect;", "vec4 BathyParams;",
                "vec4 SurfParams;", "vec4 SurfShape;", "vec4 RenderOrigin;", "vec4 SkyGround;", "vec4 OpticsRect;", "vec4 OpticsParams;",
                "vec4 SkyDiscColor[8];", "vec4 SkyDiscDir[8];", "vec4 SkyDiscHalo[8];", "vec4 ColumnExtinction;", "vec4 ColumnScattering;", "vec4 ColumnAlbedo;" })
                Assert.True(ShaderSources.WaterFrag.Contains(member),
                    $"WaterFrag lost '{member}': the Water UBO block drifted from WaterRenderer.WaterUbo. Fix ShaderSources.WaterFrag or the struct.");
        }

        [Fact]
        public void WaterVert_DeclaresSameBlockAsWaterFrag()
        {
            // The vertex stage only READS ViewProj, but the block must be declared identically in both stages
            // (same one-UBO-per-set buffer) or the two stages disagree on the layout the driver builds.
            foreach (var member in new[] { "mat4 ViewProj;", "mat4 InvViewProj;", "vec4 LightDir;", "vec4 LightColor;",
                "vec4 CameraPos;", "vec4 DeepColor;", "vec4 ShallowColor;", "vec4 HorizonColor;", "vec4 WaveParams;",
                "vec4 ShoreGlint;", "vec4 DetailParams;", "vec4 SkyHorizon;", "vec4 SkyZenith;",
                "vec4 SkyParams;", "vec4 ReflectGlint;", "vec4 SwellParams;", "vec4 SwellShape;", "vec4 Absorption;",
                "vec4 FoamColor;", "vec4 FoamParams;", "vec4 RippleSpectrum;", "vec4 FootprintParams;",
                "vec4 FftParams;", "vec4 FftTiles;", "vec4 FftVariance;", "vec4 FftFocus;", "vec4 FftRotCos;",
                "vec4 FftRotSin;", "vec4 FftSector;", "vec4 FftWave;", "vec4 BathyRect;", "vec4 BathyParams;",
                "vec4 SurfParams;", "vec4 SurfShape;", "vec4 RenderOrigin;", "vec4 SkyGround;", "vec4 OpticsRect;", "vec4 OpticsParams;",
                "vec4 SkyDiscColor[8];", "vec4 SkyDiscDir[8];", "vec4 SkyDiscHalo[8];", "vec4 ColumnExtinction;", "vec4 ColumnScattering;", "vec4 ColumnAlbedo;" })
                Assert.True(ShaderSources.WaterVert.Contains(member),
                    $"WaterVert lost '{member}': the Water UBO block declaration drifted from WaterFrag's. Fix ShaderSources.WaterVert.");
        }

        [Fact]
        public void WaterClipmapVert_DeclaresSameBlockAsWaterFrag()
        {
            // The clipmap grid runs its own vertex shader against the SAME resource set and the same dynamic-offset
            // buffer, so its block declaration has to match too - a drift here would reinterpret one plane's bytes
            // in one grid mode only, which is exactly the kind of defect that survives a review.
            foreach (var member in new[] { "mat4 ViewProj;", "mat4 InvViewProj;", "vec4 LightDir;", "vec4 LightColor;",
                "vec4 CameraPos;", "vec4 DeepColor;", "vec4 ShallowColor;", "vec4 HorizonColor;", "vec4 WaveParams;",
                "vec4 ShoreGlint;", "vec4 DetailParams;", "vec4 SkyHorizon;", "vec4 SkyZenith;",
                "vec4 SkyParams;", "vec4 ReflectGlint;", "vec4 SwellParams;", "vec4 SwellShape;", "vec4 Absorption;",
                "vec4 FoamColor;", "vec4 FoamParams;", "vec4 RippleSpectrum;", "vec4 FootprintParams;",
                "vec4 FftParams;", "vec4 FftTiles;", "vec4 FftVariance;", "vec4 FftFocus;", "vec4 FftRotCos;",
                "vec4 FftRotSin;", "vec4 FftSector;", "vec4 FftWave;", "vec4 BathyRect;", "vec4 BathyParams;",
                "vec4 SurfParams;", "vec4 SurfShape;", "vec4 RenderOrigin;", "vec4 SkyGround;", "vec4 OpticsRect;", "vec4 OpticsParams;",
                "vec4 SkyDiscColor[8];", "vec4 SkyDiscDir[8];", "vec4 SkyDiscHalo[8];", "vec4 ColumnExtinction;", "vec4 ColumnScattering;", "vec4 ColumnAlbedo;" })
                Assert.True(ShaderSources.WaterClipmapVert.Contains(member),
                    $"WaterClipmapVert lost '{member}': the Water UBO block declaration drifted from WaterFrag's.");
        }

    }
}
