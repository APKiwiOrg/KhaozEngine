using System;
using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Persistence;

namespace KhaozEngine.MapEditor
{
    /// <summary>
    /// An ordered, capped store of recently-opened map document paths, most-recent first. The landing menu reads
    /// <see cref="Paths"/> to build its Open Recent list, calls <see cref="Touch"/> when a map is opened (from the
    /// menu or the head's boot), and <see cref="Remove"/> to prune a path that no longer resolves.
    /// </summary>
    public interface IRecentFilesStore
    {
        /// <summary>The recent paths, most-recent first (index 0 is the last <see cref="Touch"/>ed). Never null.</summary>
        IReadOnlyList<string> Paths { get; }

        /// <summary>Record <paramref name="path"/> as the most recent: de-duplicate it (ordinal), move it to the
        /// front, cap the list, and persist. A null or whitespace path is ignored.</summary>
        void Touch(string path);

        /// <summary>Drop <paramref name="path"/> from the list (ordinal compare) if present, then persist. Absent is
        /// a no-op.</summary>
        void Remove(string path);
    }

    /// <summary>The serialized shape of the recent-files list persisted through the settings seam. A plain mutable
    /// record so System.Text.Json round-trips it via its public property (see <see cref="EditorRecentFiles"/>).</summary>
    public sealed class RecentFilesRecord
    {
        /// <summary>The stored recent paths, most-recent first. Absolute map document paths.</summary>
        public List<string> Paths { get; set; } = new();
    }

    /// <summary>
    /// The canonical <see cref="IRecentFilesStore"/>: a most-recent-first list of at most <see cref="MaxPaths"/>
    /// map paths, persisted through the engine settings seam (<see cref="ISettingsStorage"/>) on every mutation.
    /// It rides its own <see cref="FileName"/> file so it never collides with a game's own <c>settings.json</c>.
    /// <para>Construct it with an already-built <see cref="ISettingsStorage"/> (the testable shape: a test injects a
    /// fake or a temp-rooted <see cref="FileSettingsStorage"/>), or with a publisher / app-name pair, which builds a
    /// publisher-rooted <see cref="GameStorage"/> internally. Writes go through that storage's coalesced write queue,
    /// so rapid touches collapse to one file write.</para>
    /// </summary>
    public sealed class EditorRecentFiles : IRecentFilesStore
    {
        /// <summary>The settings file the recents ride, kept distinct from a game's own <c>settings.json</c>.</summary>
        public const string FileName = "editor-recents.json";

        /// <summary>The maximum number of retained recent paths. Older entries fall off the end.</summary>
        public const int MaxPaths = 10;

        readonly ISettingsStorage _storage;
        readonly List<string> _paths = new();

        /// <summary>
        /// Wraps an already-built <paramref name="storage"/> (which it points at <see cref="FileName"/>) and loads the
        /// persisted list. This is the seam a test drives with a fake or temp-rooted storage.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
        public EditorRecentFiles(ISettingsStorage storage)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _storage.SettingsFileName = FileName;
            Load();
        }

        /// <summary>
        /// Convenience over-load for a head: builds a publisher-rooted <see cref="GameStorage"/> internally and rides
        /// its settings storage. Layout follows <see cref="AppDataPaths"/> (<c>&lt;os-base&gt;/&lt;publisher&gt;/&lt;appName&gt;/</c>),
        /// so the recents nest beside the game's own data.
        /// </summary>
        public EditorRecentFiles(string publisher, string appName)
            : this(new GameStorage(publisher, appName).Settings)
        {
        }

        /// <inheritdoc/>
        public IReadOnlyList<string> Paths => _paths;

        /// <inheritdoc/>
        public void Touch(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            _paths.RemoveAll(p => string.Equals(p, path, StringComparison.Ordinal));
            _paths.Insert(0, path);
            if (_paths.Count > MaxPaths) _paths.RemoveRange(MaxPaths, _paths.Count - MaxPaths);
            Save();
        }

        /// <inheritdoc/>
        public void Remove(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (_paths.RemoveAll(p => string.Equals(p, path, StringComparison.Ordinal)) > 0) Save();
        }

        // Load the persisted record and re-apply the store's own invariants (drop blanks, dedup ordinal, cap), so a
        // hand-edited or legacy file can never seed the in-memory list with a stale duplicate or over-length run.
        void Load()
        {
            RecentFilesRecord record = _storage.LoadSettings<RecentFilesRecord>();
            _paths.Clear();
            if (record?.Paths is not { } stored) return;
            foreach (string path in stored)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (_paths.Exists(p => string.Equals(p, path, StringComparison.Ordinal))) continue;
                _paths.Add(path);
                if (_paths.Count >= MaxPaths) break;
            }
        }

        // Persist a copy of the current list (the record must not alias _paths, or a later mutation would edit the
        // already-queued payload). The storage's write queue coalesces, so back-to-back Touches land as one write.
        void Save() => _storage.SaveSettings(new RecentFilesRecord { Paths = new List<string>(_paths) });
    }
}
