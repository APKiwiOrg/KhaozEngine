namespace KhaozEngine.Locomotion;

/// <summary>Certified ordinary water producers.</summary>
public static class MovementWaterField
{
    public static IMovementWaterField Level(string worldId, float surfaceY, float floorY) =>
        new LevelMovementWaterField(worldId, surfaceY, floorY);
}
