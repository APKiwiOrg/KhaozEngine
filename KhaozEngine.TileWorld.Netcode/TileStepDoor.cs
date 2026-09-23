namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Which DOOR a remote's step came through, read off the sample before it. <see cref="TileMoveSimulator"/> starts
/// a step through two doors and counts them differently on purpose. A step started from a STANDING body (a click,
/// a held key, a chase, the retry after a refused step) spends its first tick on itself, the "a click never costs a
/// tick of standing still" rule, so it reads <see cref="TileMoveState.StepTicks"/> of one on the tick it commits. A
/// step started on LANDING reads zero. One state cannot tell the two apart, because the second tick of a landing
/// door step reads one as well. The sample before it can: a click door step is the first step seen after the body
/// stood on the tile it leaves.
/// <para>PRESENTATION ONLY, and held on the client's per-remote sample rather than anywhere else. The answer is
/// handed to <see cref="TilePresenter"/> as an argument, so the presenter stays a pure function of what it is
/// given, and nothing here reaches the simulation, the wire or the server. The local player needs none of it:
/// <c>ClientPrediction.RenderedState</c> eases from the previous predicted position, which is the standing tile on
/// the click tick, so that path already starts the step at zero.</para>
/// <para>Above a one-tick cadence a misread cannot move the LANDING. The click door fraction and the ordinary one
/// both reach 1 when <c>StepTicks</c> plus the carried ticks reaches the step's total, so a wrong answer reshapes
/// the curve inside the step and never the moment the body arrives.</para>
/// </summary>
internal static class TileStepDoor
{
    /// <summary>Whether <paramref name="now"/>, the sample replacing <paramref name="previous"/>, is a step that
    /// came through the click door. A remote with NO previous sample (first seen, or seen again after leaving
    /// interest) is never asked, and draws as it always did: nothing says which door a step observed late came
    /// through.</summary>
    /// <param name="previous">The sample being replaced.</param>
    /// <param name="previousIsClickDoor">What this answered for <paramref name="previous"/>.</param>
    /// <param name="now">The new sample.</param>
    /// <returns>True for the first sample of a step leaving the tile the previous sample stood on, and for every
    /// later sample of that same step. False for a body at rest, a landing door step, and anything across a
    /// teleport.</returns>
    internal static bool IsClickDoor(in TileMoveState previous, bool previousIsClickDoor, in TileMoveState now)
    {
        // A cut is never a door. A teleport advances the epoch and places a body with no tile to have come from.
        if (!now.IsStepping || now.Epoch != previous.Epoch) return false;
        if (!previous.IsStepping) return now.StepFrom.Equals(previous.Tile);
        // The same step still in flight keeps the door it came through. Progress that went BACKWARDS over the same
        // two tiles is a later step between them, one that began on a landing between two samples.
        return previousIsClickDoor && now.StepFrom.Equals(previous.StepFrom) && now.Tile.Equals(previous.Tile)
            && now.StepTicks >= previous.StepTicks;
    }
}
