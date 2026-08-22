using System;
using System.Text;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// Pins the stored shape of a tile player record: what round-trips, what an old or a newer save decodes to, and
/// that the bytes are canonical on every operating system. These are the guarantees a live account's save depends
/// on, so a change that breaks one of them is a change that loses progress.
/// </summary>
public class TilePlayerRecordTests
{
    [Fact]
    public void A_record_round_trips_tile_plane_facing_and_the_opaque_blob()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(-12, 340, 2), TileDirection.SW);
        byte[] game = Encoding.UTF8.GetBytes("{\"xp\":7}");
        TilePlayerRecord back = TilePlayerRecord.Decode(TilePlayerRecord.From(s, game).Encode());
        Assert.Equal(1, back.Version);
        Assert.Equal(-12, back.TileX);
        Assert.Equal(340, back.TileZ);
        Assert.Equal(2, back.Plane);
        Assert.Equal((byte)TileDirection.SW, back.Facing);
        Assert.Equal(game, back.Game);
        Assert.Equal(s.Tile, back.ToState().Tile);
        Assert.Equal(s.Facing, back.ToState().Facing);
        Assert.True(back.ToState().Route.IsIdle);
    }

    [Fact]
    public void An_empty_blob_is_stored_as_null_and_an_unknown_member_is_ignored()
    {
        Assert.Null(TilePlayerRecord.From(TileMoveState.At(default, TileDirection.N), Array.Empty<byte>()).Game);
        byte[] forward = Encoding.UTF8.GetBytes("{\"version\":1,\"tileX\":4,\"tileZ\":5,\"plane\":0,\"facing\":1,\"unknown\":true}");
        TilePlayerRecord r = TilePlayerRecord.Decode(forward);
        Assert.Equal(4, r.TileX);
        Assert.Equal(5, r.TileZ);
    }

    [Fact]
    public void The_encoding_is_canonical_lf_indented_json()
    {
        string json = Encoding.UTF8.GetString(TilePlayerRecord.From(TileMoveState.At(default, TileDirection.N)).Encode());
        Assert.Contains("\n", json);
        Assert.DoesNotContain("\r", json);
    }
}
