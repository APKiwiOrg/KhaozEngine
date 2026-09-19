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
            // The point-light atlas rides the same three profiles. Low turns it off outright (its cost is a
            // whole second shadow pass, which is the first thing a low-end profile should stop paying), Default
            // keeps PointShadowSettings' own defaults, and High raises both the face and the light budget.
            //
            // HIGH IS 384 BY 12 RATHER THAN 512 BY 16 BECAUSE OF WHAT THAT COSTS. The atlas is nine bytes a texel
            // (R32Float colour plus D32FloatS8UInt depth), so 512 by 16 is 216 MiB of resident video memory, which
            // is not a defensible thing for a quality preset to help itself to. 384 by 12 is about 91 MiB and is
            // still half again the face resolution and half again the light budget of Default's 256 by 8 (27 MiB).
            // PointShadowSettings.AtlasBytes is the arithmetic, and a test pins all three profiles against it.
            var points = new PointShadowSettings();
            switch (detail)
            {
                case ShadowMapDetail.Low:
                    // Off, and HARD as well, which is what a game turning point shadows back on over a low-end
                    // profile should inherit: the soft filter is about fifteen atlas fetches a lit fragment
                    // against four.
                    points.Enabled = false;
                    points.Filter = PointShadowFilter.Hard;
                    break;
                case ShadowMapDetail.High:
                    points.FaceResolution = 384;
                    points.MaxShadowedLights = 12;
                    break;
            }
            return new ShadowSettings
            {
                Mode = ShadowMode.ShadowMap,
                ShadowMapResolution = resolution,
                PointShadows = points,
            };
        }
    }
}
