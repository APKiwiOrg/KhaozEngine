using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>SC-18 worst-case query envelope, derived from controller loop caps without any measured counters.
/// Ground substeps include SlideCore's one carry-only rerun. Air substeps are budgeted separately because the
/// artifact's Substeps counter records ground and slide substeps only. These are structural bounds, not CPU budgets.</summary>
internal readonly record struct SupportCostTickBound(int Substeps, int Finds, int Sweeps, int Penetrations)
{
    internal static SupportCostTickBound For(bool explicitTick)
    {
        checked
        {
            int segments = ContactController.MaxSegments;
            int groundPasses = 2 * segments;
            int groundSteps = groundPasses * GroundCore.MaxSubsteps;
            int airSteps = segments * AirPass.MaxSubsteps;
            // GroundCore.Run has one wall retry. ResolveUnconstrained tries lifted then standing, and
            // GroundCore.Medium.Resolve permits MaximumCorrections medium retries of each wall route.
            int attempts = 2 * 2 * groundSteps * (explicitTick ? 1 + MovementCapsuleResolver.MaximumCorrections : 1);
            int slides = ShellMotion.MaxSlides + 1;
            // Each ground attempt has one seat Find and one pushed-placement Find. AirPass has a landing
            // Find and a pushed-placement Find per step. Ground starts, per-segment water/rounding reads,
            // the three look-ahead reads, initial slide/water classification and final clamp are separate.
            int finds = 2 * attempts + 2 * airSteps + groundPasses + 2 * segments + GroundSignals.Samples + 4;
            // AirPass may rerun a flight along a terrain wall, then vertically. Every shell slide can
            // resolve one medium-constrained route. Each Find retains two proposal sweeps.
            int shellMoves = slides * (attempts + 3 * airSteps);
            int sweeps = 2 * finds + (groundPasses + shellMoves + 2) * (explicitTick ? 2 : 1);
            // Recovery has a final clearance query. A shell slide can read a touch normal, and a seated
            // placement has an overlap and one pushed-clearance query. Two final-clamp queries are separate.
            int penetrations = (ShellMotion.MaxRecoveryPasses + 1) * (groundPasses + segments) +
                (slides + 2) * attempts + (3 * slides + 2) * airSteps + 2;
            if (explicitTick)
            {
                // SweepContactShell certifies then reads the primitive (the factor of two above).
                // PenetrateContactShell certifies at capacity 64, retries once at MaxSolidContacts,
                // then reads the primitive. Resolver contacts already use the maximum-capacity buffer.
                penetrations *= 3;
                // ResolveSegment can enter MovementCapsuleResolver once per shell move. Its initial trace
                // and each correction's full/prefix traces and contact/clearance queries fit this same bound.
                int resolverQueries = (1 + 2 * (MovementCapsuleResolver.MaximumCorrections + 1)) * shellMoves;
                sweeps += resolverQueries;
                penetrations += resolverQueries;
            }
            return new(groundSteps, finds, sweeps, penetrations);
        }
    }

    internal bool Contains(in SupportCostSample sample) => sample.Substeps >= 0 && sample.Substeps <= Substeps &&
        sample.Counts.Find >= 0 && sample.Counts.Find <= Finds &&
        sample.Queries.Sweeps >= 0 && sample.Queries.Sweeps <= Sweeps &&
        sample.Queries.Penetrations >= 0 && sample.Queries.Penetrations <= Penetrations;
}
