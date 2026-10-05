using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public sealed partial class MutationService
{
    // ---- placements ---------------------------------------------------------------------------------------

    /// <summary>Adds an authored placement. When <paramref name="id"/> is null, auto-generates
    /// <c>p-&lt;kind&gt;-N</c> with the smallest N &gt;= 1 unique against existing placement ids. When
    /// <paramref name="y"/> is null the placement keeps a null Y (ground-snap at load), and either way the result
    /// reports <see cref="MutationResult.GroundY"/> as the field's sampled height at (x, z), so the caller always
    /// sees the resolved height.</summary>
    public MutationResult PlacementAdd(string kind, float x, float z, float? y = null,
        float yaw = 0f, float scale = 1f, string? id = null, IReadOnlyList<string>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        float groundY = session.Field().SampleHeight(x, z);
        string placementId = id ?? session.WithDocument((doc, _) =>
            GenerateId(doc.Placements.Select(p => p.Id), $"p-{kind}-"));

        var placement = new MapPlacement
        {
            Id = placementId,
            Kind = kind,
            AssetId = session.WithDocument((doc, _) => doc.ResolverIdentity is null ? null : kind),
            X = x,
            Z = z,
            Y = y,
            Yaw = yaw,
            Scale = scale,
            Tags = tags is null ? new List<string>() : new List<string>(tags),
        };

        MutationResult result = Apply(new AddPlacementCommand(placement), "placement_add",
            $"placed {kind} at ({x:F1}, {z:F1}) ground {groundY:F2}");
        return result with { GroundY = groundY, Id = placementId };
    }

    /// <summary>Moves a placement to a new XZ. When <paramref name="y"/> is provided it passes straight through.
    /// When <paramref name="y"/> is null and <paramref name="keepExplicitY"/> is true, the placement's current Y
    /// is preserved (the gizmo's drag policy). The default (<paramref name="y"/> null, flag false) forces a null
    /// Y, re-snapping to ground, matching the R-key behavior.</summary>
    public MutationResult PlacementMove(string id, float x, float z, float? y = null, bool keepExplicitY = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        float? newY = y;
        if (newY is null && keepExplicitY)
        {
            newY = session.WithDocument((doc, _) =>
                doc.Placements.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))?.Y);
        }

        return Apply(new MovePlacementCommand(id, x, z, newY), "placement_move",
            $"moved placement {id} to ({x:F1}, {z:F1})");
    }

    /// <summary>Sets a placement's yaw.</summary>
    public MutationResult PlacementRotate(string id, float yaw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Apply(new RotatePlacementCommand(id, yaw), "placement_rotate",
            $"rotated placement {id} to yaw {yaw:F2}");
    }

    /// <summary>Sets a placement's uniform scale.</summary>
    public MutationResult PlacementScale(string id, float scale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Apply(new ScalePlacementCommand(id, scale), "placement_scale",
            $"scaled placement {id} to {scale:F2}");
    }

    /// <summary>Renames an analytic placement ID, or edits a native display label without changing identity.
    /// Native stable-ID changes use PlacementRemapId explicitly.</summary>
    public MutationResult PlacementRename(string oldId, string newId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        MutationResult result = Apply(new RenamePlacementCommand(oldId, newId), "placement_rename",
            $"renamed placement {oldId} to {newId}");
        return result with { Id = session.WithDocument((doc, _) => doc.ResolverIdentity is null ? newId : oldId) };
    }

    /// <summary>Removes a placement by id.</summary>
    public MutationResult PlacementRemove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Apply(new RemovePlacementCommand(id), "placement_remove", $"removed placement {id}");
    }


    /// <summary>Sets a native placement display label without changing either identity.</summary>
    public MutationResult PlacementLabel(string placementId, string label) =>
        Apply(new SetPlacementLabelCommand(placementId, label), "placement_label", $"labelled placement {placementId}")
            with
        { Id = placementId };

    /// <summary>Explicitly remaps a native stable placement ID, preserving numeric identity.</summary>
    public MutationResult PlacementRemapId(string oldId, string newId) =>
        Apply(new RemapPlacementIdCommand(oldId, newId), "placement_remap_id", $"remapped placement {oldId} to {newId}")
            with
        { Id = newId };
    MutationResult DuplicatePlacement(string id)
    {
        string? newId = null;
        MutationResult result = Apply((doc, _) =>
        {
            MapPlacement source = doc.Placements.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"No placement with id '{id}' in the document.");
            newId = UniqueDuplicateName("placement",
                n => doc.Placements.Any(p => string.Equals(p.Id, n, StringComparison.Ordinal)));
            return new AddPlacementCommand(new MapPlacement
            {
                Id = newId,
                Kind = source.Kind,
                AssetId = source.AssetId,
                DisplayName = source.DisplayName,
                X = source.X + DuplicateOffset,
                Z = source.Z + DuplicateOffset,
                Y = source.Y,
                Yaw = source.Yaw,
                Scale = source.Scale,
                Tags = new List<string>(source.Tags),
            });
        }, "element_duplicate", _ => $"duplicated placement '{id}' as '{newId}'", worldChanged: false);
        return result with { Id = newId };
    }

}
