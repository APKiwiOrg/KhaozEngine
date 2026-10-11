using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.Locomotion.Fixtures;

internal sealed partial class AnalyticMovementEnvironment
{
    readonly List<(StaticHandle Static, MovementSupportKey Owner, MovementSpaceKey Space)> _supportRegistrations = [];
    readonly Dictionary<string, (MovementSupportKey Owner, float Height)> _analyticFields = new(StringComparer.Ordinal);
    HashSet<StaticHandle> _excludedSupports = [];

    public void RegisterBoxSupport(StaticHandle handle, string owner, string room)
    {
        if (_view is not null) throw new InvalidOperationException("Support registration is immutable after acquisition.");
        _ = _rooms.Single(candidate => candidate.Id == room);
        _supportRegistrations.Add((handle, new("world", owner), Space(room)));
    }

    public void RegisterAnalyticGround(string room, string owner, float height)
    {
        if (_view is not null) throw new InvalidOperationException("Analytic registration is immutable after acquisition.");
        if (!float.IsFinite(height)) throw new ArgumentOutOfRangeException(nameof(height));
        _ = _rooms.Single(candidate => candidate.Id == room);
        _analyticFields.Add(room, (new("world", owner), height));
    }

    MovementAvailability SampleAnalyticGround(in MovementSupportRequest request, out MovementAnalyticGround? ground)
    {
        ground = null;
        if (MissingContainment || !request.IsValid) return MovementAvailability.Unresolved;
        MovementSpaceKey space = request.Body.CurrentSpace;
        Vector3 feet = request.Body.Feet;
        Room[] rooms = _rooms.Where(room => room.Id == space.LocalId && room.Bounds.ContainsPoint(feet)).ToArray();
        if (space.WorldId != "world" || rooms.Length != 1) return MovementAvailability.Unresolved;
        if (!_analyticFields.TryGetValue(space.LocalId, out var field)) return MovementAvailability.Known;
        Vector2 axis = new(request.Body.Centre.X, request.Body.Centre.Z);
        if (!TryContactLink(space, request.Transition, new(axis.X, field.Height, axis.Y), out string? link))
            return MovementAvailability.Unresolved;
        ground = new(axis, field.Height, Vector3.UnitY, field.Owner, space, link, Identity);
        return MovementAvailability.Known;
    }
    MovementAvailability BindContactSupport(in MovementContactSupportRequest request, out MovementContactSupportBinding binding)
    {
        binding = default;
        if (MissingContainment || !request.IsValid) return MovementAvailability.Unresolved;
        MovementSupportKey owner;
        MovementSpaceKey space;
        if (request.Static is { } installed)
        {
            // This finite producer registers box face IDs 0 to 5, under its one captured selected view.
            if (_excludedSupports.Contains(installed) || request.FeatureId is < 0 or > 5)
                return MovementAvailability.Unresolved;
            var registrations = _supportRegistrations.Where(row => row.Static == installed).Distinct().ToArray();
            if (registrations.Length == 0) return MovementAvailability.Unresolved;
            if (registrations.Length != 1) return MovementAvailability.Invalid;
            owner = registrations[0].Owner;
            space = registrations[0].Space;
        }
        else
        {
            space = request.Body.CurrentSpace;
            if (!_analyticFields.TryGetValue(space.LocalId, out var field) || field.Height != request.Height ||
                request.Normal != Vector3.UnitY) return MovementAvailability.Unresolved;
            owner = field.Owner;
        }
        if (!TryContactLink(space, request.Transition, new(request.Axis.X, request.Height, request.Axis.Y), out string? link))
            return MovementAvailability.Unresolved;
        binding = new(owner, space, link, Identity);
        return MovementAvailability.Known;
    }

    bool TryContactLink(MovementSpaceKey destination, MovementTransitionContext transition, Vector3 feet, out string? link)
    {
        link = null;
        string origin = transition.OriginSpace.LocalId;
        if (transition.OriginSpace.WorldId != "world" || destination.WorldId != "world" ||
            !_rooms.Any(room => room.Id == origin && room.Bounds.ContainsPoint(transition.StartFeet)) ||
            !_rooms.Any(room => room.Id == destination.LocalId && room.Bounds.ContainsPoint(feet))) return false;
        if (destination == transition.OriginSpace) return true;
        Link[] links = _links.Where(row => row.A == origin && row.B == destination.LocalId ||
            row.B == origin && row.A == destination.LocalId).ToArray();
        if (links.Length != 1) return false;
        link = links[0].A + "/" + links[0].B;
        return true;
    }
}
