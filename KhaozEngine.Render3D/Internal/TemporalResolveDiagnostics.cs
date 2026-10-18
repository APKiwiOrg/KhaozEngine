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

        /// <summary>The define that compiles the switches' GLSL in.</summary>
        internal const string Define = "#define TemporalDiagnostics\n";

        /// <summary>The mask the tokens set when the process first asked.</summary>
        public static int Mask { get; private set; } = Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

        /// <summary>Override the mask for a paired diagnostic measurement. The caller serializes process-global
        /// state, creates the scene with a nonzero mask to compile the diagnostic GLSL, and restores the old mask.</summary>
        internal static void OverrideMaskForMeasurement(int mask) => Mask = mask;

        /// <summary>Whether a diagnostic overlay is set. ShowLocked takes precedence if both are set.</summary>
        public static bool ShowsOverlay => (Mask & (ShowReleased | ShowLocked)) != 0;

        /// <summary><paramref name="program"/> with <see cref="Define"/> after its <c>#version</c> line while the mask
        /// is not 0, else <paramref name="program"/> itself.</summary>
        internal static string Apply(string program)
        {
            if (Mask == 0) return program;
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
                    "show-released" => ShowReleased,
                    "show-locked" => ShowLocked,
                    "lock-history-ridge" => LockHistoryRidge,
                    "lock-shade-release" => LockShadeRelease,
                    "lock-bound-current" => LockBoundCurrent,
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
            return mask;
        }
    }
}
