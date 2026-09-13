using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The opt-in settled-stack policy. Movement is a presentation fact here: moving bodies stay wholly visible and
/// claim no tile, then the stack chooses one body only after they settle.
/// </summary>
public class TileDrawPrioritySettledStacksTests
{
    const float Frame = 1f / 60f;
    static readonly TileCoord Away = new(0, 0, 0);

    static (long NetId, TileCoord Tile, float StepProgress)[] Actors(
        params (long NetId, TileCoord Tile, float StepProgress)[] actors) => actors;

    static TileDrawPriority Priority(Comparison<long>? comparison = null) => new()
    {
        Policy = TileDrawPriorityPolicy.SettledStacksOnly,
        SettledComparison = comparison,
    };

    [Fact]
    public void The_existing_one_body_policy_remains_the_default_and_keeps_its_step_fade()
    {
        var tile = new TileCoord(12, 10, 0);
        var priority = new TileDrawPriority();

        Assert.Equal(TileDrawPriorityPolicy.OneBodyPerTile, priority.Policy);

        priority.Rebuild(4, Away, null,
            Actors((7, new TileCoord(11, 10, 0), 1f), (31, tile, 1f)), Frame);
        priority.Rebuild(4, Away, null, Actors((7, tile, 0.5f), (31, tile, 1f)), Frame);

        Assert.InRange(priority.Weight(7), 0.49f, 0.51f);
        Assert.Equal(1f, priority.Weight(31));
    }

    [Fact]
    public void Moving_bodies_share_a_committed_tile_at_full_weight_while_only_the_settled_body_owns_it()
    {
        var tile = new TileCoord(12, 10, 0);
        var priority = Priority();

        priority.Rebuild(4, Away, localMoving: false,
            Actors((7, tile, 0.4f), (31, tile, 1f)), Frame);

        Assert.Equal(1f, priority.Weight(7));
        Assert.Equal(1f, priority.Weight(31));
        Assert.True(priority.TryGetDrawn(tile, out long owner));
        Assert.Equal(31, owner);
    }

    [Fact]
    public void A_moving_arrival_is_hidden_only_after_it_settles_under_the_existing_winner()
    {
        var tile = new TileCoord(12, 10, 0);
        var priority = Priority();

        foreach (float progress in new[] { 0f, 0.25f, 0.75f })
        {
            priority.Rebuild(4, Away, localMoving: false,
                Actors((7, tile, progress), (31, tile, 1f)), Frame);
            Assert.Equal(1f, priority.Weight(7));
        }

        priority.Rebuild(4, Away, localMoving: false,
            Actors((7, tile, 1f), (31, tile, 1f)), Frame);

        Assert.Equal(0f, priority.Weight(7));
        Assert.Equal(1f, priority.Weight(31));
    }

    [Fact]
    public void A_hidden_body_becomes_fully_visible_on_the_zero_progress_frame_of_its_departure()
    {
        var shared = new TileCoord(12, 10, 0);
        var destination = new TileCoord(13, 10, 0);
        var priority = Priority();

        priority.Rebuild(4, Away, localMoving: false,
            Actors((7, shared, 1f), (31, shared, 1f)), Frame);
        Assert.Equal(0f, priority.Weight(7));

        priority.Rebuild(4, Away, localMoving: false,
            Actors((7, destination, 0f), (31, shared, 1f)), Frame);

        Assert.Equal(1f, priority.Weight(7));
        Assert.Equal(1f, priority.Weight(31));
        Assert.False(priority.TryGetDrawn(destination, out _));
        Assert.True(priority.TryGetDrawn(shared, out long owner));
        Assert.Equal(31, owner);
    }

