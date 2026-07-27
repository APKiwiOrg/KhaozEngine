using System;
using System.Collections.Generic;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapEditor;

/// <summary>
/// The editor's own view preferences, persisted across sessions in <see cref="EditorSettingsStore.FileName"/>:
/// render distance, the sky / lighting look, and the ocean look. None of it touches the map document, so two
/// operators can prefer different horizons and skies over the same world.
/// <para>A plain mutable class with public properties so System.Text.Json round-trips it through the settings
/// seam (the <see cref="RecentFilesRecord"/> shape). The enums serialize as their numeric values, which is why
/// <see cref="Sanitize"/> exists: a hand-edited or truncated file must never crash the editor, so every value is
/// range-checked back into a usable one at load.</para>
/// <para>The slider defaults are DERIVED from the default presets rather than typed out again: a fresh settings
/// file shows exactly the <see cref="EnvironmentPresetKind.Day"/> sky and the <see cref="OceanPresetKind.Moderate"/>
/// sea, and it stays that way if a preset is ever retuned.</para>
/// </summary>
public sealed class EditorSettings
{
    /// <summary>The render-distance multipliers the settings menu offers, applied to
    /// <see cref="MapEditorOptions.RenderDistance"/> via <see cref="Terrain.RenderDistanceProfile.Scaled"/>.
    /// <c>1</c> leaves the head's configured profile untouched.</summary>
    public static readonly IReadOnlyList<float> RenderDistanceMultipliers = new[] { 1f, 2f, 4f };

    /// <summary>Lowest sun elevation the menu allows. Not zero: a sun exactly on the horizon degenerates the key
    /// light into a grazing direction that lights nothing.</summary>
    public const float MinSunElevationDegrees = 2f;

    /// <summary>Highest sun elevation the menu allows (straight overhead).</summary>
    public const float MaxSunElevationDegrees = 90f;

    /// <summary>Highest light-intensity multiplier the menu allows. 1 is the preset's own value, so the range runs
    /// from fully dark to twice the preset.</summary>
    public const float MaxLightIntensity = 2f;

    /// <summary>Highest swell amplitude (m) the menu allows, comfortably past the Rough preset.</summary>
    public const float MaxSwellAmplitude = 3f;

    /// <summary>Highest foam strength the menu allows.</summary>
    public const float MaxFoamStrength = 2f;

    /// <summary>The default sky and lighting bundle: the map editor opens under a day sky, which is what stops the
    /// engine's default starfield background from showing through behind the terrain.</summary>
    public const EnvironmentPresetKind DefaultEnvironment = EnvironmentPresetKind.Day;

    /// <summary>The default ocean bundle.</summary>
    public const OceanPresetKind DefaultOcean = OceanPresetKind.Moderate;

    /// <summary>Builds the defaults: base render distance, the <see cref="DefaultEnvironment"/> sky with its own sun
    /// angles, unscaled lighting, and the <see cref="DefaultOcean"/> sea with its own swell and foam.</summary>
    public EditorSettings()
    {
        (float azimuth, float elevation) = MapEditorEnvironment.SunAnglesOf(DefaultEnvironment);
        SunAzimuthDegrees = azimuth;
        SunElevationDegrees = elevation;
        (float swell, float foam) = MapEditorEnvironment.OceanValuesOf(DefaultOcean);
        SwellAmplitude = swell;
        FoamStrength = foam;
    }

    /// <summary>Scale applied to <see cref="MapEditorOptions.RenderDistance"/> as one coherent set. One of
    /// <see cref="RenderDistanceMultipliers"/>.</summary>
    public float RenderDistanceMultiplier { get; set; } = 1f;

    /// <summary>The sky + lighting bundle applied to the host scene's post settings.</summary>
    public EnvironmentPresetKind Environment { get; set; } = DefaultEnvironment;

    /// <summary>Sun compass azimuth in degrees, clockwise from north. Overrides the preset's own sun direction.</summary>
    public float SunAzimuthDegrees { get; set; }

    /// <summary>Sun elevation above the horizon in degrees
    /// (<see cref="MinSunElevationDegrees"/>..<see cref="MaxSunElevationDegrees"/>).</summary>
    public float SunElevationDegrees { get; set; }

    /// <summary>Multiplier on the preset's key-light colour. 1 is the preset value, 0 is off.</summary>
    public float KeyLightIntensity { get; set; } = 1f;

    /// <summary>Multiplier on the preset's ambient colour. 1 is the preset value, 0 is off.</summary>
    public float AmbientIntensity { get; set; } = 1f;

    /// <summary>The ocean-surface bundle applied to the host scene's water settings.</summary>
    public OceanPresetKind Ocean { get; set; } = DefaultOcean;

    /// <summary>Gerstner swell amplitude in metres. Overrides the ocean preset's own value.</summary>
    public float SwellAmplitude { get; set; }

