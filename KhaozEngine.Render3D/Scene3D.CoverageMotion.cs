using System.Numerics;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    Matrix4x4 CoveragePreviousModel(MotionKey actor, in Matrix4x4 current)
    {
        foreach (var host in _coverageHosts)
            if (host.Actor == actor && host.Request.PreviousSlot >= 0)
                return ToRender(host.Request.History.Committed.Model);
        return current;
    }
}
