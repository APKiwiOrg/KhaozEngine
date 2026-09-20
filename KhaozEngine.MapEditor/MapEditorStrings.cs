using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.App;

namespace KhaozEngine.MapEditor;

/// <summary>Localization keys and built-in English fallback for the editor's manual reload action.</summary>
internal static class MapEditorStrings
{
    public static readonly StringId Reloaded = new("mapeditor.reload.done");
    public static readonly StringId ReloadUnsaved = new("mapeditor.reload.unsaved");
    public static readonly StringId ReloadFocused = new("mapeditor.reload.focused");
    public static readonly StringId ReloadMissing = new("mapeditor.reload.missing");
    public static readonly StringId ReloadFailed = new("mapeditor.reload.failed");
    public static readonly StringId Navigation = new("mapeditor.settings.navigation");
    public static readonly StringId FlySpeed = new("mapeditor.settings.fly_speed");
    public static readonly StringId FlySpeedDescription = new("mapeditor.settings.fly_speed.description");
    public static readonly StringId View = new("mapeditor.view.button");
    public static readonly StringId ViewOptions = new("mapeditor.view.options");
    public static readonly StringId TerrainOnly = new("mapeditor.view.terrain_only");
    public static readonly StringId ShowAll = new("mapeditor.view.show_all");
    public static readonly StringId PropCategories = new("mapeditor.view.prop_categories");
    public static readonly StringId OtherProps = new("mapeditor.view.other_props");
    public static readonly StringId Trees = new("mapeditor.view.trees");
    public static readonly StringId Rocks = new("mapeditor.view.rocks");
    public static readonly StringId Water = new("mapeditor.view.water");
    public static readonly StringId Markers = new("mapeditor.view.markers");
    public static readonly StringId Spawns = new("mapeditor.view.spawns");
    public static readonly StringId PlayerSpawns = new("mapeditor.view.player_spawns");
    public static readonly StringId Exclusions = new("mapeditor.view.exclusions");
    public static readonly StringId ScatterOverrides = new("mapeditor.view.scatter_overrides");
    public static readonly StringId Regions = new("mapeditor.view.regions");
    public static readonly StringId FeatureMarkers = new("mapeditor.view.feature_markers");
    public static readonly StringId ScatterLayers = new("mapeditor.view.scatter_layers");
    public static readonly StringId SculptRaise = new("mapeditor.sculpt.raise");
    public static readonly StringId SculptLower = new("mapeditor.sculpt.lower");
    public static readonly StringId SculptSmooth = new("mapeditor.sculpt.smooth");
    public static readonly StringId SculptFlatten = new("mapeditor.sculpt.flatten");
    public static readonly StringId SculptSetHeight = new("mapeditor.sculpt.set_height");
    public static readonly StringId SculptHover = new("mapeditor.sculpt.hover");
    public static readonly StringId SculptActive = new("mapeditor.sculpt.active");
    public static readonly StringId SculptUnavailable = new("mapeditor.sculpt.unavailable");

    static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["mapeditor.reload.done"] = "Reloaded {0}",
        ["mapeditor.reload.unsaved"] = "Reload blocked: save or discard unsaved changes first.",
        ["mapeditor.reload.focused"] = "Reload blocked while an editor field has keyboard focus.",
        ["mapeditor.reload.missing"] = "Reload failed: the document path does not exist.",
        ["mapeditor.reload.failed"] = "Reload failed: {0}",
        ["mapeditor.settings.navigation"] = "Navigation",
        ["mapeditor.settings.fly_speed"] = "Fly speed",
        ["mapeditor.settings.fly_speed.description"] =
            "Movement speed while right mouse owns the viewport. The wheel changes pivot distance instead.",
        ["mapeditor.view.button"] = "View",
        ["mapeditor.view.options"] = "View Options",
        ["mapeditor.view.terrain_only"] = "Terrain Only",
        ["mapeditor.view.show_all"] = "Show All",
        ["mapeditor.view.prop_categories"] = "Props",
        ["mapeditor.view.other_props"] = "Other props",
        ["mapeditor.view.trees"] = "Trees",
        ["mapeditor.view.rocks"] = "Rocks",
        ["mapeditor.view.water"] = "Water",
        ["mapeditor.view.markers"] = "Markers",
        ["mapeditor.view.spawns"] = "Spawns",
        ["mapeditor.view.player_spawns"] = "Player spawns",
        ["mapeditor.view.exclusions"] = "Exclusions",
        ["mapeditor.view.scatter_overrides"] = "Scatter overrides",
        ["mapeditor.view.regions"] = "Regions",
        ["mapeditor.view.feature_markers"] = "Feature markers",
        ["mapeditor.view.scatter_layers"] = "Scatter Layers",
        ["mapeditor.sculpt.raise"] = "Raise",
        ["mapeditor.sculpt.lower"] = "Lower",
        ["mapeditor.sculpt.smooth"] = "Smooth",
        ["mapeditor.sculpt.flatten"] = "Flatten",
        ["mapeditor.sculpt.set_height"] = "Set height",
        ["mapeditor.sculpt.hover"] = "Hover",
        ["mapeditor.sculpt.active"] = "Active",
        ["mapeditor.sculpt.unavailable"] = "Unavailable",
    };

    public static string Resolve(StringId id, params object?[] args)
    {
        IStringCatalog? catalog = LocalizationContext.Catalog;
        if (catalog is not null && catalog.TryGet(id.Key, out _))
            return args.Length == 0 ? catalog.Get(id.Key) : catalog.Format(id.Key, args);
        string format = English.TryGetValue(id.Key, out string? value) ? value : id.Key;
        return args.Length == 0
            ? format
            : IStringCatalog.SafeFormat(CultureInfo.InvariantCulture, format, args);
    }

    public static LocalizedText Text(StringId id) => LocalizedText.Raw(Resolve(id));

    public static StringId SculptOperation(SculptBrush brush) => brush switch
    {
        SculptBrush.Raise => SculptRaise,
        SculptBrush.Lower => SculptLower,
        SculptBrush.Smooth => SculptSmooth,
        SculptBrush.Flatten => SculptFlatten,
        SculptBrush.SetHeight => SculptSetHeight,
        _ => SculptRaise,
    };

    public static StringId SculptState(SculptOverlayState state) => state switch
    {
        SculptOverlayState.Hover => SculptHover,
        SculptOverlayState.Active => SculptActive,
        SculptOverlayState.Invalid => SculptUnavailable,
        _ => SculptUnavailable,
    };
}
