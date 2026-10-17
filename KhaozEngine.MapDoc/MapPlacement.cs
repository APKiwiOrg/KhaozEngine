using System.Collections.Generic;

namespace KhaozEngine.MapDoc;

/// <summary>An authored prop/building placement. Null Y = ground-snap to the terrain field at load.</summary>
public sealed partial class MapPlacement
{
    string _id = "";
    float _x;
    float _z;

    /// <summary>Stable editor identity, unique within the document.</summary>
    public string Id
    {
        get
        {
            LookupReads++;
            return _id;
        }
        set => _id = value;
    }

    /// <summary>The asset-manifest kit id to instance.</summary>
    public string Kind { get; set; } = "";

    public float X
    {
        get
        {
            LookupReads++;
            return _x;
        }
        set => _x = value;
    }

    public float Z
    {
        get
        {
            LookupReads++;
            return _z;
        }
        set => _z = value;
    }

    public float? Y { get; set; }
    public float Yaw { get; set; }
    public float Scale { get; set; } = 1f;
    public List<string> Tags { get; set; } = new();

    /// <summary>Reads of this placement's identity and planar position lookup keys. A pick's maximum per-placement
    /// delta exposes nested scans through captured documents or other collections holding the same placements,
    /// independently of the document getter and without a clock.</summary>
    internal long LookupReads { get; private set; }
}
