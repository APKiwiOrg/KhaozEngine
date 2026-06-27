using System;
using System.Numerics;

namespace KhaozEngine.Terrain
{
    /// <summary>Terrain-aware collision over a TerrainField: ground-follow height and a slope walkability gate.
    /// The sim (e.g. Sharding's CellSim) calls this each tick to keep entities on the ground and reject moves
    /// onto terrain steeper than a per-entity budget. Render-free. Lives in KhaozEngine.Terrain (not Collision)
    /// so the dependency edge stays Terrain -> Primitives and the field is not dragged into 2D collision.</summary>
    public sealed class TerrainCollision
    {
        readonly TerrainField _field;

        public TerrainCollision(TerrainField field) => _field = field ?? throw new ArgumentNullException(nameof(field));

        /// <summary>Ground height at the world point (= TerrainField.SampleHeight).</summary>
        public float GroundHeight(float x, float z) => _field.SampleHeight(x, z);

        /// <summary>Surface normal at the world point (= TerrainField.SampleNormal). Pass this as the
        /// <c>groundNormal</c> slope-gate delegate to CharacterMovement.Step so steep terrain (the rim wall)
        /// cannot be walked up.</summary>
        public Vector3 GroundNormal(float x, float z) => _field.SampleNormal(x, z);

        /// <summary>True when the surface slope at (x,z) is no steeper than <paramref name="maxSlopeRadians"/>
        /// (the angle between the surface normal and +Y).</summary>
        public bool IsWalkable(float x, float z, float maxSlopeRadians)
        {
            float ny = Math.Clamp(_field.SampleNormal(x, z).Y, 0f, 1f);
            float slope = MathF.Acos(ny);
            return slope <= maxSlopeRadians;
        }
    }
}
