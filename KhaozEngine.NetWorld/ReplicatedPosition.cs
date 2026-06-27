using System.Numerics;
using KhaozEngine.Ecs;

namespace KhaozEngine.NetWorld;

/// <summary>The one replicated gameplay component: an entity's 3D world position. Interpolatable.</summary>
public struct ReplicatedPosition : IComponent
{
    public Vector3 Value;
}
