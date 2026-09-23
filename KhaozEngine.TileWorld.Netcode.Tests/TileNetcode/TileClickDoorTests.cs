using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The CLICK DOOR re-base, on its arithmetic, and the pins that nothing else moved by a bit. A remote's sample
/// chain here is the simulator's state after every tick, each drawn for fifteen frames of a quarter second tick,
/// with the door read off the sample before it exactly as <see cref="TileWorldClient"/> reads it. The real wiring
/// is pinned in <see cref="TileClickDoorLoopTests"/>.
/// <para>WHY the local player has no counterpart here: <c>ClientPrediction.RenderedState</c> eases from the
/// previous predicted position, which on the click tick is the standing tile, so that path already starts the step
/// at zero. It is pinned below against its own old arithmetic and left exactly as it was.</para>
/// </summary>
public class TileClickDoorTests
{
    const float Tick = 0.25f;
    const int FramesPerTick = 15;
    const float Frame = Tick / FramesPerTick;
    static readonly TilePresenter P = new(tileSize: 1f, planeHeight: 4f);
    static readonly TileCollisionMap Map = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
    static readonly TileCoord Start = new(10, 10, 0);

    readonly record struct Sample(TileMoveState State, bool ClickDoor);

    [Theory]
    [InlineData(4, 2, TileMoveMode.Walk)]
    [InlineData(4, 2, TileMoveMode.Run)]
    [InlineData(1, 1, TileMoveMode.Walk)]
    public void A_click_door_step_starts_on_the_tile_it_leaves_and_moves_at_its_own_rate(
        byte walk, byte run, TileMoveMode mode)
    {
        var cadence = new TileStepTicks(walk, run);
        int n = cadence.For(mode);
        List<Sample> samples = Chain(cadence, 12, (2, TileCommand.WalkTo(new TileCoord(10, 20, 0), mode)));
        const int click = 3;
        // The simulation is untouched: the clicked step still reads one tick in on the tick it commits.
        Assert.Equal(1, samples[click].State.StepTicks);
        Assert.True(samples[click].ClickDoor);

        Vector3[] before = Draw(samples, doors: false);
        Vector3[] after = Draw(samples, doors: true);
        int first = click * FramesPerTick;

        // Before: the whole missing tick in the one frame the step commits.
        Assert.Equal(1f / n, Vector3.Distance(before[first - 1], before[first]), 5);
        // After: nothing on that frame, then the step's own rate on every frame of its first sample.
        Assert.Equal(before[first - 1], after[first]);
        float rate = Frame / Tick / Math.Max(1, n - 1);
        for (int i = first + 1; i < first + FramesPerTick; i++)
            Assert.Equal(rate, Vector3.Distance(after[i - 1], after[i]), 5);
    }

    [Theory]
    [InlineData(4, 2, TileMoveMode.Walk)]
    [InlineData(4, 2, TileMoveMode.Run)]
    [InlineData(1, 1, TileMoveMode.Walk)]
    public void A_click_door_step_lands_on_the_committed_tile_at_the_committed_tick(
        byte walk, byte run, TileMoveMode mode)
    {
        var cadence = new TileStepTicks(walk, run);
        int n = cadence.For(mode);
        List<Sample> samples = Chain(cadence, 12, (2, TileCommand.WalkTo(new TileCoord(10, 20, 0), mode)));
        const int click = 3;
        TileCoord target = samples[click].State.Tile;

        // The simulator lands the step max(1, N - 1) ticks after it commits, and every sample until then is flagged.
        int landing = click + Math.Max(1, n - 1);
        for (int i = click; i < landing; i++)
        {
            Assert.True(samples[i].ClickDoor, $"sample {i} of the clicked step lost its door");
            Assert.Equal(target, samples[i].State.Tile);
        }
        Assert.Equal(target, samples[landing].State.StepFrom);
        Assert.False(samples[landing].ClickDoor);

        // Short of the tile a frame before the landing sample takes over, ON it exactly as that sample does, and
        // the landing sample starts from the same point.
        TileMoveState last = samples[landing - 1].State;
        Vector3 onTile = P.PoseAt(target).Position;
        Assert.NotEqual(onTile, P.Pose(last, 1f - 1f / FramesPerTick, clickDoor: true).Position);
        Assert.Equal(onTile, P.Pose(last, 1f, clickDoor: true).Position);
        Assert.Equal(onTile, P.Pose(samples[landing].State, 0f, clickDoor: false).Position);
        // Above one tick, the old read lands at that same instant, so only the shape inside the step changed.
        if (n > 1) Assert.Equal(onTile, P.Pose(last, 1f).Position);
    }

