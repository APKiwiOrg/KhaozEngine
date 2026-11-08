using System;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Typed context kept independently of the prefab refusal message.</summary>
internal readonly record struct MapPrefabValidationSubject
{
    internal string? EntityId { get; init; }
    internal string? InstanceId { get; init; }
    internal string? PlacementId { get; init; }
    internal string? ResourceId { get; init; }
    internal string? WorldId { get; init; }
    internal string? SurfaceId { get; init; }
    internal MapPatchKey? Patch { get; init; }
    internal MapPrefabChildKey? Child { get; init; }
    internal MapPrefabChildKey? ExpectedChild { get; init; }
    internal MapPrefabChildKind? ChildKind { get; init; }
    internal MapPrefabChildKind? ExpectedChildKind { get; init; }
    internal MapPrefabAttachmentKind? AttachmentKind { get; init; }
    internal MapPrefabReferenceScope? ReferenceScope { get; init; }
    internal MapPrefabReferenceScope? ExpectedReferenceScope { get; init; }
    internal long? NumericId { get; init; }
    internal long? MinimumInteger { get; init; }
    internal string? FieldPath { get; init; }
    internal string? SubjectKind { get; init; }
    internal int? Index { get; init; }

    internal static MapPrefabValidationSubject FromKey<TKey>(TKey key, string what, int index) where TKey : notnull
    {
        MapPrefabValidationSubject subject = key switch
        {
            // Duplicate admission precedes the later required-text guard for null authored string IDs.
            null => new(),
            string id => new() { EntityId = id },
            MapPrefabChildKey child => new() { Child = child, ChildKind = child.Kind, EntityId = child.Key },
            MapPatchKey patch => new() { Patch = patch, SurfaceId = patch.SurfaceId },
            ValueTuple<string, MapPrefabChildKey> transfer => new()
                { InstanceId = transfer.Item1, Child = transfer.Item2, ChildKind = transfer.Item2.Kind, EntityId = transfer.Item2.Key },
            ValueTuple<string, MapPrefabAttachmentKind, string> attachment => new()
                { PlacementId = attachment.Item1, AttachmentKind = attachment.Item2, ResourceId = attachment.Item3 },
            ValueTuple<MapPrefabReferenceScope, MapPrefabChildKind, string> reference => new()
                { ReferenceScope = reference.Item1, ChildKind = reference.Item2, EntityId = reference.Item3 },
            ValueTuple<MapPrefabChildKind, string> owner => new()
                { ChildKind = owner.Item1, WorldId = owner.Item2, EntityId = owner.Item2 },
            // Unique has a closed set of key types. A new key type needs its mapping here.
            _ => throw new InvalidOperationException("Unsupported prefab validation key type."),
        };
        return subject with { SubjectKind = what, Index = index };
    }
}