    [Fact]
    public void A_chained_step_stays_visible_when_progress_returns_to_zero_on_the_next_tile()
    {
        var first = new TileCoord(12, 10, 0);
        var second = new TileCoord(13, 10, 0);
        var priority = Priority();

        priority.Rebuild(4, Away, localMoving: false,
            Actors((7, first, 0.75f), (31, first, 1f)), Frame);
        Assert.Equal(1f, priority.Weight(7));

        priority.Rebuild(4, Away, localMoving: false,
            Actors((7, second, 0f), (31, second, 1f)), Frame);

        Assert.Equal(1f, priority.Weight(7));
        Assert.Equal(1f, priority.Weight(31));
    }

    [Fact]
    public void The_comparison_chooses_the_settled_winner_and_equal_scores_fall_back_to_net_id()
    {
        var tile = new TileCoord(12, 10, 0);
        var ranks = new Dictionary<long, int> { [7] = 50, [12] = 100, [31] = 100 };
        var priority = Priority((first, second) => ranks[first].CompareTo(ranks[second]));

        priority.Rebuild(4, Away, localMoving: false,
            Actors((31, tile, 1f), (7, tile, 1f), (12, tile, 1f)), Frame);

        Assert.Equal(0f, priority.Weight(7));
        Assert.Equal(0f, priority.Weight(12));
        Assert.Equal(1f, priority.Weight(31));
        Assert.True(priority.TryGetDrawn(tile, out long owner));
        Assert.Equal(31, owner);
    }

    [Fact]
    public void A_moving_local_body_is_visible_without_claiming_a_stack_then_wins_when_settled()
    {
        var tile = new TileCoord(12, 10, 0);
        var priority = Priority((first, second) => first == 99 ? 1 : second == 99 ? -1 : 0);

        priority.Rebuild(localNetId: 4, tile, localMoving: true, Actors((99, tile, 1f)), Frame);

        Assert.Equal(1f, priority.Weight(4));
        Assert.Equal(1f, priority.Weight(99));
        Assert.True(priority.TryGetDrawn(tile, out long movingOwner));
        Assert.Equal(99, movingOwner);

        priority.Rebuild(localNetId: 4, tile, localMoving: false, Actors((99, tile, 1f)), Frame);

        Assert.Equal(1f, priority.Weight(4));
        Assert.Equal(0f, priority.Weight(99));
        Assert.True(priority.TryGetDrawn(tile, out long settledOwner));
        Assert.Equal(4, settledOwner);
    }

    [Fact]
    public void The_settled_policy_uses_only_binary_weights_even_when_a_fade_window_is_configured()
    {
        var tile = new TileCoord(12, 10, 0);
        var priority = Priority();
        priority.FadeSeconds = 10f;

        priority.Rebuild(4, Away, localMoving: false,
            Actors((7, tile, 1f), (31, new TileCoord(13, 10, 0), 1f)), Frame);
        Assert.Equal(1f, priority.Weight(7));

        priority.Rebuild(4, Away, localMoving: false,
            Actors((7, tile, 1f), (31, tile, 1f)), dt: 0.0001f);
        Assert.Equal(0f, priority.Weight(7));

        priority.Rebuild(4, Away, localMoving: false,
            Actors((7, new TileCoord(11, 10, 0), 0f), (31, tile, 1f)), dt: 0f);
        Assert.Equal(1f, priority.Weight(7));
    }

    [Fact]
    public void The_live_client_keeps_a_delayed_remote_step_wholly_visible_until_its_arrival_sample_settles()
    {
        using var loop = new TileRemoteReadTests.Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        TileCoord mine = loop.Client.Prediction.PredictedState.Tile;
        var from = new TileCoord(mine.X + 1, mine.Z, mine.Plane);
        loop.Server.SetPlayerState(1, TileMoveState.At(from, TileDirection.W));
        loop.Frames(24);

        var priority = Priority();
        TileMoveState stepping = TileMoveState.At(mine, TileDirection.W);
        stepping.StepFrom = from;
        stepping.StepTicks = 0;
        stepping.StepTotal = 4;
        loop.Server.SetPlayerState(1, stepping);

        bool sawMovingSample = false;
        bool sawSettledSample = false;
        for (int i = 0; i < 120; i++)
        {
            loop.Step();
            priority.Rebuild(loop.Client, Frame);
            if (!loop.Client.TryGetRemoteTile(remote, out TileCoord tile) || !tile.Equals(mine)) continue;
            Assert.True(loop.Client.TryGetRemoteStepProgress(remote, out float progress));
            if (progress < 1f)
            {
                sawMovingSample = true;
                Assert.Equal(1f, priority.Weight(remote));
            }
            else if (priority.Weight(remote) == 0f)
            {
                sawSettledSample = true;
                break;
            }
        }

        Assert.True(sawMovingSample, "the delayed presentation timeline never exposed the step in flight");
        Assert.True(sawSettledSample, "the remote never joined the settled stack after landing");
    }

