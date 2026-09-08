namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Selects the fidelity of a full directional shadow map. This does not select between the
    /// <see cref="ShadowMode.Off"/>, <see cref="ShadowMode.Blob"/>, and <see cref="ShadowMode.ShadowMap"/>
    /// techniques. Apply the selected profile before scene construction. Changing it later requires a scene rebuild
    /// or restart because the shadow atlas is committed during construction.
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
        /// <see cref="ShadowMode.ShadowMap"/> and must be supplied before scene construction. Applying another
        /// profile after construction requires a scene rebuild or restart.
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