    /// <summary>Whitecap foam strength. Overrides the ocean preset's own value.</summary>
    public float FoamStrength { get; set; }

    /// <summary>When true the editor builds a depth field from the document's own terrain and hands it to the water
    /// renderer, so waves shoal and break along the shoreline. Off by default: the depth field costs one pass over
    /// the document bounds on every world rebuild, which is not worth paying unless the shoreline is what is being
    /// authored.</summary>
    public bool Surf { get; set; }

    /// <summary>An independent copy, so a persisted payload never aliases the live instance the settings menu is
    /// still editing.</summary>
    public EditorSettings Clone()
    {
        var copy = new EditorSettings();
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>Overwrites every value from <paramref name="other"/>, leaving this instance's identity intact (the
    /// menu rows and the environment apply both hold this reference, so Reset must not swap the object).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    public void CopyFrom(EditorSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        RenderDistanceMultiplier = other.RenderDistanceMultiplier;
        Environment = other.Environment;
        SunAzimuthDegrees = other.SunAzimuthDegrees;
        SunElevationDegrees = other.SunElevationDegrees;
        KeyLightIntensity = other.KeyLightIntensity;
        AmbientIntensity = other.AmbientIntensity;
        Ocean = other.Ocean;
        SwellAmplitude = other.SwellAmplitude;
        FoamStrength = other.FoamStrength;
        Surf = other.Surf;
    }

    /// <summary>Restores every value to its default (the menu's Reset action).</summary>
    public void ResetToDefaults() => CopyFrom(new EditorSettings());

    /// <summary>Re-points the sky section at <paramref name="kind"/>: the preset itself plus its own sun angles and
    /// unscaled light intensities, so picking a preset shows that preset rather than the previous preset's sliders
    /// carried over onto a new palette. The sliders then adjust from there.</summary>
    public void SelectEnvironment(EnvironmentPresetKind kind)
    {
        Environment = kind;
        (SunAzimuthDegrees, SunElevationDegrees) = MapEditorEnvironment.SunAnglesOf(kind);
        KeyLightIntensity = 1f;
        AmbientIntensity = 1f;
    }

    /// <summary>Re-points the ocean section at <paramref name="kind"/>: the preset plus its own swell and foam, the
    /// sky-section rule in <see cref="SelectEnvironment"/> applied to the sea.</summary>
    public void SelectOcean(OceanPresetKind kind)
    {
        Ocean = kind;
        (SwellAmplitude, FoamStrength) = MapEditorEnvironment.OceanValuesOf(kind);
    }

    /// <summary>Forces every value back into a usable range, in place. Run on load, so a hand-edited, truncated, or
    /// version-skewed file can only ever produce a duller editor, never a crash or a black viewport: an unknown enum
    /// falls back to its default preset, a non-finite number falls back to the default value, and everything else
    /// clamps. The render-distance multiplier snaps to the nearest offered tier rather than clamping, since an
    /// in-between value would leave the menu showing a tier the profile is not actually using.</summary>
    public void Sanitize()
    {
        RenderDistanceMultiplier = NearestMultiplier(RenderDistanceMultiplier);
        if (!Enum.IsDefined(Environment)) Environment = DefaultEnvironment;
        if (!Enum.IsDefined(Ocean)) Ocean = DefaultOcean;

        // A non-finite slider value falls back to the value the (now known-good) preset itself carries, so the
        // recovered settings still describe that preset rather than an arbitrary constant. The two intensities are
        // multipliers, whose "preset value" is 1 by definition.
        (float presetAzimuth, float presetElevation) = MapEditorEnvironment.SunAnglesOf(Environment);
        (float presetSwell, float presetFoam) = MapEditorEnvironment.OceanValuesOf(Ocean);

        SunAzimuthDegrees = Clamp(SunAzimuthDegrees, 0f, 360f, presetAzimuth);
        SunElevationDegrees = Clamp(SunElevationDegrees, MinSunElevationDegrees, MaxSunElevationDegrees, presetElevation);
        KeyLightIntensity = Clamp(KeyLightIntensity, 0f, MaxLightIntensity, 1f);
        AmbientIntensity = Clamp(AmbientIntensity, 0f, MaxLightIntensity, 1f);
        SwellAmplitude = Clamp(SwellAmplitude, 0f, MaxSwellAmplitude, presetSwell);
        FoamStrength = Clamp(FoamStrength, 0f, MaxFoamStrength, presetFoam);
    }

    // A non-finite value carries no information to clamp (Math.Clamp propagates NaN), so it takes the default.
    static float Clamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    // The offered multiplier closest to `value`, and the base multiplier for anything non-finite. Ties go to the
    // lower tier (the cheaper horizon), which is the safe direction on an unknown machine.
    static float NearestMultiplier(float value)
    {
        if (!float.IsFinite(value)) return RenderDistanceMultipliers[0];
        float best = RenderDistanceMultipliers[0];
        float bestDistance = MathF.Abs(value - best);
        foreach (float candidate in RenderDistanceMultipliers)
        {
            float distance = MathF.Abs(value - candidate);
            if (distance < bestDistance) { best = candidate; bestDistance = distance; }
        }
        return best;
    }
}

