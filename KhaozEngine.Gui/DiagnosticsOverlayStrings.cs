using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.App;

namespace KhaozEngine.Gui;

/// <summary>
/// The engine-owned localization keys for the player-facing text <see cref="DiagnosticsHud"/> draws, plus the
/// built-in English fallback (<see cref="EnglishDefaults"/>). A game localizes it by adding these
/// <c>diagnostics.overlay.*</c> keys to its own catalog and wiring it as <see cref="LocalizationContext.Catalog"/>.
/// <see cref="Resolve"/> falls back to the English here whenever a key is absent or no catalog is wired, mirroring
/// <see cref="PatchNotesStrings"/> and <see cref="UpdateOverlayStrings"/>.
/// </summary>
public static class DiagnosticsOverlayStrings
{
    /// <summary>The title of the built-in Performance section. No arguments.</summary>
    public static readonly StringId PerformanceTitle = new("diagnostics.overlay.performance.title");

    /// <summary>The frames-per-second row label. No arguments.</summary>
    public static readonly StringId PerformanceFpsLabel = new("diagnostics.overlay.performance.fps");

    /// <summary>The frame-time row label. No arguments.</summary>
    public static readonly StringId PerformanceFrameMsLabel = new("diagnostics.overlay.performance.frame-ms");

    /// <summary>The managed-memory row label. No arguments.</summary>
    public static readonly StringId PerformanceManagedMbLabel = new("diagnostics.overlay.performance.managed-mb");

    /// <summary>The title of the built-in Pass timings section. No arguments.</summary>
    public static readonly StringId PassTimingsTitle = new("diagnostics.overlay.pass-timings.title");

    /// <summary>The title of the built-in Draw stats section. No arguments.</summary>
    public static readonly StringId DrawStatsTitle = new("diagnostics.overlay.draw-stats.title");

    /// <summary>The draw-call row label. No arguments.</summary>
    public static readonly StringId DrawCallsLabel = new("diagnostics.overlay.draw-stats.draw-calls");

    /// <summary>The instance-count row label. No arguments.</summary>
    public static readonly StringId InstancesLabel = new("diagnostics.overlay.draw-stats.instances");

    /// <summary>The triangle-count row label. No arguments.</summary>
    public static readonly StringId TrianglesLabel = new("diagnostics.overlay.draw-stats.triangles");

    /// <summary>The quad-count row label. No arguments.</summary>
    public static readonly StringId QuadsLabel = new("diagnostics.overlay.draw-stats.quads");

    /// <summary>The batch-flush row label. No arguments.</summary>
    public static readonly StringId FlushesLabel = new("diagnostics.overlay.draw-stats.flushes");

    /// <summary>The texture-switch row label. No arguments.</summary>
    public static readonly StringId TextureSwitchesLabel = new("diagnostics.overlay.draw-stats.texture-switches");

    /// <summary>The total buffer-upload row label. No arguments.</summary>
    public static readonly StringId UploadKbLabel = new("diagnostics.overlay.draw-stats.upload-kb");

    /// <summary>The indented instance-upload row label. No arguments.</summary>
    public static readonly StringId InstanceUploadKbLabel = new("diagnostics.overlay.draw-stats.instances-kb");

    /// <summary>The indented CPU-skinned upload row label. No arguments.</summary>
    public static readonly StringId SkinnedUploadKbLabel = new("diagnostics.overlay.draw-stats.skinned-kb");

    /// <summary>The indented skinning-uniform upload row label. No arguments.</summary>
    public static readonly StringId SkinUboUploadKbLabel = new("diagnostics.overlay.draw-stats.skin-ubo-kb");

    /// <summary>The indented sprite-upload row label. No arguments.</summary>
    public static readonly StringId SpriteUploadKbLabel = new("diagnostics.overlay.draw-stats.sprites-kb");

    /// <summary>The title of the built-in Network section. No arguments.</summary>
    public static readonly StringId NetworkTitle = new("diagnostics.overlay.network.title");

    /// <summary>The disconnected-status row label. No arguments.</summary>
    public static readonly StringId NetworkStatusLabel = new("diagnostics.overlay.network.status");

    /// <summary>The value shown when the network source is disconnected. No arguments.</summary>
    public static readonly StringId NetworkNotConnected = new("diagnostics.overlay.network.not-connected");

