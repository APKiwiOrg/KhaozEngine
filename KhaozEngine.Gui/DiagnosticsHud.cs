using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// Turn-key wiring of the frame-cost HUD: bundles a <see cref="FrameStats"/> FPS/frame-ms meter, an optional
    /// <see cref="PassTimings"/> per-pass meter (3D hosts), and a <see cref="DiagnosticsOverlay"/> behind one
    /// object, and registers a throttled sections provider that assembles the Performance, Draw-stats,
    /// Pass-timings, and (optional) Network sections. A host calls <see cref="Update"/> once per frame (samples FPS,
    /// handles the toggle key + fade), feeds it the aggregated <see cref="SetDrawStats"/> and (3D) samples
    /// <see cref="PassTimings"/> from its scene, then <see cref="Draw"/>s it in a 2D pass. Default hidden, toggled by
    /// the overlay theme's key (F1). While hidden the provider short-circuits to no sections, so the only per-frame
    /// cost is the always-on counter increments in the render surfaces themselves.
    /// <para>
    /// It lives in <c>KhaozEngine.Gui</c> and never references the 3D renderer: a 3D host owns the coupling to its
    /// scene (setting <c>Scene3D.EnableTiming</c> while <see cref="Visible"/> and sampling this HUD's
    /// <see cref="PassTimings"/> meter from <c>Scene3D.PassTimingsMs</c>). Reusable outside <c>GameApp</c>: any Gui
    /// layer can construct one.
    /// </para>
    /// </summary>
    public sealed class DiagnosticsHud
    {
        readonly DiagnosticsOverlay _overlay;
        readonly FrameStats _frameStats;
        readonly PassTimings? _passTimings;
        readonly List<OverlaySection> _buf = new(4);

        RenderFrameStats _drawStats;
        Func<ClientNetStats?>? _netStatsSource;

        /// <summary>
        /// Build a HUD around <paramref name="theme"/> (its <see cref="DiagnosticsOverlayTheme.ToggleKey"/> is the
        /// show/hide key). <paramref name="withPassTimings"/> allocates the per-pass meter for a 3D host (a 2D host
        /// passes false, so no Pass-timings section shows). <paramref name="refreshSeconds"/> throttles the sections
        /// rebuild (default 0.25s) so per-frame values do not strobe unreadably.
        /// </summary>
        public DiagnosticsHud(DiagnosticsOverlayTheme theme, bool withPassTimings, float refreshSeconds = 0.25f)
        {
            _overlay = new DiagnosticsOverlay(theme ?? throw new ArgumentNullException(nameof(theme)));
            _frameStats = new FrameStats();
            _passTimings = withPassTimings ? new PassTimings() : null;
            _overlay.SetSectionsProvider(BuildSections, refreshSeconds);
        }

        /// <summary>The wrapped overlay (theme, fade, bounds). Use it for placement or a manual <see cref="DiagnosticsOverlay.Toggle"/>.</summary>
        public DiagnosticsOverlay Overlay => _overlay;

        /// <summary>Whether the panel is currently shown. A 3D host gates <c>Scene3D.EnableTiming</c> on this so pass timing costs nothing while hidden.</summary>
        public bool Visible => _overlay.Visible;

        /// <summary>The per-pass CPU-encode meter, or null for a 2D-only HUD. A 3D host feeds it each frame from
        /// its <c>Scene3D.PassTimingsMs</c> (e.g. <c>Sample("model", scene.PassTimingsMs.ModelMs)</c>).</summary>
        public PassTimings? PassTimings => _passTimings;

        /// <summary>The FPS / frame-ms / managed-heap meter, sampled by <see cref="Update"/> from the raw frame delta.</summary>
        public FrameStats FrameStats => _frameStats;

        /// <summary>
        /// Register a source of network stats: when it returns a non-null <see cref="ClientNetStats"/> the HUD adds
        /// a "Network" section. Pass null to remove it. Handy for a game whose active screen may or may not be
        /// networked (return null off the network, the connected stats on it).
        /// </summary>
        public void SetNetStatsSource(Func<ClientNetStats?>? source) => _netStatsSource = source;

        /// <summary>
        /// Sample the FPS meter from <paramref name="rawDt"/> (the unscaled frame delta), then process the toggle
        /// key and advance the fade. Call once per frame BEFORE the world render, so a 3D host can gate this frame's
        /// pass timing on the returned visibility. Returns <see cref="Visible"/>.
        /// </summary>
        public bool Update(InputState input, float rawDt)
        {
            _frameStats.Sample(rawDt);
            return _overlay.Update(input, rawDt);
        }

        /// <summary>Set this frame's whole-frame aggregated draw stats (e.g. the 3D scene's plus the 2D batch's),
        /// read by the throttled sections provider for the Draw-stats section.</summary>
        public void SetDrawStats(in RenderFrameStats stats) => _drawStats = stats;

        /// <summary>Draw the panel through <paramref name="batch"/> (its <c>Begin</c> must already be active), using
        /// <paramref name="font"/> + a 1x1 <paramref name="white"/> texture, anchored within <paramref name="viewport"/>.
        /// No-op while hidden / faded out.</summary>
        public void Draw(SpriteBatch batch, SpriteFont font, Texture2D white, Rect viewport) =>
            _overlay.Draw(batch, font, white, viewport);

        // Throttled provider: skip all section-building while hidden and fully faded (zero overhead beyond the
        // counter increments). Otherwise assemble Performance + Draw stats + (3D) Pass timings + (optional) Network.
        IReadOnlyList<OverlaySection> BuildSections()
        {
            if (!_overlay.Visible && _overlay.Alpha <= 0f) return Array.Empty<OverlaySection>();

            _buf.Clear();
            _buf.Add(DiagnosticsOverlay.PerformanceSection(_frameStats));
            _buf.Add(DiagnosticsOverlay.DrawStatsSection(_drawStats));
            if (_passTimings is { } pt && pt.PassNames.Count > 0)
                _buf.Add(DiagnosticsOverlay.PassTimingsSection(pt));
            if (_netStatsSource?.Invoke() is { } net)
                _buf.Add(DiagnosticsOverlay.NetworkSection(net));
            return _buf;
        }
    }
}
