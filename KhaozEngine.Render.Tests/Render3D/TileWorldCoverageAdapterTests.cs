using System.Linq;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TileWorldCoverageAdapterTests
{
    // #1765. A cache and binding over the tile-world adapter must share the actual scene owner.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wrapper_accepts_cached_equipment_only_from_its_own_scene(bool anotherWrapper)
    {
        using var f = new CoverageBindingFixture();
        using var other = new CoverageBindingFixture();
        var wrapper = new Scene3DTileWorldScene(f.Scene);
        var capability = Assert.IsAssignableFrom<IAttachmentCoverageScene>(wrapper);
        Assert.Same(f.Scene, capability.CoverageResourceOwner);
        using var cache = new AttachmentSetCache(wrapper, f.Source);
        var loaded = cache.Get("equipment.json", f.Loaded.DefinitionSha256);
        var coverage = cache.GetCoverage(loaded, "coverage.json", f.Coverage.CompanionSha256);
        var bound = loaded.Bind(f.Map, f.Meshes[0]);
        using var binding = new HostCoverageBinding(anotherWrapper ? new Scene3DTileWorldScene(f.Scene) : wrapper,
            f.Skeleton, f.Hosts, f.Actor, f.Table);
        Assert.True(binding.TrySetEquipment([new(17, bound, coverage)], out _));
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out _, out var failure));
        Assert.True(failure.IsSuccess);
        Assert.NotEmpty(binding.PreparedEquipmentDraws.ToArray());
        var foreignHosts = f.Meshes.Select(other.Scene.LoadSkinnedMesh).ToArray();
        using var foreign = new HostCoverageBinding(new Scene3DTileWorldScene(other.Scene),
            f.Skeleton, foreignHosts, other.Actor, f.Table);
        Assert.False(foreign.TrySetEquipment([new(17, bound, coverage)], out var refused));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, refused.Code);
    }
}
