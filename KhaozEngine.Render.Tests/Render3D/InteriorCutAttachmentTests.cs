using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class InteriorCutAttachmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_submission_cuts_only_rigid_parts(bool gpuSkinning)
    {
        using var fixture = new MixedAttachmentSubmissionFixture();
        Scene3D scene = fixture.Scene;
        scene.UseGpuSkinning = gpuSkinning;
        scene.FrustumCulling = false;
        scene.Post.Starfield = false;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        var template = new RigidInstanceDraw(default, Matrix4x4.Identity) { CutInsideInteriors = true };
        var draws = new AttachmentDraw[3];
        Assert.Equal(3, fixture.Bound.WriteDraws(fixture.Skeleton, fixture.Palette, fixture.Model,
            fixture.Key, template, draws));
        Assert.True(draws[0].Rigid.CutInsideInteriors);

        scene.Begin();
        fixture.Bound.Queue(fixture.Skeleton, fixture.Palette, fixture.Model, fixture.Key, template);
        Assert.True(fixture.Capability.RigidDraw.CutInsideInteriors);
        Assert.True(Assert.Single(scene.QueuedInstancesForTests).CutInsideInteriors);
        Assert.Equal(2, scene.QueuedSkinnedInstancesForTests.Count);
        using IGpuTexture texture = fixture.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            32, 24, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using IGpuFramebuffer target = fixture.Factory.CreateFramebuffer(null, texture);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList())
        { CapturePayloads = true, CaptureBindings = true };
        commands.Begin();
        scene.PrepareFrame();
        scene.RenderInternal(commands, 32, 24, target);
        commands.End();
        Assert.Equal(2, scene.DrawnSkinnedInstances);

        RecordingGpuCommandList.DrawBindings[] main = commands.Bindings.Where(draw =>
            Assert.IsType<FakePipeline>(draw.Pipeline).Request!.Value.FragmentGlsl
                == (gpuSkinning ? ShaderSources.SkinnedModelFrag : ShaderSources.ModelFrag)).ToArray();
        if (gpuSkinning)
        {
            Assert.Equal(2, main.Length);
            foreach (RecordingGpuCommandList.DrawBindings draw in main)
            {
                var (set, offset) = draw.Sets[0];
                GpuBufferRange range = Assert.Single(Assert.IsType<FakeResourceSet>(set).Resources.OfType<GpuBufferRange>());
                byte[] bytes = Assert.Single(commands.Uploads, upload => ReferenceEquals(upload.Buffer, range.Buffer)).Data!;
                Assert.Equal(0f, MemoryMarshal.Read<Vector4>(bytes.AsSpan(checked((int)(range.Offset + offset + 96)))).W);
            }
        }
        else
        {
            Assert.Equal(3, main.Length);
            byte[] rigid = Assert.Single(commands.Uploads, upload => ReferenceEquals(upload.Buffer, main[0].VertexBuffers[1])).Data!;
            Assert.Equal(1f, MemoryMarshal.Read<ModelRenderer.InstanceData>(rigid).SpecParams.W);
            byte[] skinned = Assert.Single(commands.Uploads, upload => ReferenceEquals(upload.Buffer, main[1].VertexBuffers[1])).Data!;
            Assert.Equal(2 * ModelRenderer.InstanceData.SizeInBytes, (uint)skinned.Length);
            Assert.Equal(0f, MemoryMarshal.Read<ModelRenderer.InstanceData>(skinned).SpecParams.W);
            Assert.Equal(0f, MemoryMarshal.Read<ModelRenderer.InstanceData>(skinned.AsSpan((int)ModelRenderer.InstanceData.SizeInBytes)).SpecParams.W);
        }
    }
}