    /// <summary>The round-trip latency row label. No arguments.</summary>
    public static readonly StringId NetworkPingLabel = new("diagnostics.overlay.network.ping");

    /// <summary>The packet-loss row label. No arguments.</summary>
    public static readonly StringId NetworkLossLabel = new("diagnostics.overlay.network.loss");

    /// <summary>The incoming and outgoing bandwidth row label. No arguments.</summary>
    public static readonly StringId NetworkInOutLabel = new("diagnostics.overlay.network.in-out");

    /// <summary>The snapshot-rate row label. No arguments.</summary>
    public static readonly StringId NetworkSnapshotsLabel = new("diagnostics.overlay.network.snapshots");

    /// <summary>The reconciliation-correction row label. No arguments.</summary>
    public static readonly StringId NetworkCorrectionLabel = new("diagnostics.overlay.network.correction");

    /// <summary>The title of the built-in Build section, which names the running app and its version. No arguments.</summary>
    public static readonly StringId BuildTitle = new("diagnostics.overlay.build.title");

    /// <summary>
    /// Resolves <paramref name="id"/> through the ambient <see cref="LocalizationContext.Catalog"/> when it is
    /// wired AND carries the key, otherwise against <see cref="EnglishDefaults"/>. Never throws and never shows a
    /// raw key for any id declared on this type.
    /// </summary>
    public static string Resolve(StringId id)
    {
        IStringCatalog catalog = LocalizationContext.Catalog is { } c && c.TryGet(id.Key, out _)
            ? c
            : EnglishDefaults;
        return catalog.Get(id.Key);
    }

    /// <summary>
    /// The built-in English default strings, keyed by the ids above. This is the exact text <see cref="Resolve"/>
    /// returns when no wired catalog resolves a key. A game normally provides its own catalog rather than reading
    /// this directly.
    /// </summary>
    public static IStringCatalog EnglishDefaults { get; } = new EnglishDefaultCatalog();

    private sealed class EnglishDefaultCatalog : IStringCatalog
    {
        internal static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["diagnostics.overlay.performance.title"] = "Performance",
            ["diagnostics.overlay.performance.fps"] = "fps",
            ["diagnostics.overlay.performance.frame-ms"] = "frame ms",
            ["diagnostics.overlay.performance.managed-mb"] = "managed MB",
            ["diagnostics.overlay.pass-timings.title"] = "Pass timings",
            ["diagnostics.overlay.draw-stats.title"] = "Draw stats",
            ["diagnostics.overlay.draw-stats.draw-calls"] = "draw calls",
            ["diagnostics.overlay.draw-stats.instances"] = "instances",
            ["diagnostics.overlay.draw-stats.triangles"] = "triangles",
            ["diagnostics.overlay.draw-stats.quads"] = "quads",
            ["diagnostics.overlay.draw-stats.flushes"] = "flushes",
            ["diagnostics.overlay.draw-stats.texture-switches"] = "tex switches",
            ["diagnostics.overlay.draw-stats.upload-kb"] = "upload KB",
            ["diagnostics.overlay.draw-stats.instances-kb"] = "  instances KB",
            ["diagnostics.overlay.draw-stats.skinned-kb"] = "  skinned KB",
            ["diagnostics.overlay.draw-stats.skin-ubo-kb"] = "  skin ubo KB",
            ["diagnostics.overlay.draw-stats.sprites-kb"] = "  sprites KB",
            ["diagnostics.overlay.network.title"] = "Network",
            ["diagnostics.overlay.network.status"] = "status",
            ["diagnostics.overlay.network.not-connected"] = "not connected",
            ["diagnostics.overlay.network.ping"] = "ping",
            ["diagnostics.overlay.network.loss"] = "loss",
            ["diagnostics.overlay.network.in-out"] = "in/out",
            ["diagnostics.overlay.network.snapshots"] = "snapshots",
            ["diagnostics.overlay.network.correction"] = "correction",
            ["diagnostics.overlay.build.title"] = "Build",
        };

        public string Get(string key) => Map.TryGetValue(key, out string? v) ? v : key;

        public string Format(string key, params object?[] args)
            => IStringCatalog.SafeFormat(CultureInfo.InvariantCulture, Get(key), args);

        public bool TryGet(string key, out string value)
        {
            if (Map.TryGetValue(key, out string? v)) { value = v; return true; }
            value = key;
            return false;
        }
    }
}
