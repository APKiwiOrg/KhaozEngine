using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Movement;

internal enum ContactTerrainDropPhase : byte { LaunchPrefix, Descending, LandingFringe, TerminalHold }
internal enum ContactTerrainDropRefusal : byte
{
    None, Certificate, Movement, Evidence, Boundary, Swimming, DescentAllowance, UnknownSupport,
    SteepOutsideChain, UnexpectedSupport, AirAfterLanding, SourceHold, Terminal, Budget
}

/// <summary>Attempt-local transition ownership. Callbacks consume only selected, authenticated route evidence.</summary>
internal sealed class ContactTerrainDropPolicy(ContactTerrainDropCertificate certificate,
    MovementFrameDescriptor frame, MoveTuning tuning, float allowance)
{
    int _lastFace = -1;
    Vector3 _lastFeet;
    MovementSupportKey? _selectorOwner;
    float _highestLaunch = certificate.From.Y;
    internal ContactTerrainDropPhase Phase { get; private set; }
    internal ContactTerrainDropRefusal Refusal { get; private set; }
    internal bool Complete { get; private set; }
    internal NavDropFootprint Corridor { get; } = certificate.CreateCorridor(tuning, allowance);

    internal void BeginTick() => _lastFace = -1;
    internal bool Observe(in ContactSelectedSupport selected)
    {
        selected.AssertCurrent();
        return ObserveSupport(selected.Facts, selected.Mode, selected.FirstWalkable);
    }
    internal bool ObserveSupport(in ContactSupportEvidence fact, ContactDescentMode mode, bool firstWalkable)
    {
        if (Refusal != ContactTerrainDropRefusal.None) return false;
        Vector3 feet = fact.Feet + frame.PhysicsOrigin;
        if (!certificate.Corridor.Contains(feet)) return Refuse(ContactTerrainDropRefusal.Boundary);
        if ((double)_highestLaunch - feet.Y > allowance) return Refuse(ContactTerrainDropRefusal.DescentAllowance);
        if (fact.Status == SupportStatus.None)
        {
            if (Phase >= ContactTerrainDropPhase.LandingFringe) return Refuse(ContactTerrainDropRefusal.AirAfterLanding);
            Phase = ContactTerrainDropPhase.Descending;
            return true;
        }
        if (fact.Status is not (SupportStatus.Walkable or SupportStatus.Steep) || !certificate.TryFace(fact, out int face))
            return Refuse(ContactTerrainDropRefusal.UnknownSupport);
        if (_selectorOwner is { } owner && owner != fact.Binding.Owner) return Refuse(ContactTerrainDropRefusal.UnknownSupport);
        _selectorOwner ??= fact.Binding.Owner;
        _lastFace = face;
        _lastFeet = feet;
        if (fact.Status == SupportStatus.Steep)
        {
            if (Phase >= ContactTerrainDropPhase.LandingFringe || !certificate.Steep(face))
                return Refuse(ContactTerrainDropRefusal.SteepOutsideChain);
            Phase = ContactTerrainDropPhase.Descending;
            return true;
        }
        if (firstWalkable || mode is ContactDescentMode.Air or ContactDescentMode.Slide or ContactDescentMode.Water)
            if (Phase == ContactTerrainDropPhase.LaunchPrefix) Phase = ContactTerrainDropPhase.Descending;
        if (Phase == ContactTerrainDropPhase.LaunchPrefix)
        {
            if (!certificate.Upper(face)) return Refuse(ContactTerrainDropRefusal.UnexpectedSupport);
            _highestLaunch = MathF.Max(_highestLaunch, feet.Y);
            return true;
        }
        if (!certificate.Landing(face, feet)) return Refuse(ContactTerrainDropRefusal.UnexpectedSupport);
        if (Phase == ContactTerrainDropPhase.Descending) Phase = ContactTerrainDropPhase.LandingFringe;
        return true;
    }

    internal bool AcceptTick(in MovementStepResult result, in ContactDescentSummary summary, bool initial, bool hold)
    {
        if (Refusal != ContactTerrainDropRefusal.None) return false;
        MoveState body = result.State.State;
        Vector3 feet = body.Position + frame.PhysicsOrigin - Vector3.UnitY * tuning.CapsuleHalfHeight;
        if (result.Outcome != MovementStepOutcome.Advanced || body.Commitment.IsActive || body.JumpBufferRemaining != 0 ||
            !float.IsFinite(body.VerticalVelocity) || !float.IsFinite(body.HorizontalVelocity.X) || !float.IsFinite(body.HorizontalVelocity.Y))
            return Refuse(ContactTerrainDropRefusal.Movement);
        if (summary.Status != ContactDescentEvidenceStatus.Complete || !summary.Accepted || result.ContactUnwalkableSupport && !summary.Steep)
            return Refuse(ContactTerrainDropRefusal.Evidence);
        if (body.Swimming || result.ContactEnteredSwimming || summary.Swimming || summary.WaterExcursion)
            return Refuse(ContactTerrainDropRefusal.Swimming);
        if (!certificate.Corridor.Contains(feet)) return Refuse(ContactTerrainDropRefusal.Boundary);
        if ((double)_highestLaunch - feet.Y > allowance) return Refuse(ContactTerrainDropRefusal.DescentAllowance);
        bool supported = body.Grounded && result.ContactWalkableSupport && result.State.Selection?.Support is not null;
        if (supported && (_lastFace < 0 || _lastFeet.X != feet.X || _lastFeet.Z != feet.Z))
            return Refuse(ContactTerrainDropRefusal.Evidence);
        if (body.Grounded && !supported) return Refuse(ContactTerrainDropRefusal.UnknownSupport);
        if (initial)
            return supported && Phase == ContactTerrainDropPhase.LaunchPrefix && certificate.Source(_lastFace) &&
                certificate.Ground.Accepts(feet) && GroundTraversalProbe.Near(feet, certificate.From) || Refuse(ContactTerrainDropRefusal.SourceHold);
        if (Phase >= ContactTerrainDropPhase.LandingFringe && !supported) return Refuse(ContactTerrainDropRefusal.AirAfterLanding);
        if (hold)
        {
            if (Phase != ContactTerrainDropPhase.TerminalHold || !supported || !certificate.Terminal(_lastFace, feet))
                return Refuse(ContactTerrainDropRefusal.Terminal);
            Complete = true;
        }
        return true;
    }

    internal bool ReadyToHold(Vector3 feet)
    {
        if (Refusal != ContactTerrainDropRefusal.None || Phase != ContactTerrainDropPhase.LandingFringe ||
            !certificate.Terminal(_lastFace, feet)) return false;
        Phase = ContactTerrainDropPhase.TerminalHold;
        return true;
    }
    internal bool Refuse(ContactTerrainDropRefusal reason)
    {
        if (Refusal == ContactTerrainDropRefusal.None) Refusal = reason;
        return false;
    }
}
