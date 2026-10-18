using System;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// DIAGNOSTIC SWITCHES FOR THE TEMPORAL RESOLVE, for attribution only. <see cref="EnvironmentVariable"/> holds
    /// comma-separated tokens, read once when the process first asks. The mask reaches both entry points through
    /// <see cref="TemporalResolveUniforms.Params"/>.w (<see cref="TemporalResolveMath.BuildUniforms"/>), and the
    /// shader reads it as <c>temporalDiag</c>. Unset, the mask is 0 and every rule runs as it does without the
    /// switches. Not a setting. A game never ships it set.
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

        /// <summary>The mask the tokens set when the process first asked.</summary>
        public static int Mask { get; } = Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

        /// <summary>Whether <see cref="ShowReleased"/> is set.</summary>
        public static bool ShowsReleased => (Mask & ShowReleased) != 0;

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
                    "release-off" => ReleaseOff,
                    "release-floor-2" => ReleaseFloor2,
                    "release-floor-4" => ReleaseFloor4,
                    "locks-off" => LocksOff,
                    "current-only" => CurrentOnly,
                    "recon-nonneg" => ReconNonNegative,
                    "dering-near" => DeringNear,
                    _ => throw new InvalidOperationException(
                        $"{EnvironmentVariable}: unknown token '{raw}'."),
                };
            }
            return mask;
        }
    }
}