    /// <summary>
    /// The public reads are the old arithmetic, bit for bit, for every state a scripted session produces: a
    /// click, a run toggle, a re-click while moving, a stop, a second click, at three cadences, over carried ticks
    /// either side of the step. And the internal form with the door DOWN is the same function.
    /// </summary>
    [Theory]
    [InlineData(4, 2)]
    [InlineData(3, 2)]
    [InlineData(1, 1)]
    public void Every_read_without_the_door_is_the_old_arithmetic_bit_for_bit(byte walk, byte run)
    {
        var cadence = new TileStepTicks(walk, run);
        List<Sample> samples = Chain(cadence, 60,
            (0, TileCommand.WalkTo(new TileCoord(10, 30, 0), TileMoveMode.Walk)),
            (5, TileCommand.Continue(TileMoveMode.Run)),
            (9, TileCommand.WalkTo(new TileCoord(18, 14, 0), TileMoveMode.Run)),
            (40, TileCommand.WalkTo(new TileCoord(14, 20, 0), TileMoveMode.Walk)));
        var states = new List<TileMoveState>();
        foreach (Sample s in samples) states.Add(s.State);
        TileMoveState large = TileMoveState.At(new TileCoord(20, 20, 0), TileDirection.E);
        large.FootprintSize = 3;
        large.StepFrom = new TileCoord(19, 20, 0);
        large.StepTotal = 4;
        large.StepTicks = 2;
        states.Add(large);

        var aim = new Vector2(12.5f, 21f);
        float[] extras = { -0.5f, 0f, 0.1f, 1f / 3f, 0.5f, 0.999f, 1f, 1.75f };
        foreach (TileMoveState s in states)
        {
            foreach (float e in extras)
            {
                TilePose old = OldPose(s, e);
                Assert.Equal(old, P.Pose(s, e));
                Assert.Equal(old, P.Pose(s, e, clickDoor: false));
                Assert.Equal(OldAimedPose(s, aim, e), P.Pose(s, aim, e));
                Assert.Equal(OldAimedPose(s, aim, e), P.Pose(s, aim, e, clickDoor: false));
                Assert.Equal(Bits(OldStepFraction(s, e)), Bits(TilePresenter.StepFraction(s, e)));
                Assert.Equal(Bits(OldStepFraction(s, e)), Bits(TilePresenter.StepFraction(s, e, clickDoor: false)));
            }
            Assert.Equal(Bits(OldStepFraction(s, float.NaN)), Bits(TilePresenter.StepFraction(s, float.NaN)));
        }
    }

    /// <summary>
    /// Only the first step of each route off a standing body is flagged. A re-click while moving starts its route
    /// through the landing door and stays unflagged, a teleport clears the door, and the first click after the
    /// teleport is a click door again.
    /// </summary>
    [Fact]
    public void Only_the_first_step_off_a_standing_body_is_flagged_through_a_repath_and_a_teleport()
    {
        var cadence = new TileStepTicks(4, 2);
        List<Sample> samples = Chain(cadence, 40,
            (0, TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Walk)),
            (6, TileCommand.WalkTo(new TileCoord(16, 16, 0), TileMoveMode.Walk)));
        for (int i = 0; i < samples.Count; i++)
            Assert.True(samples[i].ClickDoor == (i is >= 1 and <= 3),
                $"sample {i} read door {samples[i].ClickDoor}, and only the clicked step's three samples are flagged");
        // The re-path turned the walk: the steps after it left the column the click was walking.
        Assert.NotEqual(10, samples[20].State.Tile.X);

