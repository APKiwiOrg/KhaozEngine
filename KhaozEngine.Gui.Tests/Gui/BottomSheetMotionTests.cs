using System;
using System.Numerics;
using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class BottomSheetMotionTests
{
    [Fact]
    public void Review_content_tap_release_resumes_bounded_settlement_after_interrupting_snap()
    {
        var model = Model();
        model.SetDetent("full", true);
        model.Advance(.09f);
        Assert.Equal(550, model.Height, 3);
        model.Begin(new(0, 100), false);
        model.Release(new(0, 100), .01f);
        Assert.True(model.IsAnimating);
        model.Advance(.18f);
        Assert.Equal(600, model.Height);
        Assert.Equal("full", model.CurrentDetentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Review_equal_resolved_extrema_only_toggle_an_explicit_pair(bool explicitPair)
    {
        var model = new BottomSheetMotion(new[]
        {
            BottomSheetDetent.Fixed("first", 300), BottomSheetDetent.Fixed("second", 500)
        }, "first", new() { HandleToggleDetentIds = explicitPair ? new[] { "first", "second" } : null });
        model.Resolve(200, 1000);
        int changes = 0;
        model.DetentChanged += _ => changes++;
        model.ToggleHandle(false);
        Assert.Equal(explicitPair ? "second" : "first", model.TargetDetentId);
        Assert.Equal(explicitPair ? "second" : "first", model.CurrentDetentId);
        Assert.Equal(explicitPair ? 1 : 0, changes);
        Assert.Equal(200, model.Height);
        model.ToggleHandle(false);
        Assert.Equal("first", model.CurrentDetentId);
        Assert.Equal(explicitPair ? 2 : 0, changes);
    }

    static BottomSheetDetent[] Detents() => new[]
    {
        BottomSheetDetent.Fixed("peek", 200), BottomSheetDetent.Fixed("full", 600)
    };

    static BottomSheetMotion Model(string initial = "peek", BottomSheetOptions? options = null)
    {
        var model = new BottomSheetMotion(Detents(), initial, options);
        model.Resolve(800, 1000);
        return model;
    }

    [Fact]
    public void Fixed_fractional_and_content_capped_detents_use_total_content_including_handle()
    {
        var model = new BottomSheetMotion(new[]
        {
            BottomSheetDetent.Fixed("fixed", 900), BottomSheetDetent.Fraction("fraction", .75f),
            BottomSheetDetent.ContentCapped("content", .45f)
        }, "content");
        model.Resolve(800, 100);
        Assert.Equal(132, model.Height);
        model.SetDetent("fixed", false);
        Assert.Equal(800, model.Height);
        model.SetDetent("fraction", false);
        Assert.Equal(600, model.Height);
        model.SetDetent("content", false);
        model.Resolve(800, 1000);
        Assert.Equal(360, model.Height);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Invalid_resolver_results_are_rejected(float height)
    {
        var model = new BottomSheetMotion(new[] { new BottomSheetDetent("bad", (_, _) => height) }, "bad");
        Assert.Throws<ArgumentOutOfRangeException>(() => model.Resolve(800, 100));
    }

    [Fact]
    public void Empty_duplicate_and_unknown_ids_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new BottomSheetMotion(Array.Empty<BottomSheetDetent>(), "peek"));
        Assert.Throws<ArgumentException>(() => new BottomSheetMotion(new[]
        {
            BottomSheetDetent.Fixed("same", 1), BottomSheetDetent.Fixed("same", 2)
        }, "same"));
        Assert.Throws<ArgumentException>(() => new BottomSheetMotion(Detents(), "missing"));
        Assert.Throws<ArgumentException>(() => BottomSheetDetent.Fixed("", 10));
        Assert.Throws<ArgumentException>(() => Model().SetDetent("missing", false));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Invalid_time_is_rejected(float dt) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Model().Advance(dt));

    [Fact]
    public void Resize_and_content_changes_preserve_named_target_and_cancel_snap()
    {
        var model = new BottomSheetMotion(new[]
        {
            BottomSheetDetent.Fraction("peek", .25f), BottomSheetDetent.Fraction("full", .75f)
        }, "peek");
        model.Resolve(800, 1000);
        model.SetDetent("full", true);
        model.Advance(.09f);
        model.Resolve(400, 1200);
        Assert.Equal("full", model.CurrentDetentId);
        Assert.Equal("full", model.TargetDetentId);
        Assert.Equal(300, model.Height);
        Assert.False(model.IsAnimating);
        model.Resolve(0, 1200);
        Assert.Equal(0, model.Height);
        Assert.Equal("full", model.TargetDetentId);
        model.Resolve(800, 1200);
        Assert.Equal(600, model.Height);
    }

    [Fact]
    public void Upward_handoff_expands_twenty_and_scrolls_remaining_thirty()
    {
        var model = Model();
        model.ApplyHandleDelta(-380);
        var result = model.ApplyContentDelta(-50);
        Assert.Equal(600, result.Height);
        Assert.Equal(30, result.ScrollOffset);
        Assert.Equal(-50, result.Consumed);
        Assert.Equal(0, result.Residual);
    }

    [Fact]
    public void Downward_handoff_unwinds_twenty_then_collapses_thirty()
    {
        var model = Model("full");
        model.ApplyContentDelta(-20);
        var result = model.ApplyContentDelta(50);
        Assert.Equal(570, result.Height);
        Assert.Equal(0, result.ScrollOffset);
        Assert.Equal(50, result.Consumed);
        Assert.Equal(0, result.Residual);
    }

    [Fact]
    public void Boundary_residual_is_signed_and_every_unit_is_accounted_for()
    {
        var model = Model("full");
        var upward = model.ApplyContentDelta(-500);
        Assert.Equal(432, upward.ScrollOffset);
        Assert.Equal(-432, upward.Consumed);
        Assert.Equal(-68, upward.Residual);
        var downward = model.ApplyContentDelta(900);
        Assert.Equal(200, downward.Height);
        Assert.Equal(0, downward.ScrollOffset);
        Assert.Equal(832, downward.Consumed);
        Assert.Equal(68, downward.Residual);
    }

    [Fact]
    public void Handle_motion_keeps_content_offset_and_cannot_scroll()
    {
        var model = Model("full");
        model.ApplyContentDelta(-50);
        var result = model.ApplyHandleDelta(100);
        Assert.Equal(500, result.Height);
        Assert.Equal(50, result.ScrollOffset);
        result = model.ApplyHandleDelta(-200);
        Assert.Equal(600, result.Height);
        Assert.Equal(50, result.ScrollOffset);
        Assert.Equal(-100, result.Residual);
    }

    [Fact]
    public void Velocity_projection_selects_bounded_nearest_detent_and_declaration_order_breaks_ties()
    {
        var model = Model();
        model.ApplyHandleDelta(-100);
        model.Snap(-1600);
        Assert.Equal("full", model.TargetDetentId);
        model.Advance(.18f);
        Assert.Equal(600, model.Height);
        model.ApplyHandleDelta(200);
        model.Snap(0);
        Assert.Equal("peek", model.TargetDetentId);
        model.Snap(float.MaxValue);
        Assert.Equal("peek", model.TargetDetentId);
        var ties = new BottomSheetMotion(new[]
        {
            BottomSheetDetent.Fixed("first", 400), BottomSheetDetent.Fixed("second", 400)
        }, "second");
        ties.Resolve(800, 1000);
        ties.Snap(0);
        Assert.Equal("first", ties.TargetDetentId);
    }

    [Fact]
    public void Snap_uses_cubic_ease_out_and_zero_time_does_not_advance()
    {
        var model = Model();
        model.SetDetent("full", true);
        model.Advance(0);
        Assert.Equal(200, model.Height);
        model.Advance(.09f);
        Assert.Equal(550, model.Height, 3);
        Assert.Equal("peek", model.CurrentDetentId);
        model.Advance(.09f);
        Assert.Equal(600, model.Height);
        Assert.Equal("full", model.CurrentDetentId);
    }

    [Fact]
    public void New_press_interrupts_snap_and_slop_catches_up_all_vertical_travel()
    {
        var model = Model();
        model.SetDetent("full", true);
        model.Advance(.09f);
        model.Begin(new(10, 100), true);
        Assert.False(model.IsAnimating);
        model.Move(new(10, 95), .01f);
        Assert.Equal(550, model.Height, 3);
        model.Move(new(10, 90), .01f);
        Assert.Equal(560, model.Height, 3);
        model.Advance(1);
        Assert.Equal(560, model.Height, 3);
    }

    [Fact]
    public void Stationary_release_removes_stale_velocity_and_final_movement_is_applied()
    {
        var model = Model();
        model.Begin(new(0, 100), true);
        model.Move(new(0, 80), .01f);
        model.Release(new(0, 80), .08f);
        Assert.Equal("peek", model.TargetDetentId);
        model.Advance(.18f);
        model.Begin(new(0, 100), true);
        model.Release(new(0, 50), .01f);
        Assert.Equal(250, model.Height);
        Assert.Equal("full", model.TargetDetentId);
    }

    [Fact]
    public void Events_report_arrival_once_and_dismissal_requires_explicit_detent()
    {
        var model = new BottomSheetMotion(new[]
        {
            BottomSheetDetent.Fixed("zero", 0), BottomSheetDetent.Fixed("closed", 0, true),
            BottomSheetDetent.Fixed("peek", 200)
        }, "peek");
        model.Resolve(800, 1000);
        int changes = 0, dismissals = 0;
        model.DetentChanged += _ => changes++;
        model.Dismissed += () => dismissals++;
        model.SetDetent("zero", false);
        Assert.Equal(1, changes);
        Assert.Equal(0, dismissals);
        model.SetDetent("closed", true);
        model.Advance(.18f);
        model.SetDetent("closed", false);
        Assert.Equal(2, changes);
        Assert.Equal(1, dismissals);
    }

    [Fact]
    public void Caller_driven_handle_toggle_uses_configured_ids()
    {
        var options = new BottomSheetOptions { HandleToggleDetentIds = new[] { "peek", "full" } };
        var model = Model(options: options);
        model.ToggleHandle(false);
        Assert.Equal("full", model.CurrentDetentId);
        model.ToggleHandle(false);
        Assert.Equal("peek", model.CurrentDetentId);
    }

    [Fact]
    public void Default_toggle_excludes_dismissible_detents_and_validates_explicit_pair()
    {
        var model = new BottomSheetMotion(new[]
        {
            BottomSheetDetent.Fixed("closed", 0, true), BottomSheetDetent.Fixed("peek", 200),
            BottomSheetDetent.Fixed("full", 600)
        }, "peek");
        model.Resolve(800, 1000);
        model.ToggleHandle(false);
        Assert.Equal("full", model.CurrentDetentId);
        model.ToggleHandle(false);
        Assert.Equal("peek", model.CurrentDetentId);
        Assert.Throws<ArgumentException>(() => Model(options: new()
        {
            HandleToggleDetentIds = new[] { "peek", "missing" }
        }));
        Assert.Throws<ArgumentException>(() => Model(options: new()
        {
            HandleToggleDetentIds = new[] { "peek", "peek" }
        }));
    }
}
