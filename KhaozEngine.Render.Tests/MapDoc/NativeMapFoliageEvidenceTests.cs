using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapFoliageEvidenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteExpansion_RejectsSelectivePartOrPatchLoss(bool omitPart)
    {
        using var fixture = ResidencyFixture.Foliage(); using var host = new NativeUploadTestScene();
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(fixture.FoliageState(), new MapScene3DSink(host.Scene), null, scheduler);
        scheduler.RunAll(); using var view = new NativeMapView(host.Scene, residency); Assert.Equal(0, view.SettleForCapture(1024).Pending);
        var registration = Assert.Single(ResidencyFixture.Draw(residency).Resources, r => r.Payload is MapFoliageResource);
        var source = (MapFoliageResource)registration.Payload;
        Assert.True(residency.TryGetHandle(registration.Reference, out var handle));
        var product = host.Scene.ResourceOf(new(handle.Value)).PreparedFoliage!;
        MeshHandle Resolve(MapResourceReference reference)
        {
            Assert.True(residency.TryGetHandle(reference, out var part));
            return new SceneResourceAccess(host.Scene).MeshOf(new(part.Value));
        }
        NativeMapFoliageEvidence.AssertComplete(source, product.ExpandedParts, product.Patches, product.PackedInstances, Resolve);
        var omitted = NativeMapFoliageEvidence.Omit(product, omitPart);
        Assert.NotEmpty(omitted.Parts); Assert.NotEmpty(omitted.Patches);
        Assert.ThrowsAny<XunitException>(() => NativeMapFoliageEvidence.AssertComplete(source, omitted.Parts, omitted.Patches, omitted.Bytes, Resolve));
    }
}

internal sealed record NativeMapFoliageProduct(IReadOnlyList<SceneFoliageExpandedPart> Parts, IReadOnlyList<SceneFoliagePatch> Patches,
    ReadOnlyMemory<byte> Bytes);

internal static class NativeMapFoliageEvidence
{
    internal static (int Instances, int Patches) AssertComplete(MapFoliageResource source,
        IReadOnlyList<SceneFoliageExpandedPart> parts, IReadOnlyList<SceneFoliagePatch> patches, ReadOnlyMemory<byte> packed,
        Func<MapResourceReference, MeshHandle> resolve)
    {
        var expected = new List<SceneFoliageExpandedPart>();
        var byPatch = new Dictionary<PatchKey, List<SceneFoliageExpandedPart>>();
        int[] models = source.ModelIndices.ToArray();
        var transforms = source.TransformSpace == MapFoliageTransformSpace.World ? source.WorldTransforms.ToArray() : source.AnchorLocalTransforms.ToArray();
        for (int row = 0; row < models.Length; row++)
        {
            var binding = source.Models[models[row]]; var transform = transforms[row];
            double x = transform.M41, z = transform.M43;
            if (source.TransformSpace == MapFoliageTransformSpace.AnchorLocal) { x += source.Anchor.X; z += source.Anchor.Z; }
            for (int part = 0; part < binding.Parts.Count; part++)
            {
                var mesh = resolve(binding.Parts[part]);
                var identity = new SceneFoliageExpandedPart(row, models[row], part, mesh); expected.Add(identity);
                var key = new PatchKey(Math.Floor(x / 8), Math.Floor(z / 8), mesh, binding.CastShadows);
                if (!byPatch.TryGetValue(key, out var rows)) byPatch.Add(key, rows = []);
                rows.Add(identity);
            }
        }
        Assert.Equal(Ordered(expected), Ordered(parts));
        Assert.Equal(checked(expected.Count * 96), packed.Length);
        Assert.Equal(byPatch.Count, patches.Count);
        var covered = new bool[parts.Count];
        foreach (var (key, rows) in byPatch)
        {
            var matching = patches.Where(p => p.CellX == key.X && p.CellZ == key.Z && p.Mesh.Equals(key.Mesh)).ToArray();
            Assert.NotEmpty(matching);
            var patch = Assert.Single(matching, p => p.CastShadows == key.CastShadows);
            Assert.Equal(key.CastShadows, patch.CastShadows);
            Assert.Equal(rows.Count, patch.Count);
            Assert.InRange(patch.Start, 0, parts.Count - patch.Count);
            Assert.Equal(Ordered(rows), Ordered(parts.Skip(patch.Start).Take(patch.Count)));
            for (int i = patch.Start; i < patch.Start + patch.Count; i++)
            { Assert.False(covered[i], "A prepared part appears in multiple patches."); covered[i] = true; }
        }
        Assert.All(covered, value => Assert.True(value));
        var bytes = packed.Span;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            Assert.Equal(source.ModelIndices.Span[part.SourceRow], part.ModelIndex);
            var model = source.Models[part.ModelIndex];
            Assert.Equal(resolve(model.Parts[part.PartIndex]), part.Mesh);
            Assert.Equal(BitConverter.SingleToInt32Bits(source.Ranks.Span[part.SourceRow]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(i * 96 + 64)..]));
            var motion = model.Motion.ToVector4();
            Assert.Equal(motion.X, BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 96 + 80)..]));
            Assert.Equal(motion.Y, BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 96 + 84)..]));
            Assert.Equal(motion.Z, BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 96 + 88)..]));
            Assert.Equal(motion.W, BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 96 + 92)..]));
        }
        return (expected.Count, byPatch.Count);
    }

    readonly record struct PatchKey(double X, double Z, MeshHandle Mesh, bool CastShadows);
    static (int Row, int Model, int Part)[] Ordered(IEnumerable<SceneFoliageExpandedPart> rows) => rows
        .Select(p => (p.SourceRow, p.ModelIndex, p.PartIndex)).OrderBy(p => p.SourceRow).ThenBy(p => p.ModelIndex).ThenBy(p => p.PartIndex).ToArray();

    internal static NativeMapFoliageProduct Omit(ScenePreparedFoliage prepared, bool part)
    {
        if (!part) return new(prepared.ExpandedParts, prepared.Patches.Skip(1).ToArray(), prepared.PackedInstances);
        var target = prepared.ExpandedParts.First(p => p.PartIndex == 1);
        int[] retained = Enumerable.Range(0, prepared.ExpandedParts.Count)
            .Where(i => prepared.ExpandedParts[i].ModelIndex != target.ModelIndex || prepared.ExpandedParts[i].PartIndex != target.PartIndex).ToArray();
        var rows = retained.Select(i => prepared.ExpandedParts[i]).ToArray();
        var bytes = new byte[retained.Length * 96]; var original = prepared.PackedInstances.Span;
        for (int i = 0; i < retained.Length; i++) original.Slice(retained[i] * 96, 96).CopyTo(bytes.AsSpan(i * 96));
        var patches = new List<SceneFoliagePatch>();
        foreach (var patch in prepared.Patches)
        {
            int start = retained.Count(i => i < patch.Start);
            int count = retained.Count(i => i >= patch.Start && i < patch.Start + patch.Count);
            if (count > 0) patches.Add(patch with { Start = start, Count = count });
        }
        return new(rows, patches, bytes);
    }
}
