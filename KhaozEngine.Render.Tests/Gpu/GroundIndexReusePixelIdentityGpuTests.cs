using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using KhaozEngine.Tests.TileWorld;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GroundIndexReusePixelIdentityGpuTests
{
    [GpuTheory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public void IndexReusePreservesEveryAttachmentHistoryAndShadowByte(bool smooth, bool feather, bool steep)
    {
        using var context = GpuDeviceContext.CreateHeadless();
        var oracle = new GroundUploadOracle();
        using var baseline = new ShaderOverrideGpuDevice(context.GpuDevice, source => source,
            bufferRewrite: oracle.Resize, uploadRewrite: oracle.Restore);
        var meshes = Geometry(smooth, feather, steep);
        Assert.NotEmpty(meshes);
        foreach (var item in meshes)
        {
            Vector3[] outlines = OutlineNormalBuilder.Build(item.Mesh.Vertices, item.Mesh.Indices32);
            GroundUploadStreams compact = ExactGroundMeshReuse.Compact(item.Mesh.Vertices, outlines, item.Mesh.Indices32);
            ExactGroundMeshReuseTests.AssertExpanded(item.Mesh.Vertices, outlines, item.Mesh.Indices32, compact);
            Assert.Equal(MeshBounds.FromVertices(item.Mesh.Vertices), MeshBounds.FromVertices(compact.Vertices));
            Assert.Equal(item.Mesh.Indices32.Length, compact.Indices.Length);
        }
        byte[][] expected = ShaderCutPixelIdentityGpuTests.Render(baseline, upload: oracle.Upload, geometry: meshes);
        byte[][] actual = ShaderCutPixelIdentityGpuTests.Render(context.GpuDevice, geometry: meshes);
        byte[][] control = ShaderCutPixelIdentityGpuTests.Render(context.GpuDevice, changeMaterial: true, geometry: meshes);
        Assert.True(oracle.SavedVertices > 0, "The oracle must undo real vertex reuse at the upload boundary.");
        Assert.Equal(30, expected.Length);
        GBufferByteIdentity.AssertAll(expected, actual);
        Assert.False(actual[0].SequenceEqual(control[0]), "The material control must change actual HDR pixels.");
    }

    static (GltfMesh Mesh, Matrix4x4 Transform)[] Geometry(bool smooth, bool feather, bool steep)
    {
        var document = new TileWorldDocument { Id = "upload-reuse", DisplayName = "Upload reuse" };
        for (int z = 61; z <= 67; z++)
            for (int x = 61; x <= 67; x++)
            {
                document.GetOrCreateRegion(RegionCoord.Of(x, z));
                document.SetCornerHeightCm(x, z, 0,
                    (short)(steep ? (x - 64) * 220 + (z - 64) * 80 : (x * 31 + z * 17) % 9 * 10));
                if (x == 67 || z == 67) continue;
                document.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
                if ((x + z) % 3 != 0) continue;
                document.SetOverlay(x, z, 0, TileRenderTestData.Road);
                document.SetOverlayShape(x, z, 0, (TileOverlayShape)((x + z) % 4));
                document.SetOverlayRotation(x, z, 0, x % 4);
                if (feather) document.SetSettings(x, z, 0, TileSettings.FeatherOverlay);
            }
        var options = new TileGroundMesherOptions
        {
            SmoothNormals = smooth,
            Slots = TileGroundMaterials.Build(TileRenderTestData.Catalogs),
        };
        var result = new List<(GltfMesh, Matrix4x4)>();
        foreach (RegionCoord region in document.Regions.Keys.OrderBy(r => r.Rz).ThenBy(r => r.Rx))
        {
            GltfMesh? mesh = TileGroundMesher.Build(document, TileRenderTestData.Catalogs, region, 0, options);
            if (mesh is not null)
                result.Add((mesh, TileGroundMesher.WorldMatrix(document, region) * Matrix4x4.CreateTranslation(-64, 0, 64)));
        }
        return result.ToArray();
    }
}
