namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Selects the fidelity of a full directional shadow map. This does not select between the
    /// <see cref="ShadowMode.Off"/>, <see cref="ShadowMode.Blob"/>, and <see cref="ShadowMode.ShadowMap"/>
    /// techniques. Use <see cref="ShadowSettings.ForDetail"/> to seed a scene before construction, or
    /// <see cref="Scene3D.RequestShadowMapDetail"/> to request the equivalent atlas layout on a live scene. A live
    /// request applies when the next <see cref="Scene3D.Begin"/> starts a frame.
    /// </summary>
    public enum ShadowMapDetail
    {
        Low,
        Default,
        High,
    }

    public sealed partial class ShadowSettings
    {
        /// <summary>
        /// Creates fresh settings for the requested full directional shadow-map fidelity. The result uses
        /// <see cref="ShadowMode.ShadowMap"/> and can seed a scene before construction. To apply the profile's atlas
        /// layout to an existing scene, call <see cref="Scene3D.RequestShadowMapDetail"/>. That request preserves the
        /// live scene's current shadow mode and other shadow settings.
        /// </summary>
        public static ShadowSettings ForDetail(ShadowMapDetail detail)
        {
            int resolution = detail switch
            {
                ShadowMapDetail.Low => 1024,
                ShadowMapDetail.High => 3072,
                _ => 2048,
            };
            return new ShadowSettings
            {
                Mode = ShadowMode.ShadowMap,
                ShadowMapResolution = resolution,
            };
        }
    }
}
