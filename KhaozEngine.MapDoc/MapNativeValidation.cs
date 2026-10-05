using System.Collections.Generic;

namespace KhaozEngine.MapDoc;

/// <summary>Native metadata checks shared by loading, editing and saving.</summary>
internal static class MapNativeValidation
{
    internal static void Validate(MapDocument doc, List<string> errors)
    {
        if (doc.PlayableBounds is not { } bounds) return;
        if (!float.IsFinite(bounds.MinX) || !float.IsFinite(bounds.MinZ) ||
            !float.IsFinite(bounds.MaxX) || !float.IsFinite(bounds.MaxZ) ||
            !(bounds.MaxX > bounds.MinX) || !(bounds.MaxZ > bounds.MinZ))
            errors.Add("playableBounds must be finite with MaxX > MinX and MaxZ > MinZ.");
        if (!(bounds.MinX >= doc.Bounds.MinX) || !(bounds.MinZ >= doc.Bounds.MinZ) ||
            !(bounds.MaxX <= doc.Bounds.MaxX) || !(bounds.MaxZ <= doc.Bounds.MaxZ))
            errors.Add("playableBounds must be contained in bounds.");
    }
}
