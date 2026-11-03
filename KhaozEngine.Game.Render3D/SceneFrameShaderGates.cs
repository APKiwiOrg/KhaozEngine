using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game;

// Carries the recorder's block boundaries to the scene. Registered only when the recorder was requested.
internal sealed class SceneFrameShaderGates(Scene3D scene) : IFrameDiagnosticsShaderGates
{
    public void Apply(int gates) => scene.ProbeShaderGates = (ShaderGateProbe)gates;
    public int ConsumeExactSizeCondition() => scene.ConsumeProbeExactSizeCondition();
    public int ConsumeApplied() => scene.ConsumeAppliedProbeShaderGates();
}