    [Fact]
    public void The_live_local_presentation_claims_neither_end_of_its_step_then_claims_its_arrival_tile()
    {
        using var loop = new TileRemoteReadTests.Loop();
        loop.Join();
        TileCoord origin = loop.Client.Prediction.PredictedState.Tile;
        var destination = new TileCoord(origin.X, origin.Z + 1, origin.Plane);
        long onOrigin = loop.Server.SpawnPlayer(slot: 1, "origin", "Origin");
        long onDestination = loop.Server.SpawnPlayer(slot: 2, "destination", "Destination");
        loop.Server.SetPlayerState(1, TileMoveState.At(origin, TileDirection.S));
        loop.Server.SetPlayerState(2, TileMoveState.At(destination, TileDirection.S));
        loop.Frames(24);

        var priority = Priority();
        loop.Client.Queue(TileCommand.WalkTo(destination, TileMoveMode.Walk));
        bool sawMoving = false;
        bool sawSettled = false;
        for (int i = 0; i < 180; i++)
        {
            loop.Step();
            priority.Rebuild(loop.Client, Frame);
            TileMoveState predicted = loop.Client.Prediction.PredictedState;
            TileMoveState rendered = loop.Client.Prediction.RenderedState;
            Vector2 position = rendered.HasRenderOverride ? rendered.RenderPosition : rendered.Position;
            bool moving = predicted.IsStepping || position != new Vector2(predicted.Tile.X, predicted.Tile.Z);
            if (moving)
            {
                sawMoving = true;
                Assert.Equal(1f, priority.Weight(loop.Client.LocalNetId));
                Assert.Equal(1f, priority.Weight(onOrigin));
                Assert.Equal(1f, priority.Weight(onDestination));
                Assert.True(priority.TryGetDrawn(origin, out long originOwner));
                Assert.Equal(onOrigin, originOwner);
                Assert.True(priority.TryGetDrawn(destination, out long destinationOwner));
                Assert.Equal(onDestination, destinationOwner);
            }
            else if (sawMoving && predicted.Tile.Equals(destination))
            {
                sawSettled = true;
                Assert.Equal(0f, priority.Weight(onDestination));
                Assert.True(priority.TryGetDrawn(destination, out long owner));
                Assert.Equal(loop.Client.LocalNetId, owner);
                break;
            }
        }

        Assert.True(sawMoving, "the local presentation never exposed the step in flight");
        Assert.True(sawSettled, "the local presentation never settled on its arrival tile");
    }

