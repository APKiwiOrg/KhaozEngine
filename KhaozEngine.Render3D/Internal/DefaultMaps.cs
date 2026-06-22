namespace KhaozEngine.Render3D.Internal
{
    /// <summary>1x1 default texels for the optional model maps, kept pure so the byte values are
    /// headless-testable. Flat normal (128,128,255) decodes to tangent-space (0,0,1); zero roughness
    /// (.g = 0) means "fully smooth" so the per-instance specular is used unchanged. These are the
    /// no-map defaults that keep untextured meshes rendering bit-identical to the pre-PBR pass.</summary>
    internal static class DefaultMaps
    {
        public static byte[] FlatNormalTexel() => new byte[] { 128, 128, 255, 255 };
        public static byte[] ZeroRoughnessTexel() => new byte[] { 0, 0, 0, 255 };
    }
}
