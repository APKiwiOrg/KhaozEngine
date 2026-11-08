using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Two independently uploaded, textured actors in distinct immutable table groups.</summary>
internal sealed class CoverageMultiActorFixture : IDisposable
{
    internal readonly CoveragePublicationFixture Root;
    internal readonly RecordingGpuCommandList Commands = new(new NullGpuCommandList())
    { CapturePayloads = true, CaptureBindings = true, CaptureInstancedBindings = true, CaptureComputeBindings = true };
    internal readonly Actor First, Second;
    internal Scene3D Scene => Root.Scene;

    internal sealed class Actor(HostCoverageBinding binding, MotionKey key, SkinnedMeshHandle[] meshes,
        CoverageRecessionTable table, IGpuTexture[] textures, int identity)
    {
        internal readonly HostCoverageBinding Binding = binding;
        internal readonly MotionKey Key = key;
        internal readonly SkinnedMeshHandle[] Meshes = meshes;
        internal readonly CoverageRecessionTable Table = table;
        internal readonly int Identity = identity;
        internal readonly IGpuTexture[] Textures = textures;
        internal Matrix4x4 Model = Matrix4x4.CreateTranslation(identity == 0 ? -1 : 1, 0, 0);
        internal Color Tint(int slice) => new(.2f + Identity * .3f, .1f + slice * .2f, .7f - Identity * .2f, 1);
        internal Material Material(int slice) => new(new Color(.02f + Identity * .03f, .04f + slice * .02f, .01f),
            .1f + Identity * .2f + slice * .05f, 12 + Identity * 13 + slice * 3);
    }

    internal CoverageMultiActorFixture(int participants = 8)
    {
        Root = new(participants: participants);
        Root.Binding.Dispose();
        First = Create(Root.Host.Table, 0, Root.Host.Actor);
        var host = Root.Host;
        var scalars = new float[host.Table.PositionGroupCount * 2];
        for (int i = 0; i < scalars.Length; i++)
            if (host.Table.Representatives[i % host.Table.PositionGroupCount] >= 0) scalars[i] = .02f;
        var other = CoverageRecessionTable.Create(host.Table.Identity, host.Table.Representatives, scalars,
            [new(new(10), 0), new(new(20), 1), new(new(30), -1)]);
        Second = Create(other, 1, MotionKey.From(902));
        Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
    }

    Actor Create(CoverageRecessionTable table, int identity, MotionKey key)
    {
        var meshes = new SkinnedMeshHandle[Root.Host.Meshes.Length];
        var textures = new IGpuTexture[meshes.Length];
        for (int slice = 0; slice < meshes.Length; slice++)
        {
            var texture = Scene.LoadTexture([(byte)(40 + identity * 60), (byte)(60 + slice * 50), 120, 255], 1, 1);
            textures[slice] = Root.Device.Resources.Textures.Last();
            meshes[slice] = Scene.LoadSkinnedMesh(Root.Host.Meshes[slice], texture);
        }
        var binding = new HostCoverageBinding(Root.Host.Adapter, Root.Host.Skeleton, meshes, key, table);
        Assert.True(binding.TrySetEquipment([Root.Host.Equipment((ulong)(17 + identity))], out _));
        return new(binding, key, meshes, table, textures, identity);
    }

    internal void Begin() { Root.Begin(); Commands.Clear(); }
    internal void Queue(Actor actor, bool shadowOnly = false, bool outlines = true)
    {
        var host = Root.Host;
        Assert.True(actor.Binding.TryPrepare(host.Skeleton, host.Palette, actor.Model, new(10),
            CoverageBindingFixture.Template, out var frame, out _));
        foreach (var equipment in actor.Binding.PreparedEquipmentDraws.Span) Scene.Draw(equipment.Rigid);
        var group = outlines ? Scene.BeginMeshOutline(Color.White, 2) : default;
        for (int slice = 0; slice < actor.Meshes.Length; slice++)
        {
            var draw = new SkinnedInstanceDraw(actor.Meshes[slice], actor.Model)
            { Motion = actor.Key, Coverage = frame, ShadowOnly = shadowOnly, Tint = actor.Tint(slice), Material = actor.Material(slice) };
            Scene.DrawSkinned(draw, host.Palette);
            if (outlines) Scene.DrawSkinnedOutline(group, draw, host.Palette);
        }
    }
    internal void Record()
    {
        Scene.PrepareFrame(128, 96);
        Commands.Begin(); Scene.RenderInternal(Commands, 128, 96, Root.Target); Commands.End();
    }
    internal void Submit() => Root.Submission.Submit(Commands);
    public void Dispose()
    { Root.Submission.Abandon(); First.Binding.Dispose(); Second.Binding.Dispose(); Commands.Dispose(); Root.Dispose(); }
}
