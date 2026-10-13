namespace KhaozEngine.Render3D;

/// <summary>A content key shared by a nameplate component and its current value.</summary>
public readonly record struct NameplateSlot(string Key)
{
    public static readonly NameplateSlot Name = new("name");
    public static readonly NameplateSlot Level = new("level");
    public static readonly NameplateSlot Health = new("health");
}
