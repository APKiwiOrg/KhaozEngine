namespace KhaozEngine.TileWorld;

/// <summary>The stable reasons TilePropLayer input is refused.</summary>
public enum TilePropLayerRefusal
{
    /// <summary>Refuses a null prop-layer definition.</summary>
    DefinitionNull,

    /// <summary>Refuses a prop layer without an id.</summary>
    LayerIdMissing,

    /// <summary>Refuses duplicate prop-layer ids.</summary>
    LayerIdDuplicate,

    /// <summary>Refuses a prop layer selecting no archetypes.</summary>
    SelectionEmpty,

    /// <summary>Refuses a prop layer naming an unknown archetype.</summary>
    ArchetypeMissing,

    /// <summary>Refuses an archetype selected by multiple prop layers.</summary>
    ArchetypeSelectedTwice,

    /// <summary>Refuses a layer mixing roof and ordinary archetypes.</summary>
    RoofAndOrdinaryMixed,

    /// <summary>Refuses a roof layer enabling interior cuts.</summary>
    RoofInteriorCutEnabled,

    /// <summary>Refuses aggregate LOD HLOD and draw distances with an invalid ordering.</summary>
    DistanceOrderingInvalid,
}
