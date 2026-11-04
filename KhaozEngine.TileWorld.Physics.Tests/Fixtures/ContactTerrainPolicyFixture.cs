using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

public sealed class ContactTerrainPolicyFixture : IDisposable
{
    internal ContactTerrainScene Scene { get; }
    internal PhysicsNavBake Capture { get; }
    internal GroundNavigation Navigation { get; }
    internal ContactTerrainProfileEvidence Sources { get; }
    internal NavLink Link { get; }
    internal Vector3 From => ContactDropProof.Feet(Navigation.Space.Layers[Link.FromLayer], Link.FromX, Link.FromZ);
    internal Vector3 To => ContactDropProof.Feet(Navigation.Space.Layers[Link.ToLayer], Link.ToX, Link.ToZ);
    public ContactTerrainPolicyFixture() : this(4) { }
    internal ContactTerrainPolicyFixture(float height, TileWorldDocument? document = null,
        NavAreaClassifier? classify = null, IMovementWaterField? water = null,
        Action<BepuPhysicsWorld, OrdinaryMovementRegistration.Builder>? extra = null, Vector3? target = null)
    {
        Scene = new(height, document: document, water: water, extra: extra);
        var options = new PhysicsNavBakeOptions(0, -3, 5, 0, 0.25f, 10, 14,
            ContactTerrainScene.Tuning.MaxSlopeRadians, 256, 1024);
        Capture = PhysicsNavBake.CaptureDescent(Scene.Context, options, classify ?? (_ => 0));
        Navigation = Capture.BuildProfile(ContactTerrainScene.Tuning, default);
        Sources = new(Capture.DescentEvidence!, Navigation);
        var grid = Navigation.Space.Layers[0];
        var (sx, sz) = grid.CellOf(Scene.From.X, Scene.From.Z);
        var destination = target ?? Scene.To;
        var (tx, tz) = grid.CellOf(destination.X, destination.Z);
        int from = Enumerable.Range(0, Navigation.Space.Layers.Count).Single(i => Navigation.Graph.Layers[i].IsAccepted(sx, sz));
        int to = Enumerable.Range(0, Navigation.Space.Layers.Count).Single(i => Navigation.Graph.Layers[i].IsAccepted(tx, tz));
        Link = new(from, sx, sz, to, tx, tz) { Kind = NavLinkKind.Drop };
    }
    internal ContactTerrainDropCertificate Certificate() => ContactTerrainDropCertificate.Create(Capture.DescentEvidence!,
        Sources, Navigation.Footprint, ContactTerrainScene.Tuning, Navigation.Space, Link, From, To, 6)
        ?? throw new InvalidOperationException("The physical control requires a complete terrain certificate.");
    internal (bool Accepted, ContactTerrainDropRefusal Reason, IReadOnlyList<ContactDropStepObservation> Steps) Prove(
        NavSpace? space = null, NavLink? link = null, MoveTuning? tuning = null, NavAreaFilter? areas = null)
    {
        var steps = new List<ContactDropStepObservation>();
        using var hooks = ContactNavigationBuildHooks.Install(new() { DropStep = steps.Add });
        var body = tuning ?? ContactTerrainScene.Tuning;
        var ground = areas is { } filter ? new NavAreaFootprint(Capture.Columns, Capture.Options, body, filter) : Navigation.Footprint;
        using var binding = Scene.Context.CreateNavigationBinding(body);
        using var proof = new ContactNavigationProof(Capture, Scene.Context, binding, ground, maxDropMetres: 6);
        proof.EnableTerrainDescents(Capture.DescentEvidence!, Sources);
        bool accepted = proof.TryDrop(space ?? Navigation.Space, link ?? Link);
        return (accepted, proof.TerrainRefusal, steps);
    }
    internal IReadOnlyList<ContactDropStepObservation> ObserveRunUntilRefusal()
    {
        var certificate = Certificate();
        using var binding = Scene.Context.CreateNavigationBinding(ContactTerrainScene.Tuning);
        var frame = Scene.Context.NavigationFrame;
        var policy = new ContactTerrainDropPolicy(certificate, frame, ContactTerrainScene.Tuning, 6);
        var witness = new ContactDescentEvidence(policy.Observe);
        var boundary = policy.Corridor.Compose(binding.CaptureOrdinaryBoundary(frame), frame);
        var state = new FramedMovementState(new MoveState
        { Position = From + Vector3.UnitY * ContactTerrainScene.Tuning.CapsuleHalfHeight, Grounded = true, SpeedScale = 1 }, frame, null);
        var steps = new List<ContactDropStepObservation>();
        for (int slice = 0; slice < Capture.Options.MaxEdgeProbeSteps; slice++)
        {
            bool hold = slice == 0 || policy.Phase == ContactTerrainDropPhase.TerminalHold;
            policy.BeginTick();
            if (binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var lease) != MovementAvailability.Known || lease is null)
                throw new InvalidOperationException("The observed control requires its original movement lease.");
            using (lease)
            {
                lease.DescentEvidence = witness;
                var start = new FramedMovementState(state.State, frame, null);
                _ = binding.SampleWaterForPacing(start, lease, out _, out float scale);
                Vector2 input = hold ? Vector2.Zero : ContactDropControl.Input(state.State, To, ContactTerrainScene.Tuning,
                    true, scale, Capture.Options.EdgeProbeSeconds);
                var result = binding.StepTowards(start, input, true, Capture.Options.EdgeProbeSeconds, lease, boundary);
                Vector3 feet = result.State.State.Position - Vector3.UnitY * ContactTerrainScene.Tuning.CapsuleHalfHeight;
                steps.Add(new(From, feet, true, hold, result.Outcome, result.State.State.Grounded, result.State.State.CommandedVelocity)
                { Swimming = result.State.State.Swimming, EnteredSwimming = result.ContactEnteredSwimming });
                if (!policy.AcceptTick(result, witness.Summary, slice == 0, hold) || policy.Complete) return steps;
                policy.ReadyToHold(feet);
                state = result.State;
            }
        }
        throw new InvalidOperationException("The observed control exhausted the unchanged duration.");
    }
    public void Dispose() { Capture.Dispose(); Scene.Dispose(); }
}
