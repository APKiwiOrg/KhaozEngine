using System;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.MapEdit;

public sealed partial class MapEditSession
{
    long _snapshotVersion;
    // The foliage preview context keyed by the snapshot version it was captured at, dropped whenever the snapshot changes.
    (long Version, MapFoliageContextResult Context)? _foliagePreview;

    /// <summary>The version of the accepted session snapshot. It advances on every native publication, including undo
    /// and redo, and wherever the live document or its verified closure is replaced: open, create, save, window
    /// replacement, both conversions, retile and closure binding.</summary>
    internal long SnapshotVersion { get { lock (_lock) return _snapshotVersion; } }

    /// <summary>Whether a foliage preview context is cached, so a test can see a replaced snapshot drop it at once.</summary>
    internal bool HoldsFoliagePreview { get { lock (_lock) return _foliagePreview is not null; } }

    // Every caller holds the lock. The cached preview context belongs to the previous snapshot, so it is dropped here
    // rather than kept alive until the next preview.
    void NewSnapshotLocked()
    {
        _snapshotVersion++;
        _foliagePreview = null;
    }

    /// <summary>The foliage generation context of the current snapshot, captured once by <paramref name="capture"/> and
    /// reused until the snapshot version advances. A refused capture is cached the same way, since the same snapshot
    /// refuses the same way.</summary>
    internal MapFoliageContextResult FoliagePreviewContext(Func<MapFoliageContextResult> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (_lock)
        {
            RequireDocumentLocked();
            if (_foliagePreview is { } cached && cached.Version == _snapshotVersion) return cached.Context;
            MapFoliageContextResult context = capture();
            _foliagePreview = (_snapshotVersion, context);
            return context;
        }
    }
}