/// <summary>
/// The persisted <see cref="EditorSettings"/> the editor scene reads on enter and writes back on every change.
/// Mirrors <see cref="IRecentFilesStore"/>: the head owns the store (and so the storage seam), the scene only
/// reads <see cref="Settings"/> and calls <see cref="Save"/>.
/// </summary>
public interface IEditorSettingsStore
{
    /// <summary>The live settings instance. Mutate it in place, then call <see cref="Save"/>. Never null.</summary>
    EditorSettings Settings { get; }

    /// <summary>Persist the current <see cref="Settings"/> through the coalesced write queue, so a run of slider
    /// frames collapses to one file write.</summary>
    void Save();

    /// <summary>Drain any pending persisted write so the on-disk file reflects every prior <see cref="Save"/> before
    /// shutdown. A head calls this once during its own quit flushing, exactly like
    /// <see cref="IRecentFilesStore.Flush"/>. A scene never calls it directly.</summary>
    void Flush();
}

/// <summary>
/// The canonical <see cref="IEditorSettingsStore"/>: one <see cref="EditorSettings"/> persisted through the engine
/// settings seam (<see cref="ISettingsStorage"/>) on every change, riding its own <see cref="FileName"/> so it
/// never collides with a game's <c>settings.json</c> or with <see cref="EditorRecentFiles.FileName"/>.
/// <para>Construct it with an already-built <see cref="ISettingsStorage"/> (the testable shape: a test injects a
/// fake or a temp-rooted <see cref="FileSettingsStorage"/>), or with a publisher / app-name pair, which builds a
/// publisher-rooted <see cref="GameStorage"/> internally. The <see cref="Flush"/> contract matches
/// <see cref="EditorRecentFiles"/> exactly: the publisher/app-name overload owns its queue, and the
/// <see cref="ISettingsStorage"/> overload drains one only when the caller passes it in.</para>
/// </summary>
public sealed class EditorSettingsStore : IEditorSettingsStore
{
    /// <summary>The settings file the editor preferences ride, kept distinct from a game's own
    /// <c>settings.json</c> and from the editor's recents file.</summary>
    public const string FileName = "editor-settings.json";

    readonly ISettingsStorage _storage;
    readonly IPersistenceQueue? _queue;
    readonly GameStorage? _ownedStorage;
    EditorSettings _settings = new();

    /// <summary>Wraps an already-built <paramref name="storage"/> (which it points at <see cref="FileName"/>) and
    /// loads the persisted settings, sanitizing them. <paramref name="queue"/> is optional: pass the
    /// <see cref="IPersistenceQueue"/> <paramref name="storage"/> itself writes through so <see cref="Flush"/> can
    /// drain it.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public EditorSettingsStore(ISettingsStorage storage, IPersistenceQueue? queue = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _storage.SettingsFileName = FileName;
        _queue = queue;
        Load();
    }

    /// <summary>Convenience overload for a head: builds a publisher-rooted <see cref="GameStorage"/> internally and
    /// rides its settings storage, so the editor preferences nest beside the game's own data
    /// (<see cref="AppDataPaths"/>).</summary>
    public EditorSettingsStore(string publisher, string appName)
        : this(new GameStorage(publisher, appName))
    {
    }

    // Chains to the ISettingsStorage ctor over the owned GameStorage's settings storage, then keeps the
    // GameStorage itself so Flush can reach its write queue (the EditorRecentFiles idiom).
    EditorSettingsStore(GameStorage owned) : this(owned.Settings)
    {
        _ownedStorage = owned;
    }

    /// <inheritdoc/>
    public EditorSettings Settings => _settings;

    /// <inheritdoc/>
    // Persist a COPY: the live instance stays under the settings menu's edits, and a storage implementation that
    // defers serialization would otherwise write whatever the next slider frame left behind.
    public void Save() => _storage.SaveSettings(_settings.Clone());

    /// <inheritdoc/>
    // At most one of the two handles is ever non-null for a given instance (see the ctors: the publisher/app-name
    // overload owns a GameStorage and passes no queue, the ISettingsStorage overload owns no storage and may or may
    // not be handed one), so draining both is a single unconditional call rather than a branch.
    public void Flush()
    {
        _ownedStorage?.Flush();
        _queue?.Flush();
    }

    // Load, then re-apply the ranges: a file written by a newer build, hand-edited, or truncated must degrade to a
    // usable editor rather than throwing out of OnEnter or rendering a black viewport.
    void Load()
    {
        _settings = _storage.LoadSettings<EditorSettings>() ?? new EditorSettings();
        _settings.Sanitize();
    }
}