    [Fact]
    public void Equal_local_interpolation_endpoints_do_not_drop_the_settled_tile_claim_on_a_rounding_frame()
    {
        using var loop = new TileRemoteReadTests.Loop();
        loop.Join();
        var destination = new TileCoord(13, 10, 0);
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        loop.Server.SetPlayerState(1, TileMoveState.At(destination, TileDirection.S));
        loop.Frames(24);

        loop.Client.Queue(TileCommand.WalkTo(destination, TileMoveMode.Run));
        for (int i = 0; i < 300; i++)
        {
            loop.Step();
            TileMoveState state = loop.Client.Prediction.PredictedState;
            TileMoveState rendered = loop.Client.Prediction.RenderedState;
            if (state.Tile == destination && !state.IsStepping
                && rendered.RenderPosition == new Vector2(destination.X, destination.Z)) break;
        }

        TileMoveState settled = loop.Client.Prediction.PredictedState;
        Assert.Equal(destination, settled.Tile);
        Assert.False(settled.IsStepping);

        var roundingFramePriority = Priority();
        bool sawRoundingNoise = false;
        for (int i = 0; i < 32; i++)
        {
            // Continue on a standing tick resets the interpolation between two identical tile-centre endpoints.
            // 0.30786 makes Vector2.Lerp(13, 13, fraction) miss 13 by one ULP on .NET 10, while 0.3 is exact.
            // Alternating them reproduces the ownership flicker without any body moving.
            float fraction = i % 2 == 0 ? 0.30786f : 0.3f;
            loop.Client.Tick(1f / 6f);
            loop.Client.AdvancePresentation((1f / 6f) * fraction);
            TileMoveState roundingFrame = loop.Client.Prediction.RenderedState;
            sawRoundingNoise |= roundingFrame.RenderPosition != new Vector2(destination.X, destination.Z);

            roundingFramePriority.Rebuild(loop.Client, Frame);

            Assert.Equal(1f, roundingFramePriority.Weight(loop.Client.LocalNetId));
            Assert.Equal(0f, roundingFramePriority.Weight(remote));
            Assert.True(roundingFramePriority.TryGetDrawn(destination, out long owner));
            Assert.Equal(loop.Client.LocalNetId, owner);
        }
        Assert.True(sawRoundingNoise, "the known .NET 10 equal-endpoint rounding frame was never exercised");
    }

    [Fact]
    public void Hollowmere_spawn_equal_endpoint_lerp_noise_is_settled()
    {
        TileMoveState local = TileMoveState.At(new TileCoord(96, 93, 0), TileDirection.S);
        var centre = new Vector2(96f, 93f);
        Vector2 position = Vector2.Lerp(centre, centre, 0.34413f);

        Assert.NotEqual(centre, position);
        Assert.False(TileDrawPriority.IsLocalPresentationMoving(local, position));
    }

    [Theory]
    [InlineData(96, 93)]
    [InlineData(-96, -93)]
    [InlineData(0, 0)]
    public void The_two_ulp_settle_band_handles_positive_negative_and_zero_tiles(int x, int z)
    {
        TileMoveState local = TileMoveState.At(new TileCoord(x, z, 0), TileDirection.S);
        float nearX = float.BitIncrement(float.BitIncrement((float)x));
        float nearZ = float.BitDecrement(float.BitDecrement((float)z));

        Assert.False(TileDrawPriority.IsLocalPresentationMoving(local, new Vector2(nearX, nearZ)));
    }

    [Fact]
    public void A_real_ten_thousandth_tile_presentation_offset_remains_moving()
    {
        TileMoveState local = TileMoveState.At(new TileCoord(96, 93, 0), TileDirection.S);
        var position = new Vector2(96.0001f, 93f);

        Assert.True(TileDrawPriority.IsLocalPresentationMoving(local, position));
    }

    [Fact]
    public void An_in_flight_step_is_moving_even_when_its_presented_position_is_inside_the_settle_band()
    {
        TileMoveState local = TileMoveState.At(new TileCoord(96, 93, 0), TileDirection.S);
        local.StepFrom = new TileCoord(95, 93, 0);
        var position = new Vector2(96f, 93f);

        Assert.True(TileDrawPriority.IsLocalPresentationMoving(local, position));
    }

    [Fact]
    public void A_nonfinite_local_presentation_never_claims_a_settled_tile()
    {
        TileMoveState local = TileMoveState.At(new TileCoord(96, 93, 0), TileDirection.S);

        Assert.True(TileDrawPriority.IsLocalPresentationMoving(local, new Vector2(float.NaN, 93f)));
        Assert.True(TileDrawPriority.IsLocalPresentationMoving(local, new Vector2(96f, float.PositiveInfinity)));
    }
}
