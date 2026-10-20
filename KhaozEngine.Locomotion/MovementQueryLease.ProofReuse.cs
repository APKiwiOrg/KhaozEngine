using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    /// <summary>Borrows this interval's physics lease to open reuse on its exact selected receiver.</summary>
    internal bool TryOpenSupportFootprintReuse(out IPhysicsSupportFootprintReuseSession? session)
    {
        AssertCurrent();
        session = null;
        return _view is IPhysicsSupportFootprintReuseSource source &&
            source.TryOpenSupportFootprintReuse(_physics, out session);
    }

    /// <summary>Authenticates both the current producer pin and the session's borrowed physics basis.</summary>
    internal void AssertSupportFootprintReuseCurrent(IPhysicsSupportFootprintReuseSession session)
    {
        AssertCurrent();
        session.AssertCurrent(_physics);
    }
}
