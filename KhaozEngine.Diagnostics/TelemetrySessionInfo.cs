using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace KhaozEngine.Diagnostics;

/// <summary>One name/value pair in a telemetry session header: an environment lever or a game durable.</summary>
public readonly record struct TelemetryHeaderValue(string Key, string Value);

/// <summary>
/// What a consumer hands <see cref="TelemetryRecorder.Start(string, TelemetrySessionInfo)"/> so the recording
/// can identify itself: the app's own build identity, the GPU facts the engine resolved at device creation, and
/// any number of game-owned durable values. Everything here is optional and everything here is plain data.
/// <para>
/// The GPU fields are strings and nullable bools rather than the <c>KhaozEngine.Gpu</c> types on purpose: this
/// package sits UNDER the GPU stack (<c>KhaozEngine.Gpu</c> references it, not the reverse), so naming those
/// types here would be a cycle. Fill them with one call to the <c>WithGpu</c> extension in
/// <c>KhaozEngine.Gpu.GpuTelemetry</c>, which does the mapping in the package that owns the enums.
/// </para>
/// <para>
/// The engine version and the <c>KE_</c> environment levers are NOT here: the engine reads both itself when the
/// header is written, so a consumer cannot get them stale or wrong.
/// </para>
/// </summary>
public sealed class TelemetrySessionInfo
{
    readonly List<TelemetryHeaderValue> _game = new();
    readonly ReadOnlyCollection<TelemetryHeaderValue> _gameView;

    /// <summary>Create an empty session info. Every field on it is optional.</summary>
    public TelemetrySessionInfo() => _gameView = _game.AsReadOnly();

    /// <summary>The consuming app's name, e.g. <c>"Ruinborne"</c>. Null or blank omits it.</summary>
    public string? AppName { get; set; }

    /// <summary>The consuming app's build/display version, e.g. <c>"0.7.3"</c>. Null or blank omits it.</summary>
    public string? AppVersion { get; set; }

    /// <summary>
    /// The consuming app's build name: the fleet convention is the minor-series codename carried in the game's
    /// <c>&lt;BuildName&gt;</c>, e.g. <c>"Sundered Ground"</c>. Null or blank omits it.
    /// </summary>
    public string? BuildName { get; set; }

    /// <summary>The graphics backend that actually ran, as a name (<c>"Metal"</c>, <c>"Direct3D11"</c>, ...).</summary>
    public string? GpuBackend { get; set; }

    /// <summary>
    /// Where that backend choice came from, as a name (<c>"OsProbe"</c>, <c>"EnvironmentOverride"</c>,
    /// <c>"UserPreference"</c>, <c>"FallbackAfterFailure"</c>, ...). The provenance is what separates a
    /// deliberate backend from one the engine fell back to, so a capture is readable without guessing.
    /// </summary>
    public string? GpuBackendSource { get; set; }

    /// <summary>
    /// The backend that was ASKED for and did not work, as a name, set only on a fallback. Null otherwise.
    /// Paired with <see cref="GpuBackend"/>, which is what actually ran, this is the half that says WHAT failed,
    /// and without it a fallback capture cannot answer the first question anyone asks of it. It matters most on
    /// a <c>UserPreference</c> fallback, where the request came from the player's own in-game graphics setting
    /// and is recoverable nowhere else in the capture.
    /// </summary>
    public string? GpuRequestedBackend { get; set; }

    /// <summary>
    /// The RAW <c>KE_GRAPHICS_BACKEND</c> value exactly as it was read, or null when no non-blank override was
    /// present. Deliberately not normalized and written verbatim: the untouched string is what makes a typo
    /// (<c>vulcan</c>) or stray quoting obvious, and it is the whole diagnostic behind an
    /// <c>UnrecognizedOverride</c> source.
    /// </summary>
    public string? GpuRequestedOverride { get; set; }

    /// <summary>The adapter the device ran on (on Direct3D11 exactly the DXGI adapter description).</summary>
    public string? AdapterDescription { get; set; }

