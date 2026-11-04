using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Movement;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainPolicyRefusalTests(ContactTerrainPolicyFixture fixture) : IClassFixture<ContactTerrainPolicyFixture>
{
    // #1697. One changed witness fact or terminal classification beside the same real physical control.
    [Theory]
    [InlineData("canonical-owner")]
    [InlineData("selector-owner")]
    [InlineData("steep-chain")]
    [InlineData("first-patch")]
    [InlineData("same-tick-air")]
    [InlineData("fringe-exit")]
    [InlineData("transient-swim")]
    [InlineData("steep-terminal")]
    [InlineData("wedged-terminal")]
    public void SelectedRouteFaultCannotCompleteTheTerrainTransition(string fault)
    {
        Assert.Equal(ContactTerrainDropRefusal.None, Run(false));
        var expected = fault switch
        {
            "same-tick-air" => ContactTerrainDropRefusal.AirAfterLanding,
            "first-patch" or "fringe-exit" => ContactTerrainDropRefusal.UnexpectedSupport,
            "transient-swim" => ContactTerrainDropRefusal.Swimming,
            "steep-terminal" => ContactTerrainDropRefusal.SteepOutsideChain,
            "steep-chain" => ContactTerrainDropRefusal.SteepOutsideChain,
            _ => ContactTerrainDropRefusal.UnknownSupport
        };
        Assert.Equal(expected, Run(true));

        ContactTerrainDropRefusal Run(bool changed)
        {
            var certificate = fixture.Certificate();
            MovementSurfaceFeatureKey? excluded = null;
            if (changed && fault == "steep-chain")
            {
                Assert.Equal(MovementAvailability.Known, fixture.Scene.Source.Describe(new(1.05f, -1.375f), out var steep));
                excluded = steep.Key;
                certificate = certificate.WithoutSteepForTests(steep.Key);
            }
            certificate.Corridor.BeginMode();
            using var binding = fixture.Scene.Context.CreateNavigationBinding(ContactTerrainScene.Tuning);
            var frame = fixture.Scene.Context.NavigationFrame;
            var policy = new ContactTerrainDropPolicy(certificate, frame, ContactTerrainScene.Tuning, 6);
            bool injected = false, hold = false;
            var witness = new ContactDescentEvidence(Observe);
            var boundary = certificate.Corridor.Compose(binding.CaptureOrdinaryBoundary(frame), frame);
            var state = new FramedMovementState(new MoveState
            { Position = fixture.From + Vector3.UnitY * ContactTerrainScene.Tuning.CapsuleHalfHeight, Grounded = true, SpeedScale = 1 }, frame, null);
            for (int slice = 0; slice < fixture.Capture.Options.MaxEdgeProbeSteps; slice++)
            {
                hold = slice == 0 || policy.Phase == ContactTerrainDropPhase.TerminalHold;
                policy.BeginTick();
                Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var lease));
                using (lease)
                {
                    lease!.DescentEvidence = witness;
                    Vector2 input = hold ? Vector2.Zero : ContactDropControl.Input(state.State, fixture.To,
                        ContactTerrainScene.Tuning, true, 1, fixture.Capture.Options.EdgeProbeSeconds);
                    var result = binding.StepTowards(new(state.State, frame, null), input, true,
                        fixture.Capture.Options.EdgeProbeSeconds, lease, boundary);
                    var summary = witness.Summary;
                    if (changed && !injected && slice > 0 && fault == "transient-swim" && policy.Phase == ContactTerrainDropPhase.LandingFringe)
                    {
                        Assert.False(state.State.Swimming || result.State.State.Swimming);
                        summary = summary with { Swimming = true };
                        injected = true;
                    }
                    if (changed && !injected && slice > 0 && hold && fault == "wedged-terminal")
                    { result = result with { ContactWalkableSupport = false }; injected = true; }
                    if (!policy.AcceptTick(result, summary, slice == 0, hold))
                    {
                        Assert.True(changed && injected);
                        return policy.Refusal;
                    }
                    if (policy.Complete) { Assert.False(changed); return ContactTerrainDropRefusal.None; }
                    policy.ReadyToHold(result.State.State.Position - Vector3.UnitY * ContactTerrainScene.Tuning.CapsuleHalfHeight);
                    state = result.State;
                }
            }
            Assert.Fail("The real control exhausted the original proof duration.");
            return ContactTerrainDropRefusal.Budget;

            bool Observe(in ContactSelectedSupport selected)
            {
                selected.AssertCurrent();
                var fact = selected.Facts;
                bool first = selected.FirstWalkable;
                if (!changed || injected) return policy.Observe(selected);
                if (fault == "steep-chain" && fact.Status == SupportStatus.Steep && fact.Feature?.Key == excluded)
                    injected = true;
                if (fault == "steep-terminal" && hold && policy.Phase == ContactTerrainDropPhase.TerminalHold)
                { fact = fact with { Status = SupportStatus.Steep }; injected = true; }
                if (first && fault == "canonical-owner" && fact.Feature is { } feature)
                { fact = fact with { Feature = feature with { Owner = new("world", "unrelated-same-height-patch") } }; injected = true; }
                if (first && fault == "selector-owner")
                {
                    var b = fact.Binding;
                    fact = fact with { Binding = new(new("world", "unrelated-selector"), b.Space, b.TraversedLinkId, b.Identity) };
                    injected = true;
                }
                if (first && fault == "first-patch")
                {
                    Assert.Equal(MovementAvailability.Known, fixture.Scene.Source.Describe(new(fixture.From.X, fixture.From.Z), out var upper));
                    fact = fact with { Feature = upper }; injected = true;
                }
                if (first && fault == "fringe-exit")
                { fact = fact with { Feet = fact.Feet with { X = fixture.To.X + 0.25f } }; injected = true; }
                bool accepted = policy.ObserveSupport(fact, selected.Mode, first);
                if (first && fault == "same-tick-air")
                {
                    injected = true;
                    return policy.ObserveSupport(fact with { Status = SupportStatus.None }, ContactDescentMode.Air, false);
                }
                return accepted;
            }
        }
    }
}
