using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Fresh-bake terrain proof using the ordinary Contact solver, input and unchanged slice budget.</summary>
internal sealed class ContactTerrainDropProof(PhysicsNavBakeOptions options, EffectiveMovementBinding source,
    MovementFrameDescriptor frame, Func<Vector3, MovementQueryLease> acquire, NavAreaFootprint ground,
    float allowance, bool reuse, PhysicsNavDescentEvidence evidence, ContactTerrainProfileEvidence sources) : IDisposable
{
    readonly EffectiveMovementBinding _binding = source.CreateFallProofBinding();
    readonly ContactNavigationBuildHooks? _hooks = ContactNavigationBuildHooks.Current;
    internal ContactTerrainDropRefusal Refusal { get; private set; }
    internal bool IsTerrain(NavSpace space, NavLink link) => ContactTerrainDropCertificate.HasSteep(evidence,
        _binding.Tuning, ContactDropProof.Feet(space.Layers[link.FromLayer], link.FromX, link.FromZ),
        ContactDropProof.Feet(space.Layers[link.ToLayer], link.ToX, link.ToZ));

    internal bool TryEdge(NavSpace space, NavLink link)
    {
        Refusal = ContactTerrainDropRefusal.None;
        var tuning = _binding.Tuning;
        Vector3 from = ContactDropProof.Feet(space.Layers[link.FromLayer], link.FromX, link.FromZ);
        Vector3 to = ContactDropProof.Feet(space.Layers[link.ToLayer], link.ToX, link.ToZ);
        SupportQueryDiagnostics.Record(SupportQueryCounter.DropCandidates);
        _hooks?.DropCandidate?.Invoke(new(link, (double)from.Y - to.Y, tuning.WalkSpeed > 0, tuning.RunSpeed > 0));
        if (link.Kind != NavLinkKind.Drop || !HeightAllowed(from.Y, to.Y, tuning.StepHeight, allowance))
            return Refuse(ContactTerrainDropRefusal.DescentAllowance);
        if (!ground.Accepts(from) || !ground.Accepts(to)) return Refuse(ContactTerrainDropRefusal.Certificate);
        var certificate = ContactTerrainDropCertificate.Create(evidence, sources, ground, tuning, space, link, from, to, allowance);
        if (certificate is null) return Refuse(ContactTerrainDropRefusal.Certificate);
        bool usable = false;
        foreach (bool run in new[] { false, true })
        {
            if ((run ? tuning.RunSpeed : tuning.WalkSpeed) == 0) continue;
            usable = true;
            SupportQueryDiagnostics.Record(run ? SupportQueryCounter.DropRunProofs : SupportQueryCounter.DropWalkProofs);
            if (!TryMode(certificate, run)) return false;
        }
        return usable;
    }

    bool TryMode(ContactTerrainDropCertificate certificate, bool run)
    {
        var policy = new ContactTerrainDropPolicy(certificate, frame, _binding.Tuning, allowance);
        var witness = new ContactDescentEvidence(policy.Observe);
        using var session = new ContactDropSourceSession(_binding, reuse);
        try
        {
            certificate.Corridor.BeginMode();
            var boundary = certificate.Corridor.Compose(_binding.CaptureOrdinaryBoundary(frame), frame);
            var state = new FramedMovementState(new MoveState
            {
                Position = certificate.From - frame.PhysicsOrigin + Vector3.UnitY * _binding.Tuning.CapsuleHalfHeight,
                Grounded = true,
                SpeedScale = 1,
            }, frame, null);
            for (int slice = 0; slice < options.MaxEdgeProbeSteps; slice++)
            {
                bool initial = slice == 0, hold = initial || policy.Phase == ContactTerrainDropPhase.TerminalHold;
                policy.BeginTick();
                var result = Step(state, certificate, run, boundary, session, witness, hold);
                if (!policy.AcceptTick(result, witness.Summary, initial, hold)) return Refuse(policy.Refusal);
                if (policy.Complete) return true;
                Vector3 feet = Feet(result);
                policy.ReadyToHold(feet);
                state = result.State;
            }
            return Refuse(ContactTerrainDropRefusal.Budget);
        }
        catch (Exception failure) { session.Failure = failure; throw; }
    }

    MovementStepResult Step(in FramedMovementState state, ContactTerrainDropCertificate certificate, bool run,
        MovementBoundary boundary, ContactDropSourceSession session, ContactDescentEvidence witness, bool hold)
    {
        using var queries = acquire(certificate.From);
        session.Bind(queries);
        queries.DescentEvidence = witness;
        var start = new FramedMovementState(state.State, frame, null);
        var water = _binding.SampleWaterForPacing(start, queries, out bool swimming, out float scale);
        if (water.Availability != MovementAvailability.Known) throw new ContactDropProofException(water.Availability);
        if (swimming) return new(start, MovementStepOutcome.PlacementRefused);
        var absolute = state.State with { Position = state.State.Position + frame.PhysicsOrigin };
        Vector2 input = hold ? Vector2.Zero : ContactDropControl.Input(absolute, certificate.To, _binding.Tuning, run, scale, options.EdgeProbeSeconds);
        SupportQueryDiagnostics.Record(SupportQueryCounter.DropSteps);
        var result = _binding.StepTowards(start, input, run, options.EdgeProbeSeconds, queries, boundary);
        queries.AssertCurrent();
        _hooks?.DropStep?.Invoke(new(certificate.From, Feet(result), run, hold, result.Outcome,
            result.State.State.Grounded, result.State.State.CommandedVelocity)
        {
            UnwalkableSupport = result.ContactUnwalkableSupport,
            Swimming = result.State.State.Swimming,
            EnteredSwimming = result.ContactEnteredSwimming
        });
        if (result.Outcome is MovementStepOutcome.EnvironmentInvalid or MovementStepOutcome.EnvironmentUnresolved or MovementStepOutcome.FrameMismatch)
            throw new ContactDropProofException(result.Outcome);
        return result;
    }
    Vector3 Feet(in MovementStepResult result) => result.State.State.Position + frame.PhysicsOrigin -
        Vector3.UnitY * _binding.Tuning.CapsuleHalfHeight;
    internal static bool HeightAllowed(float from, float to, float step, float allowance) =>
        (double)from - to > step && (double)from - to <= allowance;
    bool Refuse(ContactTerrainDropRefusal reason) { Refusal = reason; return false; }
    public void Dispose() => _binding.Dispose();
}
