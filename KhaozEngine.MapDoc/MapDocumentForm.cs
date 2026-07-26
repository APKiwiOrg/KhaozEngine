using System;

namespace KhaozEngine.MapDoc;

/// <summary>Which on-disk form a document path holds. <see cref="None"/> means nothing exists at the path, so
/// the path has NO form and one must be chosen explicitly. It is a real member rather than a null return
/// because "there is nothing here" is the case both of the format's known data-loss paths walked into.</summary>
public enum MapDocumentForm
{
    /// <summary>Nothing exists at the path.</summary>
    None,

    /// <summary>A single JSON file holding the whole document.</summary>
    Monolithic,

    /// <summary>A directory holding <c>map.json</c> plus content-addressed tile files.</summary>
    Tiled,
}

/// <summary>How hard a save works to survive a crash. See <see cref="MapDocumentFile.SaveTiled"/>.</summary>
public enum MapSaveDurability
{
    /// <summary>Crash-consistent: defends against a process kill, an unhandled exception or an editor crash,
    /// which is what actually happens on a dev box and where the real durability story is the git commit that
    /// follows. It does NOT defend against a power loss, because a rename orders the directory entry and not
    /// the file contents, so a renamed-in tile can come back with stale or zeroed blocks.</summary>
    Fast,

    /// <summary>Power-fail-consistent as far as the platform allows: every tile file and the manifest temp
    /// file is flushed to disk before its rename, and the containing directories are fsynced afterwards on
    /// the platforms that have that primitive. Linux and macOS support a directory fsync. Windows has no
    /// equivalent and NTFS orders metadata through its own journal instead, so there the level is per-file
    /// flush plus that caveat rather than a stronger guarantee dressed up as one.</summary>
    PowerFail,
}

/// <summary>Write-side knobs for the tiled save path. Separate from <see cref="MapDocRegistry"/> because it is
/// policy, not content.</summary>
public sealed class MapDocumentSaveOptions
{
    /// <summary>Default <see cref="MapSaveDurability.Fast"/>.</summary>
    public MapSaveDurability Durability { get; set; } = MapSaveDurability.Fast;

    /// <summary>Optional sink for the post-commit sweep's best-effort delete failures. The sweep runs AFTER
    /// the manifest rename, so a delete failure leaves inert garbage and never a broken document: it is
    /// reported here and does not throw. Null (the default) is silent, and
    /// <see cref="MapDocumentFile.VerifyTiled"/> remains the durable report for whatever the sweep left
    /// behind.</summary>
    public Action<string>? OnSweepFailure { get; set; }

    /// <summary>Test seam: invoked at each named step of the tiled write so a test can abort the writer
    /// mid-save without killing a process. Never set by shipping code.</summary>
    internal Action<MapTiledSaveStep>? OnStep { get; set; }
}

/// <summary>The named steps of <see cref="MapDocumentFile.SaveTiled"/>, for the crash-consistency tests'
/// injectable failure point.</summary>
internal enum MapTiledSaveStep
{
    /// <summary>Before each changed tile's temp file is written.</summary>
    BeforeTileWrite,

    /// <summary>After each changed tile's temp file has been renamed into place.</summary>
    AfterTileWrite,

    /// <summary>After <c>map.json.tmp</c> is complete and before the commit rename.</summary>
    BeforeManifestRename,

    /// <summary>After the commit rename and before the sweep.</summary>
    AfterManifestRename,

    /// <summary>Inside the post-commit sweep.</summary>
    DuringSweep,
}