        // A teleport in the middle of a flagged step, then a click from where it was placed.
        List<Sample> clicked = Chain(cadence, 3, (0, TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Walk)));
        Sample mid = clicked[2];
        Assert.True(mid.ClickDoor && mid.State.IsStepping);
        TileMoveState placed = TileMoveState.At(new TileCoord(30, 30, 0), TileDirection.N);
        placed.Epoch = mid.State.Epoch + 1;
        Sample cut = Next(mid, placed);
        Assert.False(cut.ClickDoor);
        Assert.Equal(P.PoseAt(placed.Tile).Position, P.Pose(cut.State, 0.5f, cut.ClickDoor).Position);
        var sim = new TileMoveSimulator(Map, cadence);
        Sample again = Next(cut, sim.Step(placed, TileCommand.WalkTo(new TileCoord(30, 34, 0), TileMoveMode.Walk), Tick));
        Assert.True(again.ClickDoor);
    }

    [Fact]
    public void The_door_is_read_off_the_sample_before_it()
    {
        var a = new TileCoord(10, 10, 0);
        var b = new TileCoord(10, 11, 0);
        var c = new TileCoord(10, 12, 0);
        TileMoveState standingA = TileMoveState.At(a, TileDirection.N);
        TileMoveState standingB = TileMoveState.At(b, TileDirection.N);
        TileMoveState standingC = TileMoveState.At(c, TileDirection.N);

        // The click door, and the same step carrying it.
        Assert.True(TileStepDoor.IsClickDoor(standingA, false, Stepping(a, b, 1)));
        Assert.True(TileStepDoor.IsClickDoor(Stepping(a, b, 1), true, Stepping(a, b, 2)));
        // A step that was not flagged stays unflagged, and so does a landing door step after a flagged one.
        Assert.False(TileStepDoor.IsClickDoor(Stepping(a, b, 1), false, Stepping(a, b, 2)));
        Assert.False(TileStepDoor.IsClickDoor(Stepping(a, b, 3), true, Stepping(b, c, 0)));
        Assert.False(TileStepDoor.IsClickDoor(Stepping(a, b, 3), true, Stepping(b, c, 1)));
        // Arrived, and standing somewhere other than where the step leaves (a lost sample).
        Assert.False(TileStepDoor.IsClickDoor(Stepping(a, b, 3), true, standingB));
        Assert.False(TileStepDoor.IsClickDoor(standingC, false, Stepping(a, b, 1)));
        // Across a teleport, whichever side of it the step is on.
        TileMoveState later = Stepping(a, b, 2);
        later.Epoch = 1;
        Assert.False(TileStepDoor.IsClickDoor(Stepping(a, b, 1), true, later));
        TileMoveState placedClick = Stepping(a, b, 1);
        placedClick.Epoch = 1;
        Assert.False(TileStepDoor.IsClickDoor(standingA, false, placedClick));
        // Progress that went backwards over the same two tiles is a later step, begun on a landing.
        Assert.False(TileStepDoor.IsClickDoor(Stepping(a, b, 3), true, Stepping(a, b, 1)));
    }

    [Theory]
    [InlineData(4, 2, TileMoveMode.Walk)]
    [InlineData(4, 2, TileMoveMode.Run)]
    [InlineData(1, 1, TileMoveMode.Walk)]
    public void The_local_body_is_the_prediction_layers_ease_bit_for_bit_and_starts_a_click_at_zero(
        byte walk, byte run, TileMoveMode mode)
    {
        var cadence = new TileStepTicks(walk, run);
        int n = cadence.For(mode);
        var prediction = new ClientPrediction<TileMoveState, TileCommand>(new TileMoveSimulator(Map, cadence),
            new PredictionSettings(Tick, MaxPendingCommands: 64, HardSnapDistance: 3f, CorrectionRate: 8f,
                CorrectionDeadZone: 0.01f));
        prediction.Reset(TileMoveState.At(Start, TileDirection.N));

        var drawn = new List<Vector3>();
        for (int t = 0; t < 12; t++)
        {
            Vector2 from = prediction.PredictedState.Position;
            prediction.Predict(t == 2 ? TileCommand.WalkTo(new TileCoord(10, 20, 0), mode) : TileCommand.Continue(mode));
            TileMoveState to = prediction.PredictedState;
            float seconds = 0f;
            for (int f = 0; f < FramesPerTick; f++)
            {
                // The old arithmetic, restated: the eased position, the eased plane, the predicted facing.
                float frac = MathF.Min(1f, seconds / Tick);
                TilePose expected = P.PoseAt(Vector2.Lerp(from, to.Position, frac), to.Vertical, to.Facing);
                TilePose actual = P.LocalPose(prediction);
                Assert.Equal(expected, actual);
                drawn.Add(actual.Position);
                prediction.AdvancePresentation(Frame);
                seconds = MathF.Min(seconds + Frame, Tick);
            }
        }

        int first = 2 * FramesPerTick;
        Assert.Equal(drawn[first - 1], drawn[first]);
        for (int i = first + 1; i < first + FramesPerTick; i++)
            Assert.Equal(Frame / Tick / n, Vector3.Distance(drawn[i - 1], drawn[i]), 5);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Harness.
    // ---------------------------------------------------------------------------------------------------------

    // The simulator's state after every tick from a standing start, sample 0 being the start itself, with the door
    // each sample would carry on the client. A tick with no scripted command continues at the state's own mode.
    static List<Sample> Chain(TileStepTicks cadence, int ticks, params (int Tick, TileCommand Command)[] script)
    {
        var sim = new TileMoveSimulator(Map, cadence);
        var commands = new Dictionary<int, TileCommand>();
        foreach ((int tick, TileCommand command) in script) commands[tick] = command;
        TileMoveState s = TileMoveState.At(Start, TileDirection.N);
        var samples = new List<Sample> { new(s, false) };
        for (int t = 0; t < ticks; t++)
        {
            s = sim.Step(s, commands.TryGetValue(t, out TileCommand c) ? c : TileCommand.Continue(s.Mode), Tick);
            samples.Add(Next(samples[^1], s));
        }
        return samples;
    }

    // SampleRemote's rule: an unchanged sample keeps what it had, a changed one reads its door off the one before.
    static Sample Next(Sample previous, in TileMoveState now) =>
        previous.State.Equals(now) ? previous : new Sample(now, TileStepDoor.IsClickDoor(previous.State,
            previous.ClickDoor, now));

    static Vector3[] Draw(List<Sample> samples, bool doors)
    {
        var frames = new Vector3[samples.Count * FramesPerTick];
        for (int i = 0; i < samples.Count; i++)
            for (int f = 0; f < FramesPerTick; f++)
                frames[i * FramesPerTick + f] = P.Pose(samples[i].State, f / (float)FramesPerTick,
                    doors && samples[i].ClickDoor).Position;
        return frames;
    }

    static TileMoveState Stepping(TileCoord from, TileCoord to, byte ticks)
    {
        TileMoveState s = TileMoveState.At(to, TileDirection.N);
        s.StepFrom = from;
        s.StepTotal = 4;
        s.StepTicks = ticks;
        return s;
    }

    static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    // The presenter's arithmetic as it stood before the click door, restated so the pins above compare against it
    // rather than against the code under test.
    static float OldStepFraction(in TileMoveState state, float extraTicks)
        => state.IsStepping && state.StepTotal > 0
            ? Math.Clamp((state.StepTicks + Math.Max(0f, extraTicks)) / state.StepTotal, 0f, 1f)
            : 1f;

    static Vector2 OldCentre(in TileMoveState state, float extraTicks)
    {
        float tileX = state.Tile.X, tileZ = state.Tile.Z;
        if (state.IsStepping && state.StepTotal > 0)
        {
            float f = OldStepFraction(state, extraTicks);
            tileX = state.StepFrom.X + ((float)state.Tile.X - state.StepFrom.X) * f;
            tileZ = state.StepFrom.Z + ((float)state.Tile.Z - state.StepFrom.Z) * f;
        }
        float extra = (state.FootprintSize - 1) * 0.5f;
        return new Vector2(tileX + extra, tileZ + extra);
    }

    static TilePose OldPose(in TileMoveState state, float extraTicks) =>
        P.PoseAt(OldCentre(state, extraTicks), state.Tile.Plane, state.Facing);

    static TilePose OldAimedPose(in TileMoveState state, Vector2 aim, float extraTicks)
    {
        Vector2 centre = OldCentre(state, extraTicks);
        return new TilePose(P.PoseAt(centre, state.Tile.Plane, state.Facing).Position,
            centre == aim ? TilePresenter.Yaw(state.Facing) : TilePresenter.Yaw(centre, aim));
    }
}
