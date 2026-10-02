using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldPersistenceFallbackTests
{
    static PlayerRecord Converted => new() { X = 4f, Z = 7f, Game = new byte[] { 1, 2 } };

    [Fact]
    public async Task PrimaryRecordWins_AndNeverCallsFallback()
    {
        var rig = new WorldPersistenceFallbackRig(_ => throw new InvalidOperationException("unexpected fallback"));
        var primary = new PlayerRecord { X = 2f, Z = 3f, Game = new byte[] { 9 } };
        await rig.Store.Inner.SaveAsync("position:durable", primary.Encode());
        rig.Host.Join();
        await rig.Flush();
        rig.Persistence.SaveDirtyPass();
        await rig.Flush();

        Assert.Equal(new Vector3(2f, 0f, 3f), rig.Host.Position());
        Assert.Equal(new byte[] { 9 }, rig.Game[0]);
        Assert.Empty(rig.Store.Saved);
        Assert.Empty(rig.Errors);
    }

    [Fact]
    public async Task BothRecordsMissing_UsesCapturedIdentityAndKeepsSpawnWritable()
    {
        PersistenceLoadRequest captured = default;
        var rig = new WorldPersistenceFallbackRig(request =>
        {
            captured = request;
            return Task.FromResult<PlayerRecord?>(null);
        });
        rig.Host.Join(3, "verified-account", "resolved-key");
        await rig.Flush();
        rig.Persistence.SaveDirtyPass();
        await rig.Flush();

        Assert.Equal(3, captured.Slot);
        Assert.Equal("verified-account", captured.AuthenticatedAccountId);
        Assert.Equal("resolved-key", captured.PersistenceKey);
        Assert.Equal("position:resolved-key", captured.StoreKey);
        Assert.Equal(Vector3.Zero, rig.Host.Position(3));
        Assert.NotNull(await rig.Store.Inner.LoadAsync("position:resolved-key"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConvertedUnchangedState_FirstLeaveOrPeriodicSaveCreatesPrimary(bool leave)
    {
        var rig = new WorldPersistenceFallbackRig(_ => Task.FromResult<PlayerRecord?>(Converted));
        await rig.Store.Inner.SaveAsync("legacy:durable", new byte[] { 5, 6 });
        rig.Host.Join();
        Assert.Equal(Vector3.Zero, rig.Host.Position());
        Assert.Empty(rig.Game);
        await rig.Flush();

        Assert.Equal(new Vector3(4f, 0f, 7f), rig.Host.Position());
        Assert.Equal(new byte[] { 1, 2 }, rig.Game[0]);
        Assert.True(rig.Persistence.ResumeHints.TryGet("durable", out Vector3 hint));
        Assert.Equal(new Vector3(4f, 0f, 7f), hint);
        Assert.Null(await rig.Store.Inner.LoadAsync("position:durable"));

        if (leave) rig.Host.Leave();
        else rig.Persistence.Update(1f);
        await rig.Flush();

        Assert.Equal(Converted.Encode(), await rig.Store.Inner.LoadAsync("position:durable"));
        Assert.Equal(new byte[] { 5, 6 }, await rig.Store.Inner.LoadAsync("legacy:durable"));
        Assert.Equal(new[] { "position:durable" }, rig.Store.Saved);
    }

    [Fact]
    public async Task FallbackAfterPrimaryDeletion_RemovesThePreviousCleanBaseline()
    {
        var rig = new WorldPersistenceFallbackRig(_ => Task.FromResult<PlayerRecord?>(Converted));
        await rig.Store.Inner.SaveAsync("position:durable", Converted.Encode());
        rig.Host.Join();
        await rig.Flush();
        rig.Host.Leave();
        await rig.Flush();
        await rig.Store.Inner.DeleteAsync("position:durable");
        rig.Host.Join();
        await rig.Flush();
        rig.Host.Leave();
        await rig.Flush();

        Assert.Equal(Converted.Encode(), await rig.Store.Inner.LoadAsync("position:durable"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidFallback_IsQuarantinedWithoutApplyingStateOrBlob(bool rejectBlob)
    {
        PlayerRecord invalid = rejectBlob ? Converted : new PlayerRecord { X = 500f, Game = new byte[] { 1 } };
        var rig = new WorldPersistenceFallbackRig(_ => Task.FromResult<PlayerRecord?>(invalid),
            bounds: new RectBounds(-10f, -10f, 10f, 10f), rejectGame: rejectBlob);
        rig.Host.Join();
        await rig.Flush();

        Assert.Equal(Vector3.Zero, rig.Host.Position());
        Assert.Empty(rig.Game);
        Assert.Equal(new[] { "durable" }, rig.Quarantined);
        Assert.False(rig.Persistence.ResumeHints.TryGet("durable", out _));
        Assert.Null(await rig.Store.Inner.LoadAsync("position:durable"));
        Assert.Equal(invalid.Encode(), await rig.Store.Inner.LoadAsync("quarantine:position:durable"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidPrimary_NeverCallsFallback(bool undecodable)
    {
        var rig = new WorldPersistenceFallbackRig(_ => throw new InvalidOperationException("unexpected fallback"),
            bounds: new RectBounds(-10f, -10f, 10f, 10f));
        byte[] primary = undecodable ? new byte[] { 0xff } : new PlayerRecord { X = 500f }.Encode();
        await rig.Store.Inner.SaveAsync("position:durable", primary);
        rig.Host.Join();
        await rig.Flush();

        Assert.Equal(new[] { "durable" }, rig.Quarantined);
        Assert.Equal(primary, await rig.Store.Inner.LoadAsync("quarantine:position:durable"));
        Assert.Empty(rig.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOrFallbackFailure_SurfacesAndKeepsTheSaveGuard(bool primaryFails)
    {
        int fallbackCalls = 0;
        var rig = new WorldPersistenceFallbackRig(_ =>
        {
            fallbackCalls++;
            return Task.FromException<PlayerRecord?>(new IOException("legacy read failed"));
        });
        rig.Store.FailLoads = primaryFails;
        rig.Host.Join();
        await rig.Flush();
        rig.Persistence.SaveDirtyPass();
        rig.Host.Leave();
        await rig.Flush();

        Assert.Equal(primaryFails ? 0 : 1, fallbackCalls);
        Assert.IsType<IOException>(Assert.Single(rig.Errors));
        Assert.Empty(rig.Store.Saved);
        Assert.Empty(rig.Applied);
    }

    [Fact]
    public async Task ConvertedFirstSaveFailure_RemainsDirtyAndRetries()
    {
        var rig = new WorldPersistenceFallbackRig(_ => Task.FromResult<PlayerRecord?>(Converted));
        rig.Host.Join();
        await rig.Flush();
        rig.Store.FailSaves = true;
        rig.Persistence.SaveDirtyPass();
        await rig.Flush();
        Assert.Single(rig.Errors);
        Assert.Null(await rig.Store.Inner.LoadAsync("position:durable"));

        rig.Store.FailSaves = false;
        rig.Persistence.SaveDirtyPass();
        await rig.Flush();
        Assert.Equal(Converted.Encode(), await rig.Store.Inner.LoadAsync("position:durable"));
    }

    [Fact]
    public async Task Rejoin_WaitsForConvertedSaveBeforeReadingPrimary()
    {
        int calls = 0;
        var rig = new WorldPersistenceFallbackRig(_ =>
        {
            calls++;
            return Task.FromResult<PlayerRecord?>(Converted);
        });
        rig.Host.Join();
        await rig.Flush();
        var gate = new TaskCompletionSource();
        rig.Store.SaveGate = gate.Task;
        rig.Host.Leave();
        rig.Host.Join();
        Assert.Equal(1, calls);
        gate.SetResult();
        await rig.Flush();

        Assert.Equal(1, calls);
        Assert.Equal(new Vector3(4f, 0f, 7f), rig.Host.Position());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededFallback_OnlyCurrentSessionAppliesAndSaves(bool staleFirst)
    {
        var old = new TaskCompletionSource<PlayerRecord?>();
        var current = new TaskCompletionSource<PlayerRecord?>();
        int calls = 0;
        var rig = new WorldPersistenceFallbackRig(_ => ++calls == 1 ? old.Task : current.Task);
        rig.Host.Join();
        rig.Host.Leave();
        rig.Host.Join();
        var newest = new PlayerRecord { X = 8f, Z = 9f, Game = new byte[] { 3 } };
        if (staleFirst) { old.SetResult(Converted); current.SetResult(newest); }
        else { current.SetResult(newest); old.SetResult(Converted); }
        await rig.Flush();
        rig.Persistence.SaveDirtyPass();
        await rig.Flush();

        Assert.Equal(new Vector3(8f, 0f, 9f), rig.Host.Position());
        Assert.Equal(new[] { "durable" }, rig.Dropped);
        Assert.Equal(new[] { "durable" }, rig.Applied);
        Assert.Equal(newest.Encode(), await rig.Store.Inner.LoadAsync("position:durable"));
    }

    [Fact]
    public async Task RecycledSlot_DropsOldFallbackWithoutChangingTheNewOccupant()
    {
        var gate = new TaskCompletionSource<PlayerRecord?>();
        var rig = new WorldPersistenceFallbackRig(request => request.AuthenticatedAccountId == "account"
            ? gate.Task : Task.FromResult<PlayerRecord?>(null));
        rig.Host.Join();
        rig.Host.Leave();
        rig.Host.Join(0, "other-account", "other-key");
        gate.SetResult(Converted);
        await rig.Flush();
        rig.Persistence.SaveDirtyPass();
        await rig.Flush();

        Assert.Equal(Vector3.Zero, rig.Host.Position());
        Assert.Empty(rig.Game);
        Assert.Equal(new[] { "durable" }, rig.Dropped);
        Assert.Null(await rig.Store.Inner.LoadAsync("position:durable"));
        Assert.NotNull(await rig.Store.Inner.LoadAsync("position:other-key"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuestJoin_NeverCallsFallback(bool persistGuests)
    {
        var rig = new WorldPersistenceFallbackRig(_ => throw new InvalidOperationException("unexpected fallback"), persistGuests);
        rig.Host.Join(0, "guest:0", "guest:0");
        await rig.Flush();
        rig.Host.Leave();
        await rig.Flush();

        Assert.Empty(rig.Errors);
        Assert.Empty(rig.Applied);
        Assert.Null(await rig.Store.Inner.LoadAsync("position:guest:0"));
    }
}
