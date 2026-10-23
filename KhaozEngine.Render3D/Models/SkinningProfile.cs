namespace KhaozEngine.Render3D
{
    /// <summary>The deformation method selected for an entire source skin.</summary>
    public enum SkinningMethod
    {
        LinearBlend = 0,
        DualQuaternionRigid = 1,
    }

    /// <summary>An immutable, value-equal skinning method and supported algorithm version.
    /// Selecting a different profile requires a distinct source asset and uploaded mesh identity.</summary>
    public sealed record SkinningProfile
    {
        public SkinningMethod Method { get; }
        public int AlgorithmVersion { get; }

        public static SkinningProfile LinearBlend { get; } = new(SkinningMethod.LinearBlend);
        public static SkinningProfile DualQuaternionRigid { get; } = new(SkinningMethod.DualQuaternionRigid);

        private SkinningProfile(SkinningMethod method)
        {
            Method = method;
            AlgorithmVersion = 1;
        }
    }
}
