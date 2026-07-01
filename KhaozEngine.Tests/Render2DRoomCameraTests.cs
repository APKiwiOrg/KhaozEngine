using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for room/region cameras (Camera2D zoom-clamp overload, CameraRoom, RoomCamera).</summary>
public class Render2DRoomCameraTests
{
    private const int Vw = 800, Vh = 600;
    private const float Tol = 1e-2f;

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol) =>
        Assert.True(Vector2.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    // ---- Camera2D explicit-zoom ClampPosition overload ----

    [Fact]
    public void Clamp_ExplicitZoomMatchesInstanceZoom()
    {
        var cam = new Camera2D { Zoom = 2f };
        var bounds = new Rect(0f, 0f, 2000f, 1000f);
        var desired = new Vector2(50f, 500f);

        var viaField = cam.ClampPosition(desired, bounds, Vw, Vh);
        var viaArg = cam.ClampPosition(desired, bounds, Vw, Vh, 2f);

        AssertClose(viaField, viaArg);
    }

    [Fact]
    public void Clamp_HigherZoomAllowsPositionNearerEdge()
    {
        var cam = new Camera2D();
        var bounds = new Rect(0f, 0f, 2000f, 1000f);
        var desired = new Vector2(50f, 500f);   // near the left edge

        var atZoom1 = cam.ClampPosition(desired, bounds, Vw, Vh, 1f);   // halfW 400 -> x clamps to 400
        var atZoom2 = cam.ClampPosition(desired, bounds, Vw, Vh, 2f);   // halfW 200 -> x clamps to 200

        Assert.Equal(400f, atZoom1.X, Tol);
        Assert.Equal(200f, atZoom2.X, Tol);
        Assert.True(atZoom2.X < atZoom1.X, "higher zoom should sit nearer the bound edge");
    }

    // ---- CameraRoom ----

    [Fact]
    public void Room_ContainsRespectsBounds()
    {
        var room = new CameraRoom(new Rect(0f, 0f, 100f, 100f));
        Assert.True(room.Contains(new Vector2(50f, 50f)));
        Assert.False(room.Contains(new Vector2(150f, 50f)));
    }

    [Fact]
    public void Room_ZoomDefaultsToNull()
    {
        var noZoom = new CameraRoom(new Rect(0f, 0f, 100f, 100f));
        Assert.Null(noZoom.Zoom);

        var withZoom = new CameraRoom(new Rect(0f, 0f, 100f, 100f), 2f);
        Assert.Equal(2f, withZoom.Zoom);
    }

    // ---- RoomCamera ----

    // Two rooms side by side: A = [0,2000) x [0,1000) zoom 1; B = [2000,4000) x [0,1000) zoom 2.
    private static CameraRoom[] TwoRooms() => new[]
    {
        new CameraRoom(new Rect(0f, 0f, 2000f, 1000f), 1f),
        new CameraRoom(new Rect(2000f, 0f, 2000f, 1000f), 2f),
    };

    [Fact]
    public void Room_FirstUpdateAcquiresContainingRoomNoTransition()
    {
        var cam = new Camera2D();
        var rc = new RoomCamera(cam, TwoRooms());

        rc.Update(new Vector2(500f, 500f), 0.016f, Vw, Vh);

        Assert.Equal(0, rc.ActiveRoomIndex);
        Assert.False(rc.IsTransitioning);
        Assert.Equal(1f, cam.Zoom, Tol);
    }

    [Fact]
    public void Room_CrossingIntoNewRoomTransitionsThenSettles()
    {
        var cam = new Camera2D();
        var rc = new RoomCamera(cam, TwoRooms()) { BlendDuration = 0.4f };

        rc.Update(new Vector2(500f, 500f), 0.016f, Vw, Vh);   // acquire A
        Assert.Equal(0, rc.ActiveRoomIndex);

        var inB = new Vector2(2500f, 500f);
        rc.Update(inB, 0.016f, Vw, Vh);                       // cross into B -> begins hand-off
        Assert.Equal(1, rc.ActiveRoomIndex);
        Assert.True(rc.IsTransitioning);

        for (int i = 0; i < 10; i++) rc.Update(inB, 0.1f, Vw, Vh);   // 1.0s, past BlendDuration

        Assert.False(rc.IsTransitioning);
        Assert.Equal(2f, cam.Zoom, Tol);                      // B's zoom applied
        var s = cam.WorldToScreen(inB, Vw, Vh);
        Assert.True(s.X is >= 0f and <= Vw && s.Y is >= 0f and <= Vh, $"target {inB} -> screen {s} offscreen");
        // B bounds (2000,0,2000,1000) at zoom 2 -> halfW 200, halfH 150 -> inB (2500,500) clamps to itself.
        AssertClose(new Vector2(2500f, 500f), cam.Position, 1f);
    }

    [Fact]
    public void Room_WarpClampsInRoomBounds()
    {
        var cam = new Camera2D();
        var rc = new RoomCamera(cam, TwoRooms());

        // Target near A's left edge; A zoom 1 -> halfW 400 -> X clamps to 400; halfH 300 -> Y in [300,700].
        rc.Warp(new Vector2(50f, 500f), Vw, Vh);

        Assert.Equal(0, rc.ActiveRoomIndex);
        Assert.False(rc.IsTransitioning);
        AssertClose(new Vector2(400f, 500f), cam.Position);
    }

    [Fact]
    public void Room_NullZoomKeepsCurrentZoom()
    {
        var cam = new Camera2D { Zoom = 3f };
        var rooms = new[] { new CameraRoom(new Rect(0f, 0f, 2000f, 1000f)) };   // null zoom
        var rc = new RoomCamera(cam, rooms);

        rc.Warp(new Vector2(1000f, 500f), Vw, Vh);

        Assert.Equal(3f, cam.Zoom, Tol);
    }

    [Fact]
    public void Room_TargetInNoRoomHoldsActiveRoom()
    {
        var cam = new Camera2D();
        var rooms = new[] { new CameraRoom(new Rect(0f, 0f, 2000f, 1000f), 1f) };
        var rc = new RoomCamera(cam, rooms);

        rc.Update(new Vector2(1000f, 500f), 0.016f, Vw, Vh);   // acquire room 0
        Assert.Equal(0, rc.ActiveRoomIndex);

        rc.Update(new Vector2(5000f, 5000f), 0.016f, Vw, Vh);  // outside every room
        Assert.Equal(0, rc.ActiveRoomIndex);                   // holds
    }

    [Fact]
    public void Room_OverlappingRoomsLowestIndexWins()
    {
        var cam = new Camera2D();
        var rooms = new[]
        {
            new CameraRoom(new Rect(0f, 0f, 3000f, 1000f), 1f),
            new CameraRoom(new Rect(1000f, 0f, 3000f, 1000f), 2f),
        };
        var rc = new RoomCamera(cam, rooms);

        rc.Warp(new Vector2(1500f, 500f), Vw, Vh);   // inside both -> room 0

        Assert.Equal(0, rc.ActiveRoomIndex);
        Assert.Equal(1f, cam.Zoom, Tol);
    }

    [Fact]
    public void Room_ExposedFollowStiffnessDrivesInRoomEase()
    {
        // One huge room so nothing clamps; compare a stiff vs a slack in-room follow after one step.
        var room = new[] { new CameraRoom(new Rect(-10000f, -10000f, 20000f, 20000f), 1f) };

        var camStiff = new Camera2D();
        var stiff = new RoomCamera(camStiff, room);
        stiff.Follow.SetStiffness(20f);
        stiff.Warp(new Vector2(0f, 0f), Vw, Vh);

        var camSlack = new Camera2D();
        var slack = new RoomCamera(camSlack, room);
        slack.Follow.SetStiffness(2f);
        slack.Warp(new Vector2(0f, 0f), Vw, Vh);

        stiff.Update(new Vector2(1000f, 0f), 0.1f, Vw, Vh);
        slack.Update(new Vector2(1000f, 0f), 0.1f, Vw, Vh);

        Assert.True(camStiff.Position.X > camSlack.Position.X,
            $"stiff {camStiff.Position.X} should lead slack {camSlack.Position.X}");
    }

    [Fact]
    public void Room_NoRoomBeforeAcquisitionLeavesCameraUntouched()
    {
        var cam = new Camera2D { Position = new Vector2(7f, 7f), Zoom = 1.5f };
        var rc = new RoomCamera(cam, TwoRooms());

        rc.Update(new Vector2(-500f, -500f), 0.016f, Vw, Vh);   // outside every room, never acquired

        Assert.Equal(-1, rc.ActiveRoomIndex);
        Assert.False(rc.IsTransitioning);
        AssertClose(new Vector2(7f, 7f), cam.Position);
        Assert.Equal(1.5f, cam.Zoom, Tol);
    }

    [Fact]
    public void Room_ZeroBlendDurationHandsOffInstantly()
    {
        var cam = new Camera2D();
        var rc = new RoomCamera(cam, TwoRooms()) { BlendDuration = 0f };

        rc.Update(new Vector2(500f, 500f), 0.016f, Vw, Vh);    // acquire A
        rc.Update(new Vector2(2500f, 500f), 0.016f, Vw, Vh);   // cross into B: zero-duration blend = instant snap

        Assert.Equal(1, rc.ActiveRoomIndex);
        Assert.False(rc.IsTransitioning);                      // settled in the same frame
        Assert.Equal(2f, cam.Zoom, Tol);
    }

    [Fact]
    public void Room_FollowSuspendedDuringTransition()
    {
        // Two cameras cross into B with the SAME crossing target (identical blend target), then advance a
        // couple of mid-blend frames passing DIFFERENT in-room targets. If follow were running during the
        // blend the positions would diverge; since follow is suspended (blend-only), they stay identical.
        var inA = new Vector2(500f, 500f);
        var crossB = new Vector2(2500f, 500f);

        var camX = new Camera2D();
        var rcX = new RoomCamera(camX, TwoRooms()) { BlendDuration = 0.4f };
        rcX.Update(inA, 0.016f, Vw, Vh);
        rcX.Update(crossB, 0.016f, Vw, Vh);   // begin hand-off
        rcX.Update(crossB, 0.05f, Vw, Vh);
        rcX.Update(crossB, 0.05f, Vw, Vh);    // still mid-blend, target = crossB

        var camY = new Camera2D();
        var rcY = new RoomCamera(camY, TwoRooms()) { BlendDuration = 0.4f };
        rcY.Update(inA, 0.016f, Vw, Vh);
        rcY.Update(crossB, 0.016f, Vw, Vh);   // identical crossing target -> identical blend target
        rcY.Update(new Vector2(3700f, 850f), 0.05f, Vw, Vh);   // different in-room target during the blend
        rcY.Update(new Vector2(3700f, 850f), 0.05f, Vw, Vh);

        Assert.True(rcX.IsTransitioning && rcY.IsTransitioning, "both should still be transitioning");
        AssertClose(camX.Position, camY.Position);   // blend-driven, independent of the live target
    }

    [Fact]
    public void Room_WarpDuringTransitionCancelsHandoffAndSnaps()
    {
        var cam = new Camera2D();
        var rc = new RoomCamera(cam, TwoRooms()) { BlendDuration = 0.4f };

        rc.Update(new Vector2(500f, 500f), 0.016f, Vw, Vh);    // acquire A
        rc.Update(new Vector2(2500f, 500f), 0.016f, Vw, Vh);   // begin A->B hand-off
        Assert.True(rc.IsTransitioning);

        rc.Warp(new Vector2(500f, 500f), Vw, Vh);              // snap back to A mid-blend

        Assert.False(rc.IsTransitioning);
        Assert.Equal(0, rc.ActiveRoomIndex);
        Assert.Equal(1f, cam.Zoom, Tol);                       // A's zoom

        // The cancelled blend must not resurrect: a settled follow Update keeps us in A, no transition.
        rc.Update(new Vector2(500f, 500f), 0.1f, Vw, Vh);
        Assert.False(rc.IsTransitioning);
        Assert.Equal(0, rc.ActiveRoomIndex);
    }

    [Fact]
    public void Room_RetargetMidBlendToThirdRoomSettlesInThird()
    {
        // A [0,2000) z1, B [2000,4000) z2, C [4000,6000) z3.
        var cam = new Camera2D();
        var rooms = new[]
        {
            new CameraRoom(new Rect(0f, 0f, 2000f, 1000f), 1f),
            new CameraRoom(new Rect(2000f, 0f, 2000f, 1000f), 2f),
            new CameraRoom(new Rect(4000f, 0f, 2000f, 1000f), 3f),
        };
        var rc = new RoomCamera(cam, rooms) { BlendDuration = 0.4f };

        rc.Update(new Vector2(500f, 500f), 0.016f, Vw, Vh);    // acquire A
        rc.Update(new Vector2(2500f, 500f), 0.016f, Vw, Vh);   // begin A->B blend
        rc.Update(new Vector2(2500f, 500f), 0.05f, Vw, Vh);    // still blending to B
        var inC = new Vector2(5000f, 500f);
        rc.Update(inC, 0.016f, Vw, Vh);                        // cross into C mid-blend -> retarget
        Assert.Equal(2, rc.ActiveRoomIndex);

        for (int i = 0; i < 10; i++) rc.Update(inC, 0.1f, Vw, Vh);   // settle

        Assert.False(rc.IsTransitioning);
        Assert.Equal(2, rc.ActiveRoomIndex);
        Assert.Equal(3f, cam.Zoom, Tol);                       // C's zoom
    }
}
