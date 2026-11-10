using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Cache-owned immutable coverage compiled from exact verified attachment and displayed mesh bytes.</summary>
public sealed class LoadedEquipmentCoverage
{
    readonly AttachmentSetCache _owner;
    readonly LoadedAttachmentSet _set;
    readonly EquipmentCoverageDefinition _definition;
    readonly IReadOnlyList<CoverageSupportSurface> _supports;
    readonly object? _resourceOwner;
    readonly string _companionSha256;
    internal readonly int[] Parts;

    internal LoadedEquipmentCoverage(AttachmentSetCache owner, LoadedAttachmentSet set,
        EquipmentCoverageDefinition definition, CoverageSupportSurface[] supports, int[] parts, object? resourceOwner, string companionSha256)
    {
        _owner = owner; _set = set; _definition = definition;
        _supports = Array.AsReadOnly(supports); Parts = parts;
        _resourceOwner = resourceOwner; _companionSha256 = companionSha256;
    }

    /// <summary>SHA-256 of the exact verified companion bytes passed to GetCoverage.</summary>
    public string CompanionSha256 { get { _owner.ThrowIfDisposed(); return _companionSha256; } }

    internal CoverageConfigurationItem Configuration(BoundAttachmentSet set) =>
        new(_set.DefinitionSha256, CompanionSha256, set.ConfigurationParts);

    internal void RegisterLifetime(SkinnedHostCoverage host) => _owner.RegisterCoverageLifetime(host);
    internal void UnregisterLifetime(SkinnedHostCoverage host) => _owner.UnregisterCoverageLifetime(host);

    public EquipmentCoverageDefinition Definition
    { get { _owner.ThrowIfDisposed(); return _definition; } }
    /// <summary>Command-ordered immutable support surfaces. Sources are shared only within the owning cache.</summary>
    public IReadOnlyList<CoverageSupportSurface> Supports
    { get { _owner.ThrowIfDisposed(); return _supports; } }

    internal bool Matches(BoundAttachmentSet set, object resourceOwner)
    {
        _owner.ThrowIfDisposed();
        set.Owner.ThrowIfDisposed();
        return _resourceOwner is not null && ReferenceEquals(resourceOwner, _resourceOwner)
            && _owner.Scene is IAttachmentCoverageScene scene && ReferenceEquals(scene.CoverageResourceOwner, _resourceOwner)
            && ReferenceEquals(set.Owner, _owner)
            && ReferenceEquals(set.Definition, _set.Definition) && ReferenceEquals(set.Resources, _set.Resources);
    }
}

/// <summary>One stable equipped incarnation, its borrowed set and coverage, and current transition weight.</summary>
public readonly record struct CoveredAttachmentInstance(ulong InstanceId, BoundAttachmentSet Set,
    LoadedEquipmentCoverage Coverage, float TransitionWeight = 1);
