using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// WHEN the point-shadow atlas comes into existence, changes shape and goes away: all of it at the FRAME
    /// BOUNDARY, which is <see cref="Begin"/>, and none of it inside a recorded frame.
    /// <para>
    /// This is the cascade atlas's rule (<c>Scene3D.ShadowReconfigure.cs</c>) applied to the second atlas. An
    /// allocation is a texture pair, a framebuffer, four pipelines and a rebuild of every material set in the
    /// scene, and the rebuild carries a <c>WaitForIdle</c>. Doing that with a command list open means a full GPU
    /// stall in the middle of recording a frame, and it means the sets the model pass is about to bind being
    /// swapped underneath it. So a frame that finds no atlas of the layout it wants RECORDS what it wanted,
    /// renders unshadowed, and the next frame boundary brings the atlas up. The visible cost is that the first
    /// frame of a new request carries no map. That is one frame, once.
    /// </para>
    /// <para>
    /// LAZY IS PRESERVED (design decision 6). The boundary allocates nothing until a frame has actually carried a
    /// request, so a game that never asks for a point shadow still pays nothing, and turning the settings off
    /// gives the memory back at the next boundary.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        // The settings object RequestPointShadowSettings was handed, adopted at the next boundary. Separate from
        // the layout below because it is a different question: this is "the game changed the budget", that is "a
        // frame wanted a map and there was no atlas for it".
        PointShadowSettings? _pendingPointShadowSettings;

        // Whether a rendered frame has ever carried a shadow request, and how many stable static owners that frame
        // carried. Settings supply the configured floor. The request count is what can expand it.
        bool _pointShadowLayoutPending;
        int _pointShadowStaticRequests;

        // The layout the device refused, LATCHED. An allocation refusal is not transient: retrying it every frame
        // costs two texture allocations, four pipelines and a stall a frame, forever, for the same answer. Cleared
        // the moment a DIFFERENT layout is asked for, so a game that steps down to a smaller one is attempted.
        PointShadowLayout? _failedPointShadowLayout;
        bool _pointShadowFailureLogged;
        PointShadowLayout? _failedPointShadowTransientLayout;
        bool _pointShadowTransientFailureLogged;
        int _pointShadowTransientDemandRows;

        PointShadowResolution _resolvedPointShadows;

        /// <summary>
        /// What the point-shadow atlas is LIVE at right now: whether one exists, its layout, and whether the last
        /// requested layout was refused. A settings screen reads this rather than the settings object, because
        /// the settings object is what was asked for and this is what the frame is rendering.
        /// </summary>
        public PointShadowResolution ResolvedPointShadows => _resolvedPointShadows;

        /// <summary>Record this frame's transient row demand for the next frame boundary.</summary>
        internal void RecordPointShadowTransientDemand(int requiredRows)
        {
            if (requiredRows < 0) throw new ArgumentOutOfRangeException(nameof(requiredRows));
            _pointShadowTransientDemandRows = Math.Max(_pointShadowTransientDemandRows, requiredRows);
        }

        /// <summary>
        /// Request a whole point-shadow budget. The settings are CLONED, so the caller may keep and reuse the
        /// object it passed, and the clone is adopted at the next <see cref="Begin"/> along with any atlas
        /// reallocation it implies. Disabling releases the atlas entirely at that boundary.
        /// <para>
        /// Mutating <see cref="ShadowSettings.PointShadows"/> in place keeps working and is read at the same
        /// boundary. The two do not MERGE, and a request wins: the boundary assigns the clone over the live
        /// settings object, so an in-place edit made in the same frame goes with the object it was made on,
        /// whether it was made before the request or after it. Use one or the other within a frame.
        /// </para>
        /// <para>
        /// This overload exists for the case the cascade atlas has the same overload for: handing a whole profile
        /// over at once, without the caller having to know which fields a profile touches.
        /// </para>
        /// </summary>
        /// <param name="settings">The budget to adopt. Not retained.</param>
        public void RequestPointShadowSettings(PointShadowSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ThrowIfShadowReconfigureDisposed();
            _pendingPointShadowSettings = settings.Clone();
        }

        /// <summary>
        /// The frame boundary's point-shadow half, called from <see cref="Begin"/> directly after the cascade
        /// atlas's own (which may have rebuilt every material set, so this runs against the fresh ones). Adopts a
        /// requested budget, then brings the atlas into line with it: allocate, reshape, or release.
        /// </summary>
        void ApplyPendingPointShadowLayout()
        {
            if (_pendingPointShadowSettings is { } requested)
            {
                // The clone REPLACES the live settings object, so a request made in a frame takes any in-place
                // edit made to that object in the same frame with it. That is the documented rule rather than an
                // accident: merging two descriptions of the same budget would need a per-field dirty flag on a
                // plain settings object, and the caller already knows which of the two it meant.
                Post.Quality.Shadows.PointShadows = requested;
                _pendingPointShadowSettings = null;
            }

            PointShadowSettings settings = Post.Quality.Shadows.PointShadows;
            int transientDemand = _pointShadowTransientDemandRows;
            _pointShadowTransientDemandRows = 0;
            if (!settings.Enabled)
            {
                ReleasePointShadows();
                return;
            }

            PointShadowLayout wanted = ResolvePointShadowLayout(settings, _pointShadowStaticRequests);
            if (_failedPointShadowLayout is { } failed && failed != wanted)
            {
                // A different layout is a different question, so the refusal stops standing in its way.
                _failedPointShadowLayout = null;
                _pointShadowFailureLogged = false;
            }

            if (_pointShadowAtlas is { } live)
            {
                if (live.MatchesLayout(wanted.FaceResolution, wanted.Rows))
                {
                    ApplyCompatiblePointShadowTransientLayout(live, transientDemand);
                    return;
                }
            }
            else if (!_pointShadowLayoutPending)
            {
                // Nothing has asked for a map yet, so there is nothing to allocate. This is the branch a game
                // that never uses point shadows takes on every frame it ever renders.
                PublishResolvedPointShadows(false, false, null);
                return;
            }

            if (_failedPointShadowLayout is not null) return;   // already answered, and the answer was no
            if (_pointShadowStaticRequests > PointShadowSettings.MaxLights)
            {
                FailPointShadowLayout(wanted, $"{_pointShadowStaticRequests} static requests exceed the supported "
                    + $"{PointShadowSettings.MaxLights} rows");
                return;
            }
            AttemptPointShadowLayout(wanted, transientDemand);
        }

        static PointShadowLayout ResolvePointShadowLayout(PointShadowSettings settings, int staticRequests)
        {
            int staticRows = Math.Max(0, staticRequests);
            int dynamicReserve = Math.Min(Math.Max(0, settings.MaxDynamicLightsPerFrame),
                settings.ResolvedMaxLights);
            dynamicReserve = Math.Min(dynamicReserve, Math.Max(0, PointShadowSettings.MaxLights - staticRows));
            int rows = Math.Max(settings.ResolvedMaxLights, staticRows + dynamicReserve);
            return new PointShadowLayout(settings.ResolveFaceResolution(rows), rows);
        }

        static int ResolveTransientRowsForBaseReplacement(int liveTransientRows, int demandRows, int newBaseRows)
        {
            if (demandRows <= 0) return 0;
            return Math.Min(newBaseRows, Math.Max(liveTransientRows, demandRows));
        }

        void ClearTransientRefusalForChangedDemand(PointShadowLayout requested)
        {
            if (_failedPointShadowTransientLayout is not { } failed || failed == requested) return;
            _failedPointShadowTransientLayout = null;
            _pointShadowTransientFailureLogged = false;
        }

        void ApplyCompatiblePointShadowTransientLayout(PointShadowAtlas baseAtlas, int demandRows)
        {
            int targetRows = Math.Min(baseAtlas.Rows, Math.Max(0, demandRows));
            var requested = new PointShadowLayout(baseAtlas.FaceResolution, targetRows);
            ClearTransientRefusalForChangedDemand(requested);
            if (targetRows <= PointShadowTransientRows)
            {
                PublishResolvedPointShadows(true, false, null);
                return;
            }
            if (_failedPointShadowTransientLayout == requested)
            {
                PublishResolvedPointShadows(true, true, TransientRefusalReason(requested));
                return;
            }

            PointShadowAtlas? candidate = PointShadowAtlas.TryCreate(_gd, requested.FaceResolution, targetRows);
            if (candidate is null)
            {
                FailPointShadowTransientLayout(requested);
                return;
            }
            if (BindPointShadowAtlasesToReceivers(baseAtlas.Texture, candidate.Texture) == PointShadowBindResult.Failed)
            {
                _model.ForgetPointShadowBindFailure(candidate.Texture);
                candidate.Dispose();
                FailPointShadowTransientLayout(requested);
                return;
            }

            _pointShadowTransientAtlas?.Dispose();
            _pointShadowTransientAtlas = candidate;
            PublishResolvedPointShadows(true, false, null);
        }

        static string TransientRefusalReason(PointShadowLayout requested) =>
            $"transient point-shadow atlas at {requested.FaceResolution} by {requested.Rows} refused. "
            + "Retaining the live compatible atlas or its white default.";

        void FailPointShadowTransientLayout(PointShadowLayout requested)
        {
            _failedPointShadowTransientLayout = requested;
            string reason = TransientRefusalReason(requested);
            PublishResolvedPointShadows(true, true, reason);
            if (_pointShadowTransientFailureLogged) return;
            _pointShadowTransientFailureLogged = true;
            _shadowReconfigureLogger.Error(reason);
        }

        void PublishResolvedPointShadows(bool enabled, bool degraded, string? reason)
        {
            _resolvedPointShadows = new PointShadowResolution(enabled, PointShadowFaceResolution, PointShadowRows,
                degraded, reason, _pointShadowAtlas?.ByteSize ?? 0L, PointShadowTransientRows,
                _pointShadowTransientAtlas?.ByteSize ?? 0L);
        }

        /// <summary>Bring up <paramref name="wanted"/> and put every receiver on it. Either the whole thing lands
        /// or nothing does.
        /// <para>
        /// THE ORDER IS THE SAFETY PROPERTY, and it is the cascade replacement's order
        /// (<c>ModelRenderer.ShadowLayoutReplacement.cs</c>): build the new pair, bind the receivers to it, and
        /// only then retire the old one. So both ways this can fail leave the previous atlas allocated, bound and
        /// shadowing, which is what the design promises. Retiring first made that promise true for an allocation
        /// refusal only: a bind that then failed had nothing left to fall back to, and every receiver set (and the
        /// renderer's own handle, which the next cascade reconfigure copies into fresh sets) was naming a freed
        /// texture.
        /// </para></summary>
        void AttemptPointShadowLayout(PointShadowLayout wanted, int transientDemand)
        {
            if (BuildPointShadowReplacement(wanted.FaceResolution, wanted.Rows) is not { } replacement)
            {
                // The previous atlas is untouched by a refusal, so a scene that was already shadowing carries on
                // shadowing at the layout it had.
                FailPointShadowLayout(wanted, "the device refused the atlas or its pass");
                return;
            }

            int transientRows = ResolveTransientRowsForBaseReplacement(
                PointShadowTransientRows, transientDemand, wanted.Rows);
            var transientShape = new PointShadowLayout(wanted.FaceResolution, transientRows);
            ClearTransientRefusalForChangedDemand(transientShape);
            bool transientRefused = false;
            if (transientRows > 0)
            {
                if (_failedPointShadowTransientLayout == transientShape)
                    transientRefused = true;
                else
                {
                    replacement.TransientAtlas = PointShadowAtlas.TryCreate(_gd, wanted.FaceResolution, transientRows);
                    transientRefused = replacement.TransientAtlas is null;
                }
            }

            if (BindPointShadowAtlasesToReceivers(
                    replacement.Atlas.Texture, replacement.TransientAtlas?.Texture) == PointShadowBindResult.Failed)
            {
                // Nothing may sample an atlas the receivers are not bound to, so the one just built goes back
                // whole and the live one keeps its place: still allocated, still bound, still drawing the rows
                // the slot cache is holding. The renderer's failure latch is dropped with the texture it names,
                // because a freed atlas cannot be asked for again and the latch that stops THIS layout being
                // retried is the scene's own, below.
                _model.ForgetPointShadowBindFailure(replacement.Atlas.Texture);
                if (replacement.TransientAtlas is { } rejectedTransient)
                    _model.ForgetPointShadowBindFailure(rejectedTransient.Texture);
                replacement.Dispose();
                FailPointShadowLayout(wanted, "the receiver sets could not be rebuilt against the atlas");
                return;
            }

            CommitPointShadowReplacement(replacement);
            AdoptPointShadowSlotCache(wanted.Rows);
            if (transientRefused) FailPointShadowTransientLayout(transientShape);
            else PublishResolvedPointShadows(true, false, null);
        }

        /// <summary>Latch a refused layout, publish the degraded resolution over whatever is still live, and say
        /// so in the log ONCE. The boundary re-reaches this decision every frame the request stands, and a line a
        /// frame would bury the log it is supposed to be a signal in.</summary>
        void FailPointShadowLayout(PointShadowLayout wanted, string what)
        {
            _failedPointShadowLayout = wanted;
            bool live = _pointShadowAtlas is not null;
            string retained = live ? $"{PointShadowFaceResolution} by {PointShadowRows}" : "no point shadows";
            string reason =
                $"point-shadow atlas at {wanted.FaceResolution} by {wanted.Rows} refused ({what}). "
                + $"Retaining {retained}.";
            PublishResolvedPointShadows(live, true, reason);
            if (_pointShadowFailureLogged) return;
            _pointShadowFailureLogged = true;
            _shadowReconfigureLogger.Error(reason);
        }

        /// <summary>Give the atlas back: unbind it to the 1x1 default, free it, and forget every row. The frame
        /// path is left with nothing to sample and nothing to schedule, which is the same state a scene that
        /// never asked for a point shadow is in.</summary>
        void ReleasePointShadows()
        {
            _pointShadowLayoutPending = false;
            _pointShadowStaticRequests = 0;
            _failedPointShadowLayout = null;
            _pointShadowFailureLogged = false;
            _failedPointShadowTransientLayout = null;
            _pointShadowTransientFailureLogged = false;
            _pointShadowTransientDemandRows = 0;
            if (_pointShadowAtlas is null)
            {
                // Nothing is allocated and the default is already bound, so the disabled path costs a compare.
                PublishResolvedPointShadows(false, false, null);
                return;
            }

            if (BindPointShadowAtlasesToReceivers(null, null) == PointShadowBindResult.Failed)
            {
                // The receivers still hold the atlas, so it cannot be freed. Nothing samples it (the frame path
                // clears the tail), but the memory stays until a rebind can be built.
                _model.ClearPointShadowUniforms();
                PublishResolvedPointShadows(false, true,
                    "point shadows were turned off but the receiver sets could not be rebuilt, so the "
                    + "atlas is still allocated.");
                return;
            }

            DisposePointShadows();
            DropPointShadowSlotCache();
            _model.ClearPointShadowUniforms();
            PublishResolvedPointShadows(false, false, null);
        }

        /// <summary>Rebuild every receiver set against <paramref name="baseAtlas"/> and
        /// <paramref name="transientAtlas"/> (or their 1x1 defaults when null).
        /// The same two arguments the cascade replacement takes, for the same reason: a resource set is immutable,
        /// so one changed binding means building replacements and handing them back to their holders.</summary>
        PointShadowBindResult BindPointShadowAtlasesToReceivers(
            IGpuTexture? baseAtlas, IGpuTexture? transientAtlas)
        {
            var liveSets = new List<IGpuResourceSet>();
            CollectLiveMaterialSets(liveSets);
            return _model.BindPointShadowAtlases(baseAtlas, transientAtlas, liveSets, CommitMaterialSets);
        }

        /// <summary>Fit the slot cache to a freshly allocated atlas. A changed row count is a different cache
        /// outright. The same row count keeps its owners but forgets their CONTENTS, because the texture those
        /// rows lived in has been freed.</summary>
        void AdoptPointShadowSlotCache(int rows)
        {
            if (_pointSlotCache is { } cache && cache.Capacity == rows)
            {
                cache.InvalidateEveryRow();
                Array.Clear(_pointCasterSignatures);
                return;
            }

            _pointSlotCache = new PointShadowSlots(rows);
            _pointCasterSignatures = new long[rows];
        }

        void DropPointShadowSlotCache()
        {
            _pointSlotCache = null;
            _pointCasterSignatures = Array.Empty<long>();
        }

        /// <summary>One atlas shape: a face size and a row count. The pair the whole reconfigure path compares by,
        /// so "the same layout" is one definition rather than two ints tested in four places.</summary>
        readonly record struct PointShadowLayout(int FaceResolution, int Rows);
    }
}
