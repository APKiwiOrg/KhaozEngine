using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld : IPhysicsStaticGeometrySource
{
    /// <inheritdoc/>
    public long StaticGeometryGeneration
    {
        get
        {
            using QueryOperation scope = EnterQuery();
            return _staticGeometryVersion;
        }
    }

    QueryOperation EnterStaticMutation()
    {
        QueryOperation scope = EnterMutation();
        try
        {
            // Invalidate before installation can partially mutate the backend.
            StaticGeometryChanged();
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }
}
