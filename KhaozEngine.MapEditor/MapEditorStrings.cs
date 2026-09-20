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
}
