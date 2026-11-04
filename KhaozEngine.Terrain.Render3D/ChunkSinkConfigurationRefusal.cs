namespace KhaozEngine.Terrain;

/// <summary>The stable reasons ChunkSinkConfiguration input or state is refused.</summary>
public enum ChunkSinkConfigurationRefusal
{
    /// <summary>Refuses a dynamics source without a physics world.</summary>
    DynamicsRequiresPhysics,

    /// <summary>Refuses terrain collision without a physics world.</summary>
    CollisionRequiresPhysics,

    /// <summary>Refuses field replacement while CPU chunk builds are running.</summary>
    FieldChangeDuringBuild,

    /// <summary>Refuses layer replacement while CPU chunk builds are running.</summary>
    LayersChangeDuringBuild,

    /// <summary>Refuses LOD-table replacement while CPU chunk builds are running.</summary>
    LodChangeDuringBuild,
}
