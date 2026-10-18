using System;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// DIAGNOSTIC SWITCHES FOR THE TEMPORAL RESOLVE, for attribution only. <see cref="EnvironmentVariable"/> holds
    /// comma-separated tokens, read once when the process first asks. The mask reaches both entry points through
    /// <see cref="TemporalResolveUniforms.Params"/>.w (<see cref="TemporalResolveMath.BuildUniforms"/>), and the
    /// shader reads it as <c>temporalDiag</c>. Unset, the mask is 0 and every rule runs as it does without the
    /// switches. Not a setting. A game never ships it set.
    /// <para><b>COMPILED ONLY WHEN SET.</b> The switches' GLSL sits under <c>#ifdef TemporalDiagnostics</c>, which
    /// <see cref="Apply"/> defines in every program that runs the per-pixel resolve only while the mask is not 0.
    /// Metal compiles with fast math, so any change to a program's text can move its rounding, and the shading
    /// release's thresholds carry a rounding step into a visible one. Unset, each program's text after preprocessing
    /// is the resolve's own, and so is its output. Set, every token is compiled in, and <c>noop</c> is the control
    /// that compiles them in with no effect.</para>
    /// </summary>
    internal static class TemporalResolveDiagnostics
    {
        /// <summary>The environment variable holding the tokens.</summary>
        public const string EnvironmentVariable = "KE_TEMPORAL_DIAG";

        /// <summary>Released pixels draw pure magenta over the final image, re-evaluated over the resolve's own
        /// set after the post chain, so the history never holds the colour.</summary>
        public const int ShowReleased = 1;
        /// <summary>The shading release never fires, on the still and on the moving path.</summary>
        public const int ReleaseOff = 2;
        /// <summary>A released pixel keeps history clipped to a box of half the gamma, with its accumulated weight
        /// capped at 2 frames, and keeps its display share.</summary>
        public const int ReleaseFloor2 = 4;
        /// <summary>As <see cref="ReleaseFloor2"/> with the cap at 4 frames.</summary>
        public const int ReleaseFloor4 = 8;
        /// <summary>Thin-feature locks are never set or held.</summary>
        public const int LocksOff = 16;
        /// <summary>History weight zero: the output is the clamped internal reconstruction, with no display
        /// share.</summary>
        public const int CurrentOnly = 32;
        /// <summary>The internal and display reconstructions use exp(-2.29 x^2) truncated at |x| &lt; 2, the
        /// Blackman-Harris approximation, in place of Lanczos 2. The sample weight stays on Lanczos 2.</summary>
        public const int ReconNonNegative = 64;
        /// <summary>The internal and display reconstructions are clamped to the range of the 2x2 texels nearest the
        /// pixel centre in jittered sample space, in place of the 3x3.</summary>
        public const int DeringNear = 128;
        /// <summary>Compiles the switches in and changes nothing, the control for the others.</summary>
        public const int NoOp = 256;

        /// <summary>Active locks, refreshed or carried this frame, draw pure cyan after the post chain.</summary>
        public const int ShowLocked = 512;

        /// <summary>Refresh a ridge lock only where history has the same ridge direction and polarity.</summary>
        public const int LockHistoryRidge = 1024;
        /// <summary>Release a carried lock on the ring shading evidence without the opposite-pair condition.</summary>
        public const int LockShadeRelease = 2048;
        /// <summary>On that shading evidence, clip with no lock exemption and keep the lock state.</summary>
        public const int LockBoundCurrent = 4096;

        /// <summary>Held, unreleased pixels draw a flat colour for the first shading-release condition that blocked
        /// them, after the post chain.</summary>
        public const int ShowGate = 8192;
        /// <summary>Active locks draw a flat colour for their source, a ridge this frame or a carried lock by its age
        /// since the last ridge, after the post chain.</summary>
        public const int ShowLockAge = 16384;
        /// <summary>Held, unreleased pixels past the upscale and ridge or box-width gates draw a flat colour for their
        /// box half-width over ridgeStep, and whether the release would fire with ridgeStep as its extent.</summary>
        public const int ShowGateExtent = 32768;
        /// <summary>A ridge raises the lock by a half, a third or a quarter, capped at 1, in place of setting it whole.</summary>
        public const int LockTrust2 = 65536;
        public const int LockTrust3 = 131072;
        public const int LockTrust4 = 262144;
        /// <summary>The lock and hold drop to zero where low-frequency luma changed by more than 0.1 or 0.2, as one
        /// minus min over max of this frame's and last frame's.</summary>
        public const int LockLumaKill10 = 524288;
        public const int LockLumaKill20 = 1048576;
        /// <summary>The luma kill measures a 5x5 internal-texel footprint on both sides, in place of this frame's 3x3
        /// against last frame's ring plus centre.</summary>
        public const int LockLumaKillWide = 2097152;

        /// <summary>Nine bilinear comparison taps, shared over a 4x4 footprint.</summary>
        public const int ShadowBilinear3x3 = 4194304;
        /// <summary>One bilinear comparison tap over a 2x2 footprint.</summary>
        public const int ShadowBilinear1 = 8388608;
        internal const int ShadowFilterMask = ShadowBilinear3x3 | ShadowBilinear1;

        /// <summary>Compile only the selected shadow filter into a lighting fragment. Resolve-only tokens leave
        /// the lighting program untouched. The two filters are mutually exclusive.</summary>
        internal static string ApplyShadowFilter(string program, int? mask = null)
        {
            int filter = (mask ?? Mask) & ShadowFilterMask;
            string define = filter switch
            {
                0 => "",
                ShadowBilinear3x3 => "#define ShadowBilinear3x3\n",
                ShadowBilinear1 => "#define ShadowBilinear1\n",
                _ => throw new InvalidOperationException("Choose one shadow filter."),
            };
            if (define.Length == 0) return program;
            int line = program.IndexOf('\n');
            return line < 0 ? define + program : program.Insert(line + 1, define);
        }

        /// <summary>The define that compiles the switches' GLSL in.</summary>
        internal const string Define = "#define TemporalDiagnostics\n";

        /// <summary>The mask the tokens set when the process first asked.</summary>
        public static int Mask { get; private set; } = Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

        /// <summary>Override the mask for a paired diagnostic measurement. The caller serializes process-global
        /// state, creates the scene with a nonzero mask to compile the diagnostic GLSL, and restores the old mask.</summary>
        internal static void OverrideMaskForMeasurement(int mask) => Mask = mask;

        /// <summary>Whether a diagnostic overlay is set. Precedence is ShowGate, ShowGateExtent, ShowLockAge, ShowLocked,
        /// then ShowReleased.</summary>
        public static bool ShowsOverlay => (Mask & (ShowReleased | ShowLocked | ShowGate | ShowLockAge | ShowGateExtent)) != 0;

        /// <summary><paramref name="program"/> with <see cref="Define"/> after its <c>#version</c> line while the mask
        /// is not 0, else <paramref name="program"/> itself.</summary>
        internal static string Apply(string program)
        {
            if ((Mask & ~ShadowFilterMask) == 0) return program;
            int line = program.IndexOf('\n');
            return line < 0 ? Define + program : program.Insert(line + 1, Define);
        }

        /// <summary>The mask of a token list. Null or blank is 0. An unknown token throws, so a typo never measures
        /// as the base.</summary>
        internal static int Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            int mask = 0;
            foreach (string raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                mask |= raw.ToLowerInvariant() switch
                {
                    "shadow-bilinear-3x3" => ShadowBilinear3x3,
                    "shadow-bilinear-1" => ShadowBilinear1,
                    "show-released" => ShowReleased,
                    "show-locked" => ShowLocked,
                    "lock-history-ridge" => LockHistoryRidge,
                    "lock-shade-release" => LockShadeRelease,
                    "lock-bound-current" => LockBoundCurrent,
                    "show-gate" => ShowGate,
                    "show-lock-age" => ShowLockAge,
                    "show-gate-extent" => ShowGateExtent,
                    "lock-trust-2" => LockTrust2,
                    "lock-trust-3" => LockTrust3,
                    "lock-trust-4" => LockTrust4,
                    "lock-luma-kill-0.1" => LockLumaKill10,
                    "lock-luma-kill-0.2" => LockLumaKill20,
                    "lock-luma-kill-wide-0.1" => LockLumaKill10 | LockLumaKillWide,
                    "lock-luma-kill-wide-0.2" => LockLumaKill20 | LockLumaKillWide,
                    "release-off" => ReleaseOff,
                    "release-floor-2" => ReleaseFloor2,
                    "release-floor-4" => ReleaseFloor4,
                    "locks-off" => LocksOff,
                    "current-only" => CurrentOnly,
                    "recon-nonneg" => ReconNonNegative,
                    "dering-near" => DeringNear,
                    "noop" => NoOp,
                    _ => throw new InvalidOperationException(
                        $"{EnvironmentVariable}: unknown token '{raw}'."),
                };
            }
            if ((mask & ShadowFilterMask) == ShadowFilterMask)
                throw new InvalidOperationException("Choose one shadow filter.");
            return mask;
        }
    }
}
