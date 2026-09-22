namespace KhaozEngine.MapEditor;

/// <summary>Explicit draw and pick categories for prop kits in the map editor.</summary>
public enum EditorPropCategory
{
    /// <summary>A prop kit that is not explicitly classified as a tree or rock.</summary>
    OtherProps,
    /// <summary>An explicitly classified tree kit.</summary>
    Trees,
    /// <summary>An explicitly classified rock kit.</summary>
    Rocks,
}
