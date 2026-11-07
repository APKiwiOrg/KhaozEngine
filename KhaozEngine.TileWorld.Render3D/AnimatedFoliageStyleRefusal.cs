namespace KhaozEngine.TileWorld;

/// <summary>The stable reasons AnimatedFoliageStyle input is refused.</summary>
public enum AnimatedFoliageStyleRefusal
{
    /// <summary>Refuses a style key that does not exactly name a selected authored archetype.</summary>
    StyleOutsideSelection,

    /// <summary>Refuses selected archetypes sharing geometry with different effective styles.</summary>
    SharedMeshStyleConflict,
}
