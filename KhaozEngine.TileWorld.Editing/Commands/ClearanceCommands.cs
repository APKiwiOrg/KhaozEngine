using System;

namespace KhaozEngine.TileWorld.Editing;

/// <summary>Replaces the world's whole clearance declaration as one reversible edit. The settings are immutable, so
/// every clearance verb builds the next settings and lands through this one command. Clearance is authoring metadata
/// that nothing draws or collides with, so the command reports no dirty rects.</summary>
public sealed class SetClearanceCommand : TileCommandBase
{
    readonly TileClearanceSettings? _settings;
    readonly TileClearanceSettings? _old;

    /// <summary>Captures the current settings so <see cref="Revert"/> restores them. Null
    /// <paramref name="settings"/> removes the block.</summary>
    public SetClearanceCommand(TileWorldDocument doc, TileClearanceSettings? settings)
        : base("Set clearance")
    {
        ArgumentNullException.ThrowIfNull(doc);
        _settings = settings;
        _old = doc.Clearance;
    }

    /// <inheritdoc/>
    public override void Apply(TileWorldDocument doc) => doc.SetClearance(_settings);

    /// <inheritdoc/>
    public override void Revert(TileWorldDocument doc) => doc.SetClearance(_old);
}
