using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>The render origin shared by an interior cut's uniforms and CPU queries. Default is world zero.</summary>
public readonly record struct InteriorCutFrame(double OriginX, double OriginY, double OriginZ)
{
    public InteriorCutFrame(Vector3 renderOrigin) : this(renderOrigin.X, renderOrigin.Y, renderOrigin.Z) { }

    /// <summary>Reduce a world position before rounding into the scene's float32 render frame.</summary>
    public Vector3 ToRender(Vector3 world) => new(
        (float)((double)world.X - OriginX), (float)((double)world.Y - OriginY), (float)((double)world.Z - OriginZ));

    /// <summary>The literal window and height vectors consumed by the GLSL predicate.</summary>
    public (Vector4 Window, Vector4 Heights) Uniforms(InteriorHeightField field) =>
        (new Vector4((float)((double)field.Window.Min.X - OriginX),
            (float)((double)field.Window.Min.Y - OriginZ), 1f / field.Window.CellSize, field.Window.Cells),
         new Vector4((float)((double)field.BaseY - OriginY), 1f / field.StepY, 0f, 0f));
}
