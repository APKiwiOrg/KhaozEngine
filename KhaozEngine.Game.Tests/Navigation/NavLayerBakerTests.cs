using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavLayerBakerTests
{
    const float StepHeight = 0.5f;
    const float AgentHeight = 0f;
    const float JumpHeight = 1.2f;
    const float AgentRadius = 0.2f;

    // Resolves "the layer whose surface at this cell is approximately this height" rather than a
    // hardcoded index: layer indices are deterministic but not contractual (see class remarks in
    // NavLayerBaker.cs), so every scenario below looks a layer up by its baked surface height instead.
    static int FindLayer(NavSpace space, int cx, int cz, float expectedHeight, float tolerance = 0.01f)
    {
        for (int l = 0; l < space.Layers.Count; l++)
        {
            float? h = space.Layers[l].SurfaceHeightAt(cx, cz);
            if (h is not null && MathF.Abs(h.Value - expectedHeight) <= tolerance)
                return l;
        }

        throw new Xunit.Sdk.XunitException(
            $"No layer has a passable surface at ({cx}, {cz}) near height {expectedHeight}.");
    }

    static bool AnyLayerHasHeightAt(NavSpace space, int cx, int cz, float expectedHeight, float tolerance = 0.01f)
    {
        for (int l = 0; l < space.Layers.Count; l++)
        {
            float? h = space.Layers[l].SurfaceHeightAt(cx, cz);
            if (h is not null && MathF.Abs(h.Value - expectedHeight) <= tolerance)
                return true;
        }

        return false;
    }

    // ---------------------------------------------------------------------------------------------
    // (a) Bridge over a road. A deck at height 2.0 spans x in [6..9], z in [2..4], carrying TWO
    // surfaces per column (ground at 0, deck at 2.0, 2.0 headroom under the deck). West and east ramps
    // climb from ground to deck height in 0.5 steps.
    //
    // Two separate bakes exercise this world rather than one, because of a real algorithmic
    // sensitivity this suite discovered (reported to the caller): NavLayerExtractor's single-seed
    // BFS region growth can, when a ramp's foot is transitively absorbed into the same giant ground
    // region that ALSO reaches the ramp's own top step from open flanking ground (a plausible layout
    // once the deck footprint is embedded in an open field with clearance to walk underneath), erode
    // the ramp's top step to blocked in every layer via the ordinary single-layer step-height rule -
    // disconnecting the ramp from the deck it was built to reach. A bake scoped to exactly the
    // bridge's own footprint (no competing open ground on the flanks) avoids the collision and is
    // used for the "walking over the deck" assertions. A second, wider bake (with the ramp's flanking
    // rows walled off as a railing) is used for the "walking underneath" assertions, which need the
    // open field on both sides.
    // ---------------------------------------------------------------------------------------------

    static DelegateColumnProvider BridgeProvider() => new((x, z, surfaces) =>
    {
        int cx = (int)MathF.Floor(x);
        int cz = (int)MathF.Floor(z);

        if (cz is >= 2 and <= 4)
        {
            if (cx == 2) { surfaces[0] = new NavSurfaceSample(true, 0.5f, float.PositiveInfinity); return 1; }
            if (cx == 3) { surfaces[0] = new NavSurfaceSample(true, 1.0f, float.PositiveInfinity); return 1; }
            if (cx == 4) { surfaces[0] = new NavSurfaceSample(true, 1.5f, float.PositiveInfinity); return 1; }
            if (cx == 5) { surfaces[0] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity); return 1; }
            if (cx == 10) { surfaces[0] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity); return 1; }
            if (cx == 11) { surfaces[0] = new NavSurfaceSample(true, 1.5f, float.PositiveInfinity); return 1; }
            if (cx == 12) { surfaces[0] = new NavSurfaceSample(true, 1.0f, float.PositiveInfinity); return 1; }
            if (cx == 13) { surfaces[0] = new NavSurfaceSample(true, 0.5f, float.PositiveInfinity); return 1; }
            if (cx is >= 6 and <= 9)
            {
                surfaces[0] = new NavSurfaceSample(true, 0f, 2.0f);
                surfaces[1] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity);
                return 2;
            }
        }

        surfaces[0] = new NavSurfaceSample(true, 0f, float.PositiveInfinity);
        return 1;
    });

    // The wide-field variant used for the under-bridge tests: same deck and ramps, but the ramp is a
    // single-file corridor at z=3 with its z=2/z=4 flanks walled off (a railing), and the deck's own
    // z=2/z=4 edge columns nearest each ramp foot (x=6, x=9) are deck-only (no ground surface, a solid
    // abutment) so they cannot compete with the ramp's touchdown for the same claim. See the class
    // remarks above.
    static DelegateColumnProvider WideBridgeProvider() => new((x, z, surfaces) =>
    {
        int cx = (int)MathF.Floor(x);
        int cz = (int)MathF.Floor(z);

        bool inRampSpan = (cx >= 2 && cx <= 5) || (cx >= 10 && cx <= 13);
        if ((cz == 2 || cz == 4) && inRampSpan) return 0;

        if (cz == 3)
        {
            if (cx == 2) { surfaces[0] = new NavSurfaceSample(true, 0.5f, float.PositiveInfinity); return 1; }
            if (cx == 3) { surfaces[0] = new NavSurfaceSample(true, 1.0f, float.PositiveInfinity); return 1; }
            if (cx == 4) { surfaces[0] = new NavSurfaceSample(true, 1.5f, float.PositiveInfinity); return 1; }
            if (cx == 5) { surfaces[0] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity); return 1; }
            if (cx == 10) { surfaces[0] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity); return 1; }
            if (cx == 11) { surfaces[0] = new NavSurfaceSample(true, 1.5f, float.PositiveInfinity); return 1; }
            if (cx == 12) { surfaces[0] = new NavSurfaceSample(true, 1.0f, float.PositiveInfinity); return 1; }
            if (cx == 13) { surfaces[0] = new NavSurfaceSample(true, 0.5f, float.PositiveInfinity); return 1; }
        }

        if (cz is 2 or 4 && cx is 6 or 9)
        {
            surfaces[0] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity);
            return 1;
        }

        if (cz is >= 2 and <= 4 && cx is >= 6 and <= 9)
        {
            surfaces[0] = new NavSurfaceSample(true, 0f, 2.0f);
            surfaces[1] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity);
            return 2;
        }

        surfaces[0] = new NavSurfaceSample(true, 0f, float.PositiveInfinity);
        return 1;
    });

    static NavSpace BridgeFootprintSpace(float agentHeight = AgentHeight)
        => NavLayerBaker.BakeOverworldLayered(
            BridgeProvider(), minX: 0f, minZ: 2f, maxX: 16f, maxZ: 5f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: agentHeight, jumpHeight: JumpHeight);

    static NavSpace WideBridgeSpace()
        => NavLayerBaker.BakeOverworldLayered(
            WideBridgeProvider(), minX: 0f, minZ: 1f, maxX: 16f, maxZ: 6f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight);

    [Fact]
    public void Bridge_Footprint_TwoLayers_DeckAndGroundBothPresentUnderneath()
    {
        NavSpace space = BridgeFootprintSpace();

        Assert.Equal(2, space.Layers.Count);

        int deckLayer = FindLayer(space, 7, 1, 2.0f);
        int groundLayer = FindLayer(space, 7, 1, 0.0f);
        Assert.NotEqual(deckLayer, groundLayer);
    }

    [Fact]
    public void Bridge_Footprint_PathOverDeck_Complete()
    {
        NavSpace space = BridgeFootprintSpace();
        var planner = new GridPathPlanner(space);

        NavPath path = planner.FindPath(new Vector3(1.5f, 0f, 3.5f), new Vector3(14.5f, 0f, 3.5f), AgentRadius);

        Assert.Equal(NavPathStatus.Complete, path.Status);

        // The deck (height ~2.0) sits on the same continuous graded layer as the ramps and the ground
        // either side of them, so a Complete route between the two ground ends necessarily crosses the
        // deck's own cells: confirm those cells are baked passable on the path's layer.
        int deckLayer = FindLayer(space, 7, 1, 2.0f);
        Assert.True(space.Layers[deckLayer].IsPassable(7, 1, AgentRadius));
        Assert.Contains(path.Waypoints, w => w.Layer == deckLayer);
    }

    [Fact]
    public void Bridge_Footprint_HeadroomGate_DropsUnderDeckGround_KeepsDeck()
    {
        NavSpace gated = BridgeFootprintSpace(agentHeight: 2.5f);

        // The ground under the deck has only 2.0 headroom (the deck sits directly above it), so an
        // agent taller than that never sees it, on any layer.
        Assert.False(AnyLayerHasHeightAt(gated, 7, 1, 0.0f));

        // The deck itself has open-sky (infinite) headroom, so it survives the same gate.
        Assert.True(AnyLayerHasHeightAt(gated, 7, 1, 2.0f));
    }

    [Fact]
    public void Bridge_Wide_PathUnderBridge_Complete_NeverTouchesDeckLayer()
    {
        NavSpace space = WideBridgeSpace();
        var planner = new GridPathPlanner(space);

        NavPath path = planner.FindPath(new Vector3(7.5f, 0f, 1.5f), new Vector3(7.5f, 0f, 5.5f), AgentRadius);

        Assert.Equal(NavPathStatus.Complete, path.Status);

        int deckLayer = FindLayer(space, 7, 1, 2.0f);
        Assert.DoesNotContain(path.Waypoints, w => w.Layer == deckLayer);
    }

    [Fact]
    public void Bridge_Wide_StairLinks_CrossLayer_BothDirections()
    {
        NavSpace space = WideBridgeSpace();

        List<NavLink> crossLayer = space.Links.Where(l => l.Kind == NavLinkKind.Stair && l.FromLayer != l.ToLayer).ToList();
        Assert.NotEmpty(crossLayer);

        // Every cross-layer stair the ground-under-bridge pocket carries has a return trip: the space
        // never leaves an agent able to walk one way across a layer seam but not back.
        foreach (NavLink link in crossLayer)
        {
            Assert.Contains(crossLayer, back =>
                back.FromLayer == link.ToLayer && back.FromX == link.ToX && back.FromZ == link.ToZ &&
                back.ToLayer == link.FromLayer && back.ToX == link.FromX && back.ToZ == link.FromZ);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // (a2) Natural open-field bridge: the exact repro that motivated the growth-invariant + merge-guard
    // fix in NavLayerExtractor. A deck at height 2.0 spans x in [6..9], z in [2..4] (two surfaces per
    // column: ground at 0 with 2.0 headroom, deck at 2.0 with open sky). West and east ramps climb from
    // the surrounding open field to deck height in 0.5 steps, in the SAME z rows, with open field (h 0)
    // on every side. Under the old single-seed growth the flat field absorbed the ramps AND the deck
    // into one region, then StepMask eroded every ramp top (each ramp cell is 8-adjacent to the flat
    // field it climbed from, rise > step) and the deck lost all links. The invariant now refuses those
    // claims, so the ramp edges and deck split into their own layers with walked stair seams, and no
    // cell is eroded. Unlike scenario (a) this needs NO footprint scoping and NO railing walls: it bakes
    // correctly as the natural geometry a game would hand the baker.
    // ---------------------------------------------------------------------------------------------

    const float OpenFieldAgentHeight = 1.5f;

    static DelegateColumnProvider OpenFieldBridgeProvider() => new((x, z, surfaces) =>
    {
        int cx = (int)MathF.Floor(x);
        int cz = (int)MathF.Floor(z);

        if (cz is >= 2 and <= 4)
        {
            switch (cx)
            {
                case 2: surfaces[0] = new NavSurfaceSample(true, 0.5f, float.PositiveInfinity); return 1;
                case 3: surfaces[0] = new NavSurfaceSample(true, 1.0f, float.PositiveInfinity); return 1;
                case 4: surfaces[0] = new NavSurfaceSample(true, 1.5f, float.PositiveInfinity); return 1;
                case 5: surfaces[0] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity); return 1;
                case 10: surfaces[0] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity); return 1;
                case 11: surfaces[0] = new NavSurfaceSample(true, 1.5f, float.PositiveInfinity); return 1;
                case 12: surfaces[0] = new NavSurfaceSample(true, 1.0f, float.PositiveInfinity); return 1;
                case 13: surfaces[0] = new NavSurfaceSample(true, 0.5f, float.PositiveInfinity); return 1;
            }

            if (cx is >= 6 and <= 9)
            {
                surfaces[0] = new NavSurfaceSample(true, 0f, 2.0f);
                surfaces[1] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity);
                return 2;
            }
        }

        surfaces[0] = new NavSurfaceSample(true, 0f, float.PositiveInfinity);
        return 1;
    });

    static NavSpace OpenFieldBridgeSpace()
        => NavLayerBaker.BakeOverworldLayered(
            OpenFieldBridgeProvider(), minX: 0f, minZ: 0f, maxX: 16f, maxZ: 7f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: OpenFieldAgentHeight, jumpHeight: JumpHeight);

    // The single surface height each ramp column carries, west and east mirrored.
    static float RampHeight(int cx) => cx switch
    {
        2 or 13 => 0.5f,
        3 or 12 => 1.0f,
        4 or 11 => 1.5f,
        5 or 10 => 2.0f,
        _ => throw new ArgumentOutOfRangeException(nameof(cx), cx, "Not a ramp column."),
    };

    [Fact]
    public void OpenFieldBridge_NoErosion_EveryRampDeckAndUnderDeckCellSurvives()
    {
        NavSpace space = OpenFieldBridgeSpace();
        const float tol = 1e-3f;

        // Every ramp cell keeps its own height on some layer: the erosion the invariant removes would
        // have blanked these out of every layer.
        foreach (int cx in new[] { 2, 3, 4, 5, 10, 11, 12, 13 })
            for (int cz = 2; cz <= 4; cz++)
                Assert.True(AnyLayerHasHeightAt(space, cx, cz, RampHeight(cx), tol),
                    $"Ramp cell ({cx}, {cz}) at height {RampHeight(cx)} was eroded from every layer.");

        // Both surfaces of every deck column survive: the deck at 2.0 and the ground passing under it at 0.
        for (int cx = 6; cx <= 9; cx++)
            for (int cz = 2; cz <= 4; cz++)
            {
                Assert.True(AnyLayerHasHeightAt(space, cx, cz, 2.0f, tol),
                    $"Deck cell ({cx}, {cz}) at 2.0 missing from every layer.");
                Assert.True(AnyLayerHasHeightAt(space, cx, cz, 0.0f, tol),
                    $"Under-deck ground cell ({cx}, {cz}) at 0.0 missing from every layer.");
            }
    }

    [Fact]
    public void OpenFieldBridge_WalkOntoDeck_Complete_NoHop()
    {
        NavSpace space = OpenFieldBridgeSpace();
        var planner = new GridPathPlanner(space);

        // From the open field up onto the deck. The ground-to-deck rise of 2.0 is far past the jump
        // budget, so the only way across is the walked ramp seams: a Complete route must carry no hop.
        NavPath path = planner.FindPath(new Vector3(1.5f, 0f, 3.5f), new Vector3(7.5f, 2.0f, 3.5f), AgentRadius);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.DoesNotContain(path.Waypoints, w => w.Kind == NavWaypointKind.Hop);
    }

    [Fact]
    public void OpenFieldBridge_WalkUnderDeck_Complete_StaysAtGroundHeight()
    {
        NavSpace space = OpenFieldBridgeSpace();
        var planner = new GridPathPlanner(space);

        NavPath path = planner.FindPath(new Vector3(7.5f, 0f, 0.5f), new Vector3(7.5f, 0f, 6.5f), AgentRadius);

        Assert.Equal(NavPathStatus.Complete, path.Status);

        // The under-deck route never rises off the ground: every waypoint resolves, on its own layer, to
        // a surface at height 0 in the cell it occupies.
        Assert.All(path.Waypoints, wp =>
        {
            NavGrid layer = space.Layers[wp.Layer];
            (int cx, int cz) = layer.CellOf(wp.Position.X, wp.Position.Y);
            float? h = layer.SurfaceHeightAt(cx, cz);
            Assert.NotNull(h);
            Assert.True(MathF.Abs(h!.Value) <= 1e-3f,
                $"Waypoint at {wp.Position} on layer {wp.Layer} resolved to surface height {h}, not ground.");
        });
    }

    [Fact]
    public void OpenFieldBridge_Deterministic_SameWorldTwice_IdenticalLayersAndLinks()
    {
        NavSpace first = OpenFieldBridgeSpace();
        NavSpace second = OpenFieldBridgeSpace();

        Assert.Equal(first.Layers.Count, second.Layers.Count);
        for (int l = 0; l < first.Layers.Count; l++)
        {
            NavGrid a = first.Layers[l];
            NavGrid b = second.Layers[l];
            for (int cz = 0; cz < a.Height; cz++)
                for (int cx = 0; cx < a.Width; cx++)
                    Assert.Equal(a.SurfaceHeightAt(cx, cz), b.SurfaceHeightAt(cx, cz));
        }

        Assert.Equal(first.Links, second.Links);
    }

    [Fact]
    public void OpenFieldBridge_StairLinks_CrossLayer_BothDirections()
    {
        NavSpace space = OpenFieldBridgeSpace();

        // The too-tall contacts the invariant carves into layer boundaries must be seamed with walked
        // stairs, both ways, or an agent could cross a boundary one direction but not back.
        List<NavLink> crossLayer = space.Links.Where(l => l.Kind == NavLinkKind.Stair && l.FromLayer != l.ToLayer).ToList();
        Assert.NotEmpty(crossLayer);

        foreach (NavLink link in crossLayer)
        {
            Assert.Contains(crossLayer, back =>
                back.FromLayer == link.ToLayer && back.FromX == link.ToX && back.FromZ == link.ToZ &&
                back.ToLayer == link.FromLayer && back.ToX == link.FromX && back.ToZ == link.FromZ);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // (b) Roofed interior: a walled room (perimeter x/z in [3..8]) with a two-cell door gap, floor at
    // 0 and roof/wall-top at 2.0. Bakes cleanly with no special-case geometry (no ramps, so no BFS
    // race: the walls jump straight from nothing to wall-top height with no gradual connector, so
    // GrowRegions never has a reason to fold them into the ground region).
    // ---------------------------------------------------------------------------------------------

    static bool IsPerimeter(int cx, int cz) =>
        (cx is 3 or 8 && cz is >= 3 and <= 8) || (cz is 3 or 8 && cx is >= 3 and <= 8);
    static bool IsDoor(int cx, int cz) => cz == 3 && (cx == 5 || cx == 6);
    static bool IsInterior(int cx, int cz) => cx is >= 4 and <= 7 && cz is >= 4 and <= 7;

    static DelegateColumnProvider RoomProvider() => new((x, z, surfaces) =>
    {
        int cx = (int)MathF.Floor(x);
        int cz = (int)MathF.Floor(z);

        if (IsDoor(cx, cz) || IsInterior(cx, cz))
        {
            surfaces[0] = new NavSurfaceSample(true, 0f, 2.0f);
            surfaces[1] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity);
            return 2;
        }

        if (IsPerimeter(cx, cz))
        {
            surfaces[0] = new NavSurfaceSample(true, 2.0f, float.PositiveInfinity);
            return 1;
        }

        surfaces[0] = new NavSurfaceSample(true, 0f, float.PositiveInfinity);
        return 1;
    });

    static NavSpace RoomSpace(float agentHeight = AgentHeight)
        => NavLayerBaker.BakeOverworldLayered(
            RoomProvider(), minX: 0f, minZ: 0f, maxX: 12f, maxZ: 12f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: agentHeight, jumpHeight: JumpHeight,
            maxSurfacesPerColumn: 4);

    [Fact]
    public void Room_PathFromOutsideToInterior_Complete_StaysOnFloorLayer()
    {
        NavSpace space = RoomSpace();
        var planner = new GridPathPlanner(space);

        NavPath path = planner.FindPath(new Vector3(1.5f, 0f, 1.5f), new Vector3(5.5f, 0f, 5.5f), AgentRadius);

        Assert.Equal(NavPathStatus.Complete, path.Status);

        int floorLayer = FindLayer(space, 1, 1, 0.0f);
        Assert.All(path.Waypoints, w => Assert.Equal(floorLayer, w.Layer));
    }

    [Fact]
    public void Room_RoofCell_PassableOnSomeLayer()
    {
        NavSpace space = RoomSpace();

        Assert.True(AnyLayerHasHeightAt(space, 5, 5, 2.0f));
    }

    [Fact]
    public void Room_HeadroomGate_InteriorFloorUnreachable()
    {
        NavSpace gated = RoomSpace(agentHeight: 2.5f);
        var planner = new GridPathPlanner(gated);

        NavPath path = planner.FindPath(new Vector3(1.5f, 0f, 1.5f), new Vector3(5.5f, 0f, 5.5f), AgentRadius);

        Assert.NotEqual(NavPathStatus.Complete, path.Status);
    }

    // ---------------------------------------------------------------------------------------------
    // (c) Cliff, no erosion, plus cross-layer hops. Ground at 0 for x < 8, a plateau at 1.0 for x >= 8:
    // a rise of 1.0 is above the step budget (0.5) and within the jump budget (1.2). A pure elevation
    // step like this never enters the ramp-style BFS race from scenario (a): the two heights are never
    // step-adjacent to begin with, so GrowRegions and MergeRegions never even consider joining them.
    // ---------------------------------------------------------------------------------------------

    static DelegateColumnProvider CliffProvider() => new((x, z, surfaces) =>
    {
        int cx = (int)MathF.Floor(x);
        surfaces[0] = new NavSurfaceSample(true, cx < 8 ? 0f : 1.0f, float.PositiveInfinity);
        return 1;
    });

    static NavSpace CliffSpace(float jumpHeight = JumpHeight)
        => NavLayerBaker.BakeOverworldLayered(
            CliffProvider(), minX: 0f, minZ: 0f, maxX: 12f, maxZ: 8f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: jumpHeight);

    [Fact]
    public void Cliff_TwoLayers_RimNotEroded()
    {
        NavSpace space = CliffSpace();

        Assert.Equal(2, space.Layers.Count);

        int plateauLayer = FindLayer(space, 8, 4, 1.0f);
        NavGrid plateau = space.Layers[plateauLayer];

        // The no-erosion assertion: every plateau rim cell (8, z) is passable in the plateau's own
        // layer. A phase-1 single-layer bake would have blocked the higher side of this step.
        for (int cz = 0; cz < plateau.Height; cz++)
            Assert.NotNull(plateau.SurfaceHeightAt(8, cz));
    }

    [Fact]
    public void Cliff_SingleLayerBake_BlocksTheSameRim_ForContrast()
    {
        var surfaceProvider = new DelegateSurfaceProvider((float x, float z, out float h, out float hr) =>
        {
            h = (int)MathF.Floor(x) < 8 ? 0f : 1.0f;
            hr = float.PositiveInfinity;
            return true;
        });

        NavGrid grid = NavGridBaker.BakeOverworldSteps(
            surfaceProvider, minX: 0f, minZ: 0f, maxX: 12f, maxZ: 8f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight);

        // Phase-1 behavior: the higher side of the too-tall step bakes blocked (StepMask erosion),
        // which is exactly what the layered bake avoids by splitting the plateau into its own layer.
        Assert.Null(grid.SurfaceHeightAt(8, 4));
    }

    [Fact]
    public void Cliff_HopLinks_BothDirections_AcrossRim()
    {
        NavSpace space = CliffSpace();

        List<NavLink> crossLayer = space.Links.Where(l => l.Kind == NavLinkKind.Hop && l.FromLayer != l.ToLayer).ToList();
        Assert.NotEmpty(crossLayer);

        int groundLayer = FindLayer(space, 7, 4, 0.0f);
        int plateauLayer = FindLayer(space, 8, 4, 1.0f);

        Assert.Contains(crossLayer, l =>
            l.FromLayer == groundLayer && l.FromX == 7 && l.FromZ == 4 &&
            l.ToLayer == plateauLayer && l.ToX == 8 && l.ToZ == 4);
        Assert.Contains(crossLayer, l =>
            l.FromLayer == plateauLayer && l.FromX == 8 && l.FromZ == 4 &&
            l.ToLayer == groundLayer && l.ToX == 7 && l.ToZ == 4);
    }

    [Fact]
    public void Cliff_PathAcrossRim_Complete_ContainsHopWaypoint()
    {
        NavSpace space = CliffSpace();
        var planner = new GridPathPlanner(space);

        NavPath path = planner.FindPath(new Vector3(2.5f, 0f, 4.5f), new Vector3(10.5f, 1.0f, 4.5f), AgentRadius);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Contains(path.Waypoints, w => w.Kind == NavWaypointKind.Hop);
    }

    [Fact]
    public void Cliff_JumpHeightTooLow_PathUnreachable()
    {
        NavSpace space = CliffSpace(jumpHeight: 0.9f);
        var planner = new GridPathPlanner(space);

        NavPath path = planner.FindPath(new Vector3(2.5f, 0f, 4.5f), new Vector3(10.5f, 1.0f, 4.5f), AgentRadius);

        Assert.NotEqual(NavPathStatus.Complete, path.Status);
    }

    // ---------------------------------------------------------------------------------------------
    // (d) Single-surface degenerate world: the layered bake over a smooth rolling field (via
    // SurfaceColumnAdapter wrapping an INavSurfaceProvider) must match NavGridBaker.BakeOverworldSteps
    // over the same field cell for cell, and collapse to exactly one layer with no links.
    // ---------------------------------------------------------------------------------------------

    static DelegateSurfaceProvider RollingFieldProvider() => new((float x, float z, out float h, out float hr) =>
    {
        int cx = (int)MathF.Floor(x);
        int cz = (int)MathF.Floor(z);
        h = 0.1f * (cx + cz);
        hr = float.PositiveInfinity;
        return true;
    });

    [Fact]
    public void RollingField_LayeredBake_MatchesSingleLayerStepsBake()
    {
        var columns = new SurfaceColumnAdapter(RollingFieldProvider());

        NavSpace space = NavLayerBaker.BakeOverworldLayered(
            columns, minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight);

        Assert.Single(space.Layers);
        Assert.Empty(space.Links);

        NavGrid expected = NavGridBaker.BakeOverworldSteps(
            RollingFieldProvider(), minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight);
        NavGrid actual = space.Layers[0];

        for (int cz = 0; cz < expected.Height; cz++)
        {
            for (int cx = 0; cx < expected.Width; cx++)
            {
                Assert.Equal(expected.IsPassable(cx, cz, AgentRadius), actual.IsPassable(cx, cz, AgentRadius));
                Assert.Equal(expected.SurfaceHeightAt(cx, cz), actual.SurfaceHeightAt(cx, cz));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // (e) Determinism: baking the same world twice yields identical layers and identical links, in
    // element order.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Bridge_Deterministic_SameWorldTwice_IdenticalLayersAndLinks()
    {
        NavSpace first = BridgeFootprintSpace();
        NavSpace second = BridgeFootprintSpace();

        Assert.Equal(first.Layers.Count, second.Layers.Count);
        for (int l = 0; l < first.Layers.Count; l++)
        {
            NavGrid a = first.Layers[l];
            NavGrid b = second.Layers[l];
            for (int cz = 0; cz < a.Height; cz++)
                for (int cx = 0; cx < a.Width; cx++)
                    Assert.Equal(a.SurfaceHeightAt(cx, cz), b.SurfaceHeightAt(cx, cz));
        }

        Assert.Equal(first.Links, second.Links);
    }

    // ---------------------------------------------------------------------------------------------
    // (g) Baker validation and provider contract.
    // ---------------------------------------------------------------------------------------------

    static DelegateColumnProvider FlatProvider() => new((x, z, surfaces) =>
    {
        surfaces[0] = new NavSurfaceSample(true, 0f, float.PositiveInfinity);
        return 1;
    });

    [Fact]
    public void NullProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NavLayerBaker.BakeOverworldLayered(
            null!, minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight));
    }

    [Fact]
    public void OutOfRangeArguments_Throw()
    {
        DelegateColumnProvider provider = FlatProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() => NavLayerBaker.BakeOverworldLayered(
            provider, minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: 0.5f, agentHeight: AgentHeight, jumpHeight: 0.5f));

        Assert.Throws<ArgumentOutOfRangeException>(() => NavLayerBaker.BakeOverworldLayered(
            provider, minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight,
            maxSurfacesPerColumn: 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => NavLayerBaker.BakeOverworldLayered(
            provider, minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight,
            maxHopCells: 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => NavLayerBaker.BakeOverworldLayered(
            provider, minX: 0f, minZ: 0f, maxX: 0f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight));

        Assert.Throws<ArgumentOutOfRangeException>(() => NavLayerBaker.BakeOverworldLayered(
            provider, minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 0f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight));

        Assert.Throws<ArgumentOutOfRangeException>(() => NavLayerBaker.BakeOverworldLayered(
            provider, minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: -0.1f, agentHeight: AgentHeight, jumpHeight: JumpHeight));

        Assert.Throws<ArgumentOutOfRangeException>(() => NavLayerBaker.BakeOverworldLayered(
            provider, minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: -0.1f, jumpHeight: JumpHeight));
    }

    [Fact]
    public void ProviderOverflow_Throws()
    {
        var provider = new DelegateColumnProvider((x, z, surfaces) =>
        {
            // Reports one more surface than the caller's buffer can possibly hold.
            return surfaces.Length + 1;
        });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => NavLayerBaker.BakeOverworldLayered(
            provider, minX: 0f, minZ: 0f, maxX: 4f, maxZ: 4f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight));
        Assert.Contains("surfaces", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderDescendingOrder_Throws()
    {
        var provider = new DelegateColumnProvider((x, z, surfaces) =>
        {
            surfaces[0] = new NavSurfaceSample(true, 1.0f, float.PositiveInfinity);
            surfaces[1] = new NavSurfaceSample(true, 0.5f, float.PositiveInfinity);
            return 2;
        });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => NavLayerBaker.BakeOverworldLayered(
            provider, minX: 0f, minZ: 0f, maxX: 4f, maxZ: 4f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight,
            maxSurfacesPerColumn: 2));
        Assert.Contains("ascending", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtraBlocked_BlocksRectangle_OnEveryLayer()
    {
        NavSpace space = NavLayerBaker.BakeOverworldLayered(
            FlatProvider(), minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight,
            extraBlocked: (x, z) => x is >= 3f and < 6f && z is >= 3f and < 6f);

        foreach (NavGrid layer in space.Layers)
        {
            for (int cz = 3; cz < 6; cz++)
                for (int cx = 3; cx < 6; cx++)
                    Assert.False(layer.IsPassable(cx, cz, AgentRadius));
        }

        Assert.True(space.Layers[0].IsPassable(1, 1, AgentRadius));
    }

    [Fact]
    public void EmptyProvider_YieldsSingleFullyBlockedLayer_PlannerReturnsUnreachable()
    {
        var provider = new DelegateColumnProvider((x, z, surfaces) => 0);

        NavSpace space = NavLayerBaker.BakeOverworldLayered(
            provider, minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight);

        Assert.Single(space.Layers);
        NavGrid grid = space.Layers[0];
        for (int cz = 0; cz < grid.Height; cz++)
            for (int cx = 0; cx < grid.Width; cx++)
                Assert.False(grid.IsPassable(cx, cz, AgentRadius));

        var planner = new GridPathPlanner(space);
        NavPath path = planner.FindPath(new Vector3(1.5f, 0f, 1.5f), new Vector3(8.5f, 0f, 8.5f), AgentRadius);
        Assert.Equal(NavPathStatus.Unreachable, path.Status);
    }
}
