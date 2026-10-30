using KhaozEngine.Render3D.Internal;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    ShadowInputObservationBuilder _shadowInputObservation;

    bool ShadowProbeMapActive()
    {
        bool active = Post.Quality.Shadows.ResolveFor(_gd.Capabilities).Effective == ShadowMode.ShadowMap;
        if (_shadowProbeActive && !active)
            _shadowInputObservation.ObserveUnavailable(ShadowInputAvailability.Disabled);
        return active;
    }
}
