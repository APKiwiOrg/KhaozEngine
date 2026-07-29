namespace KhaozEngine.Render3D
{
    /// <summary>Why the key-light shadow depth pass did or did not render on the last frame.</summary>
    public readonly struct ShadowPassDiagnostics
    {
        /// <summary>Whether the resolved shadow tier was <see cref="ShadowMode.ShadowMap"/> this frame.</summary>
        public bool Active { get; }

        /// <summary>Whether the depth pass recorded caster draws this frame.</summary>
        public bool Rendered { get; }

        /// <summary>Whether the previous depth atlas was reused this frame.</summary>
        public bool Skipped { get; }

        /// <summary>Whether a prior depth atlas existed before this frame's decision.</summary>
        public bool HadPrevious { get; }

        /// <summary>Whether at least one animated skinned caster forced the pass to render.</summary>
        public bool AnySkinnedCaster { get; }

        /// <summary>Whether the shadow atlas resolution changed since its last rendered pass.</summary>
        public bool ResolutionChanged { get; }

        /// <summary>Whether a fitted cascade matrix changed since the last rendered pass.</summary>
        public bool LightMatrixChanged { get; }

        /// <summary>Whether the rigid caster signature changed since the last rendered pass.</summary>
        public bool CasterDataChanged { get; }

        /// <summary>How many skinned casters were queued for the shadow pass.</summary>
        public int SkinnedCasterCount { get; }

        /// <summary>How many cascades were active this frame.</summary>
        public int CascadeCount { get; }

        internal ShadowPassDiagnostics(bool active, bool rendered, bool skipped, bool hadPrevious,
            bool anySkinnedCaster, bool resolutionChanged, bool lightMatrixChanged, bool casterDataChanged,
            int skinnedCasterCount, int cascadeCount)
        {
            Active = active;
            Rendered = rendered;
            Skipped = skipped;
            HadPrevious = hadPrevious;
            AnySkinnedCaster = anySkinnedCaster;
            ResolutionChanged = resolutionChanged;
            LightMatrixChanged = lightMatrixChanged;
            CasterDataChanged = casterDataChanged;
            SkinnedCasterCount = skinnedCasterCount;
            CascadeCount = cascadeCount;
        }
    }
}