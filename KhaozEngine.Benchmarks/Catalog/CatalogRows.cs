namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// The row shapes the six engine types carry, one struct per type so the codec is a positional walk over
/// a known field list rather than a dictionary. Markers carry no member at all, because a localized text
/// key is derived and the row stores nothing for it (spec section 3.2).
/// </summary>
public struct TagRowData
{
    public int Id;
    public string Key;
    public int Sort;
    public bool Retired;
}

public struct ItemRowData
{
    public int Id;
    public string Key;
    public bool Retired;
    public int Tag0;
    public int Tag1;
    public int Tag2;
    public int Tag3;
    public int TagCount;
    public bool Stackable;
    public int MaxStack;
    public bool Tradable;
    public int Value;
    public string Icon;
    public string Mesh;
    public string HeldMesh;
    public int GroundPose;
    public int IconTilt;
    public int IconSpin;
    public int DurabilityMax;
    public int SocketMax;
    public int EquipProfile;
}

public struct StatRowData
{
    public int Id;
    public string Key;
    public bool Retired;
    public int Scale;
    public int Min;
    public int Max;
    public int Tag0;
    public int Tag1;
    public int TagCount;
}

public struct LootTableRowData
{
    public int Id;
    public string Key;
    public bool Retired;
    public int RollCount;
    public int Tag0;
    public int TagCount;
    public bool Guaranteed;
}

public struct LootEntryRowData
{
    public int Id;
    public string Key;
    public bool Retired;
    public int Table;
    public int Item;
    public int NestedTable;
    public int Weight;
    public int ChanceBp;
    public int MinCount;
    public int MaxCount;
    public int Sort;
    public int RequiredTag0;
    public int RequiredTagCount;
}

public struct BaseSocketRowData
{
    public int Id;
    public string Key;
    public bool Retired;
    public int Item;
    public int Sort;
    public int SocketType;
}

public struct GameRowData
{
    public int Id;
    public string Key;
    public bool Retired;
    public int Slot;
    public int WeaponArchetype;
}
