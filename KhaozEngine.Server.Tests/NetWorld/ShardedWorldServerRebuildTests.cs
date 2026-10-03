namespace KhaozEngine.Tests.NetWorld;

/// <summary>Format 2 offer, acceptance and selection on <see cref="KhaozEngine.NetWorld.ShardedWorldServer"/>.</summary>
public sealed class ShardedWorldServerRebuildTests : RebuildHostCases
{
    protected override RebuildHostKind Kind => RebuildHostKind.Sharded;
}