    /// <summary>
    /// Whether that adapter is a SOFTWARE rasterizer (on Direct3D11, <c>DXGI_ADAPTER_FLAG_SOFTWARE</c>). Null when
    /// nothing answered the question, which is every backend that does not report it and every session where the
    /// query failed.
    /// <para>
    /// It is a separate field from <see cref="AdapterDescription"/> rather than something a reader infers from the
    /// name, because inferring it means keeping a list of the names software rasterizers use, and that list is
    /// wrong the first time a new one appears. It matters most for a PERFORMANCE capture: numbers off WARP or the
    /// Microsoft Basic Render Driver are not comparable with numbers off a GPU at all, and a capture that does not
    /// say which it was is a capture that gets averaged in with the others.
    /// </para>
    /// </summary>
    public bool? SoftwareAdapter { get; set; }

    /// <summary>
    /// Why the graphics device was LOST, when it was, or null for the ordinary session where it was not. On
    /// Direct3D11 this is <c>GetDeviceRemovedReason</c>'s answer, read at the first call site that noticed the
    /// removal and carried here as a stable token plus the site.
    /// <para>
    /// Read at the fault site because <c>DXGI_ERROR_DEVICE_REMOVED</c> is STICKY: every later call returns it too,
    /// so by the time a crash handler asks, the reason has been overwritten by whatever ran next. That is not
    /// hypothetical. All 25 stacks on the incident this field exists for pointed at a texture view constructor
    /// that was merely the next call made, and reconstructing what actually happened cost a full investigation.
    /// </para>
    /// </summary>
    public string? DeviceLossReason { get; set; }

    /// <summary>
    /// Known third-party overlay / capture software found hooked into the process, or null when nothing was
    /// scanned. Null and empty are OPPOSITE facts and the header keeps them apart: null means the scan never
    /// ran (not Windows, or it failed), an empty list means it ran and the process was clean.
    /// </summary>
    public IReadOnlyList<string>? InjectedModules { get; set; }

    /// <summary>
    /// <c>D3D11_FEATURE_DATA_THREADING.DriverCommandLists</c>, on Direct3D11 only. Null everywhere else. False
    /// is the pathological case: the runtime is emulating command lists in software.
    /// </summary>
    public bool? DriverCommandLists { get; set; }

    /// <summary><c>D3D11_FEATURE_DATA_THREADING.DriverConcurrentCreates</c>, on Direct3D11 only. Null elsewhere.</summary>
    public bool? DriverConcurrentCreates { get; set; }

    /// <summary>True when either threading field was filled, so the header writes a threading object.</summary>
    public bool HasThreadingCaps => DriverCommandLists.HasValue || DriverConcurrentCreates.HasValue;

    /// <summary>
    /// The game-owned durable values, in the order they were added. These land under the header's own
    /// <c>game</c> section, so nothing a game records can collide with an engine field.
    /// <para>
    /// A genuinely read-only view rather than the live list behind an interface, so a caller that casts it back
    /// to <see cref="IList{T}"/> gets a <see cref="NotSupportedException"/> instead of a silent back door into
    /// the recorded set. It still tracks later <see cref="AddGameValue"/> calls, since it wraps the same list.
    /// </para>
    /// </summary>
    public IReadOnlyList<TelemetryHeaderValue> GameValues => _gameView;

    /// <summary>
    /// Record one game-owned durable value. A repeated key replaces the earlier value in place rather than
    /// writing the key twice, so the header stays a well-formed object. A null or blank key is ignored and a
    /// null value is recorded as empty, because a header must never fail a recording that was going fine.
    /// </summary>
    /// <returns>This instance, so calls chain.</returns>
    public TelemetrySessionInfo AddGameValue(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key)) return this;

        string text = value ?? string.Empty;
        for (int i = 0; i < _game.Count; i++)
        {
            if (string.Equals(_game[i].Key, key, StringComparison.Ordinal))
            {
                _game[i] = new TelemetryHeaderValue(key, text);
                return this;
            }
        }

        _game.Add(new TelemetryHeaderValue(key, text));
        return this;
    }

    /// <summary>
    /// Record every pair in <paramref name="values"/> via <see cref="AddGameValue"/>. This is the one-call dump
    /// a game uses for its F1-overlay durables: hand it a <c>Dictionary&lt;string, string&gt;</c>, or project
    /// the overlay's own rows (<c>rows.Select(r =&gt; new KeyValuePair&lt;string, string&gt;(r.Label, r.Value))</c>).
    /// </summary>
    /// <returns>This instance, so calls chain.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    public TelemetrySessionInfo AddGameValues(IEnumerable<KeyValuePair<string, string>> values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));

        foreach (KeyValuePair<string, string> pair in values) AddGameValue(pair.Key, pair.Value);
        return this;
    }
}
