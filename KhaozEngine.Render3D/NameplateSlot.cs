namespace KhaozEngine.Render3D;

/// <summary>A content key shared by a nameplate component and its current value.</summary>
public readonly record struct NameplateSlot(string Key)
{
    /// <summary>The entity's display name. The legacy plate draws its title from this slot.</summary>
    public static readonly NameplateSlot Name = new("name");
    /// <summary>The entity's level, usually shown in a badge.</summary>
    public static readonly NameplateSlot Level = new("level");
    /// <summary>The entity's health, usually shown as a bar.</summary>
    public static readonly NameplateSlot Health = new("health");
}
