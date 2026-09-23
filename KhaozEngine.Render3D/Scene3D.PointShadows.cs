using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The point-light shadow INTEGRATION half of <see cref="Scene3D"/>: the one per-frame method that turns this
    /// frame's queued <see cref="LightShadow"/> requests into atlas rows, decides which of those rows are re-drawn
    /// under the budgets, records the pass, and hands the receiver its slot table.
    /// <para>
    /// The pass itself is <c>Scene3D.PointShadowPass.cs</c> and the slot bookkeeping is
    /// <see cref="PointShadowSlots"/>. What is here is the SCHEDULING between them, and nothing else: which lights
    /// asked, which ones fit, which rows are stale, and what the shader is allowed to sample.
    /// </para>
    /// <para>
    /// LAZY BY CONSTRUCTION (design decision 6). A frame in which point shadows are off, or in which no queued
    /// light asks for one, clears the receiver tail and returns having allocated nothing at all, so a game that
    /// never asks pays neither the atlas nor the pipelines.
    /// </para>
    /// <para>
    /// AND IT ALLOCATES NOTHING ITSELF. The first frame to carry a request records the layout it wanted and
    /// renders unshadowed, <c>Scene3D.PointShadowReconfigure.cs</c> brings that atlas up at the next frame
    /// boundary, and the frame after it carries the map. Everything about creating, reshaping, rebinding and
    /// releasing the atlas lives there, because all of it stalls the device and rebuilds the material sets the
    /// model pass is about to bind.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        // This frame's requesting lights, keyed statics first in stable-key order and then dynamic effects nearest
        // first, plus the subset whose rows are re-drawn. Reused so an ordinary shadowed frame allocates nothing.
        readonly List<PointShadowRequest> _pointRequests = new();
        readonly List<int> _pointRebuilds = new();

        // The receiver's slot table, INDEX-ALIGNED WITH THE COMPLETE UPLOADED LIGHT ORDER. Never the request order:
        // static requests sort by stable key and dynamics by distance, while the receiver indexes queue order.
        int[] _pointSlotUniform = Array.Empty<int>();

        PointShadowSlots? _pointSlotCache;
        // One caster signature per atlas row, parallel to the cache. Kept here rather than in the cache because it
        // is the SCENE's idea of what is under a light, and the cache is deliberately geometry-free.
        long[] _pointCasterSignatures = Array.Empty<long>();
        int _pointShadowFrame;

        /// <summary>How many point lights actually carried a shadow map on the last rendered frame, which is at or
        /// below how many asked. Every keyed static request has a reserved row, while dynamic requests use the
        /// configured effect budget. A row not drawn yet is not counted because it samples nothing.</summary>
        public int PointShadowedLights { get; private set; }

        /// <summary>
        /// This frame's whole point-shadow decision: gather the requests, budget them, render the rows that need
        /// it and publish the receiver's slot table. Called once per frame from <c>RenderInternal</c>, after the
        /// instances are grouped and uploaded (the pass reuses that buffer) and after the key light's depth pass,
        /// and before the model pass binds its material sets, because a first allocation rebuilds every one of
        /// them and because <c>SetFrameUniforms</c> uploads the tail written here.
        /// </summary>
        /// <param name="cl">This frame's open command list. The pass is recorded into it, not submitted.</param>
        /// <param name="eyeAbsolute">The camera in absolute space, which the budget ranks requests by.</param>
        /// <remarks>Internal rather than private only so the allocation test can measure this one method on a
        /// steady-state frame. Nothing outside <c>RenderInternal</c> calls it in a real frame.</remarks>
        internal void PreparePointShadows(IGpuCommandList cl, Vector3 eyeAbsolute)
        {
            PointShadowedLights = 0;
            int frame = ++_pointShadowFrame;
            PointShadowSettings settings = Post.Quality.Shadows.PointShadows;

            if (!GatherPointShadowRequests(settings, eyeAbsolute))
            {
                // NOTHING ASKED, so nothing is allocated. Releasing the unrequested rows keeps a cache built by an
                // earlier frame from holding rows for lights that have stopped being queued.
                _model.ClearPointShadowUniforms();
                _pointSlotCache?.ReleaseUnrequested(frame);
                return;
            }

            // A REQUEST IS WHAT MAKES THE ATLAS WANTED, and that is all this frame does about allocation. Nothing
            // is created here: the allocation, the receiver rebind and the stall they carry are frame-boundary
            // work (ApplyPendingPointShadowLayout), so the FIRST frame to ask renders unshadowed and the next one
            // carries the map. A refused layout is latched there too, so a device that cannot have it is asked
            // once rather than once a frame.
            _pointShadowLayoutPending = true;
            if (_pointShadowAtlas is null || _pointSlotCache is not { } cache)
            {
                _model.ClearPointShadowUniforms();
                return;
            }

            // Whatever the settings now say, this frame renders into the atlas that EXISTS. A layout the boundary
            // has not brought up yet (or refused outright) leaves the previous one live, and a frame that
            // second-guessed it here would throw away a working map over a number nothing has acted on.
            // THE CASTERS ARE INDEXED ONCE, here, where a request and a live atlas both exist. Every static
            // signature and every row the pass draws below asks this one index rather than walking every instance.
            EnsurePointCasterIndex();
            AcquirePointShadowSlots(cache, frame);
            ChoosePointShadowRebuilds(cache, settings);
            int draws = RenderChosenPointShadowRows(cl, cache, frame);
            PublishPointShadowUniforms(cache, settings, frame);
            cache.ReleaseUnrequested(frame);
            RecordPointShadowDiagnostics(cache, draws);
        }

        /// <summary>
        /// Give every request a row, and decide which of the static ones changed underneath. A static light's
        /// signature is compared here rather than in the cache because it is a question about the SCENE (which
        /// casters stand inside this light) rather than about the cache's bookkeeping.
        /// <para>
        /// IN TWO PHASES, AND THE ORDER IS THE WHOLE POINT. Every request that already owns a row is re-seated
        /// first, then the rest are offered rows. Static requests are sorted by stable key, so a one-pass acquire
        /// could let a new earlier key evict an incumbent that is about to ask later in the list, which then evicts
        /// the next one. Re-seating first makes every incumbent's row "requested this frame", and a row requested
        /// this frame is never a victim.
        /// </para>
        /// </summary>
        void AcquirePointShadowSlots(PointShadowSlots cache, int frame)
        {
            // A dynamic light has no identity across frames, so its row carries nothing across one either. Handing
            // the rows back BEFORE phase one is what stops a light that inherited an expired light's queue index
            // from re-seating itself onto that light's map, and it puts the row back in front of the static lights
            // for the frames the dynamic budget cannot reach it.
            cache.ReleaseDynamicRows();

            for (int i = 0; i < _pointRequests.Count; i++)
            {
                PointShadowRequest request = _pointRequests[i];
                request.Slot = cache.TryTouch(request.Key, request.Mode, frame);
                _pointRequests[i] = request;
            }

            for (int i = 0; i < _pointRequests.Count; i++)
            {
                PointShadowRequest request = _pointRequests[i];
                if (request.Slot >= 0) continue;
                request.Slot = cache.Acquire(request.Key, request.Mode, frame);
                _pointRequests[i] = request;
            }

            for (int i = 0; i < _pointRequests.Count; i++)
            {
                PointShadowRequest request = _pointRequests[i];
                if (request.Slot < 0 || request.Mode != LightShadowMode.Static) continue;
                long signature = PointCasterSignature(request.Position, request.Radius, request.NearRadius,
                    request.ExclusionMin, request.ExclusionMax);
                // A changed signature can only ADD dirt. A freshly acquired row is already dirty, so a stale
                // signature left behind by the row's previous owner cannot accidentally report it clean.
                if (_pointCasterSignatures[request.Slot] != signature) cache.MarkDirty(request.Slot);
                _pointCasterSignatures[request.Slot] = signature;
            }
        }

        /// <summary>Pick this frame's render list into <see cref="_pointRebuilds"/>: the dirty static rows, stalest
        /// first, up to <see cref="PointShadowSettings.MaxStaticRebuildsPerFrame"/>, then the dynamic rows, nearest
        /// first, up to <see cref="PointShadowSettings.MaxDynamicLightsPerFrame"/>. A static row the budget defers
        /// keeps sampling the map it already has, which is exactly the trade the cache exists for.</summary>
        void ChoosePointShadowRebuilds(PointShadowSlots cache, PointShadowSettings settings)
        {
            _pointRebuilds.Clear();
            int statics = Math.Max(0, settings.MaxStaticRebuildsPerFrame);
            int dynamics = Math.Max(0, settings.MaxDynamicLightsPerFrame);

            // Stalest first, and a row with nothing on it sorts ahead of every rendered one (LastRenderedFrame is
            // int.MinValue there), so a light that has never been drawn is never starved by one that has.
            while (_pointRebuilds.Count < statics)
            {
                int pick = -1;
                for (int i = 0; i < _pointRequests.Count; i++)
                {
                    PointShadowRequest r = _pointRequests[i];
                    if (r.Mode != LightShadowMode.Static || r.Slot < 0) continue;
                    if (!cache.IsDirty(r.Slot) || _pointRebuilds.Contains(i)) continue;
                    if (pick < 0 || cache.LastRenderedFrame(r.Slot) < cache.LastRenderedFrame(_pointRequests[pick].Slot))
                        pick = i;
                }
                if (pick < 0) break;
                _pointRebuilds.Add(pick);
            }

            int dynamicsTaken = 0;
            for (int i = 0; i < _pointRequests.Count && dynamicsTaken < dynamics; i++)
            {
                PointShadowRequest r = _pointRequests[i];
                if (r.Mode != LightShadowMode.Dynamic || r.Slot < 0 || !cache.IsDirty(r.Slot)) continue;
                _pointRebuilds.Add(i);
                dynamicsTaken++;
            }
        }

        /// <summary>Record the pass for everything <see cref="ChoosePointShadowRebuilds"/> picked and mark those
        /// rows clean. Returns the caster draw calls issued, which is what the diagnostics report.</summary>
        int RenderChosenPointShadowRows(IGpuCommandList cl, PointShadowSlots cache, int frame)
        {
            BeginPointShadowFrame(_pointRebuilds.Count);
            if (_pointRebuilds.Count == 0) return 0;

            for (int packed = 0; packed < _pointRebuilds.Count; packed++)
            {
                PointShadowRequest r = _pointRequests[_pointRebuilds[packed]];
                PackPointShadowSlot(packed, r.Slot, r.Position, r.Radius, r.NearRadius,
                    r.ExclusionMin, r.ExclusionMax);
            }
            UploadPointShadowFaces(cl);
            int draws = RenderPointShadowSlots(cl);
            foreach (int index in _pointRebuilds) cache.MarkClean(_pointRequests[index].Slot, frame);
            return draws;
        }

        /// <summary>
        /// Write the receiver's slot table. A light gets -1 (sample nothing) unless its row has actually been
        /// drawn into: a row acquired this frame but deferred by the rebuild budget holds whatever the allocation
        /// left in it, and a receiver sampling that would read noise rather than a shadow.
        /// <para>
        /// A DYNAMIC LIGHT IS HELD TO A STRICTER TEST: its row must have been drawn THIS frame. It has no identity
        /// across frames (it is keyed by its place in the light queue), so a row rendered on an earlier frame is
        /// not evidence that it was rendered for THIS light, and a dynamic light past the per-frame dynamic budget
        /// renders unshadowed rather than casting somebody else's shadow. Its row is released at the start of the
        /// acquire for the same reason, so the two halves agree by construction.
        /// </para>
        /// <para>
        /// It does not BIND anything. The atlas standing here is one the frame boundary already put on every
        /// receiver set, which is why this frame was allowed to render into it at all.
        /// </para>
        /// </summary>
        void PublishPointShadowUniforms(PointShadowSlots cache, PointShadowSettings settings, int frame)
        {
            Array.Fill(_pointSlotUniform, -1);
            foreach (PointShadowRequest r in _pointRequests)
            {
                if (r.Slot < 0) continue;
                bool sampleable = r.Mode == LightShadowMode.Dynamic
                    ? cache.LastRenderedFrame(r.Slot) == frame
                    : cache.EverRendered(r.Slot);
                if (!sampleable) continue;
                _pointSlotUniform[r.LightIndex] = r.Slot;   // the UPLOADED light order, not the request order
                PointShadowedLights++;
            }
            _model.SetPointShadowUniforms(_pointSlotUniform.AsSpan(0, _lights.Count), settings.ResolvedBias,
                settings.ResolvedSlopeBias,
                PointShadowFaceResolution, PointShadowRows, settings.Filter, settings.ResolvedLightSizeMetres,
                settings.ResolvedMaxPenumbraTexels);
        }

        /// <summary>Fold this frame's point-shadow counters into the shadow diagnostics snapshot. The key light's
        /// half of the snapshot was published before this pass ran, so it is extended rather than rebuilt.</summary>
        void RecordPointShadowDiagnostics(PointShadowSlots cache, int draws)
        {
            int statics = 0, dynamics = 0;
            foreach (int index in _pointRebuilds)
                if (_pointRequests[index].Mode == LightShadowMode.Static) statics++; else dynamics++;
            _lastShadowPassDiagnostics = _lastShadowPassDiagnostics with
            {
                PointShadowedLights = PointShadowedLights,
                PointStaticRebuilds = statics,
                PointDynamicRenders = dynamics,
                PointFaceDrawCalls = draws,
                PointSlotsInUse = cache.InUse,
            };
        }

        /// <summary>
        /// A 64-bit signature of everything that would be drawn into one static light's map: every rigid caster
        /// standing inside the light sphere, by mesh identity, world matrix, cast kind and dissolve threshold,
        /// combined with the light's own position, radius, near radius and exclusion box quantised to a millimetre.
        /// <para>
        /// THE TWO CLEARANCES BELONG HERE because they decide what the map CONTAINS: either one takes the light's
        /// own fixture out of its own shadow. A row rendered at one clearance is not the map the same key asks for
        /// at another, so a signature that ignored them would answer a changed request with the old picture for
        /// ever.
        /// </para>
        /// <para>
        /// It reads the same index query <c>BuildPointCasterSpans</c> reads, so a signature can only miss a change
        /// that the pass would also not have drawn.
        /// </para>
        /// <para>
        /// ONLY THE LIGHT IS QUANTISED, and that is the whole of what the millimetre rounding buys: a light
        /// parented to a jittering transform does not rebuild its own map for a move nothing can see. A CASTER is
        /// compared by the raw bits of its matrix, so a caster that jitters re-renders every light it stands
        /// inside, every frame. That is accepted rather than overlooked: the cost is bounded by
        /// <see cref="PointShadowSettings.MaxStaticRebuildsPerFrame"/> and the oldest-first order, so a jittering
        /// caster spends the static budget and delays the other lights rather than multiplying the work.
        /// </para>
        /// </summary>
        long PointCasterSignature(Vector3 lightPosAbsolute, float radius, float nearRadius,
            Vector3 exclusionMin, Vector3 exclusionMax)
        {
            ulong hash = PointSignatureSeed;
            MixPointSignature(ref hash, Quantise(lightPosAbsolute.X));
            MixPointSignature(ref hash, Quantise(lightPosAbsolute.Y));
            MixPointSignature(ref hash, Quantise(lightPosAbsolute.Z));
            MixPointSignature(ref hash, Quantise(radius));
            MixPointSignature(ref hash, Quantise(nearRadius));
            MixPointSignature(ref hash, Quantise(exclusionMin.X));
            MixPointSignature(ref hash, Quantise(exclusionMin.Y));
            MixPointSignature(ref hash, Quantise(exclusionMin.Z));
            MixPointSignature(ref hash, Quantise(exclusionMax.X));
            MixPointSignature(ref hash, Quantise(exclusionMax.Y));
            MixPointSignature(ref hash, Quantise(exclusionMax.Z));

            // The casters come from the frame's index (Scene3D.PointCasters.cs): exactly the ones the pass will draw
            // for this light, in ascending slot order, which is the order the walk over every run visited them in.
            // Read by reference: an InstanceData is 128 bytes and this runs once per touching caster per static
            // request per frame.
            QueryPointCasters(lightPosAbsolute, radius, nearRadius, exclusionMin, exclusionMax);
            Span<ModelRenderer.InstanceData> instances = CollectionsMarshal.AsSpan(_instanceData);
            foreach (int slot in _pointCasterHits)
            {
                MeshHandle mesh = _runs[_pointCasterIndex.RunOf(slot)].Mesh;
                ref ModelRenderer.InstanceData data = ref instances[slot];
                MixPointSignature(ref hash, SignatureWord((uint)mesh.Index, (uint)mesh.Generation));
                MixPointSignature(ref hash, SignatureWord(FloatBits(data.Dissolve.X), (uint)PointCasterKind(slot)));
                MixPointSignature(ref hash, data.Model);
            }
            return unchecked((long)hash);
        }

        /// <summary>Whether one instance's world bounding sphere reaches into a light's shadowing shell, which is
        /// the ONE definition of "this caster takes part in this light's map" (design decision 8). Shared by the
        /// frame's <see cref="PointCasterIndex"/>, and through it by the pass's caster cull and the signature above,
        /// so the two can never disagree about which instances a light's map depends on.
        /// <para>
        /// The shell has an inner wall as well as an outer one. An instance lying WHOLLY inside
        /// <paramref name="nearRadius"/> is the light's own fixture and every one of its fragments would be
        /// discarded, so it is dropped here instead and never costs six faces of draws. A fixture the clearance
        /// only reaches part way into (a lantern on a bracket, a lamp on a post) still draws, and the fragments
        /// near the bulb are what the shader throws away.
        /// </para>
        /// <para>
        /// THE EXCLUSION BOX IS THE SAME RULE IN THE OTHER SHAPE, and it is read against the instance's world
        /// SPHERE rather than its oriented box, so the two clearances answer the same question about the same
        /// volume and a caster is never dropped that a fragment would have kept. A fixture the box only reaches
        /// part way into still draws and loses its fragments in the shader.
        /// </para></summary>
        internal static bool InstanceTouchesLight(in MeshBounds bounds, in Matrix4x4 model,
            Vector3 lightPosAbsolute, float radius, float nearRadius = 0f,
            Vector3 exclusionMin = default, Vector3 exclusionMax = default)
        {
            bounds.WorldSphere(model, out Vector3 centre, out float r);
            return InstanceTouchesLight(centre, r, lightPosAbsolute, radius, nearRadius, exclusionMin, exclusionMax);
        }

        /// <summary>The same test on a world sphere already in hand, which is what the frame's
        /// <see cref="PointCasterIndex"/> stores for every caster. The overload above transforms the bounds and asks
        /// this one, so the index, the signature and the pass all read one definition of touching a light.</summary>
        internal static bool InstanceTouchesLight(Vector3 centre, float sphereRadius,
            Vector3 lightPosAbsolute, float radius, float nearRadius = 0f,
            Vector3 exclusionMin = default, Vector3 exclusionMax = default)
        {
            float reach = sphereRadius + radius;
            float distanceSq = (centre - lightPosAbsolute).LengthSquared();
            if (distanceSq > reach * reach) return false;
            if (nearRadius > 0f)
            {
                // Wholly inside the clearance: the farthest point of the sphere is still nearer than the near
                // radius.
                float farthest = MathF.Sqrt(distanceSq) + sphereRadius;
                if (farthest <= nearRadius) return false;
            }
            if (!IsExclusionBox(exclusionMin, exclusionMax)) return true;
            return !(centre.X - sphereRadius >= exclusionMin.X && centre.X + sphereRadius <= exclusionMax.X
                && centre.Y - sphereRadius >= exclusionMin.Y && centre.Y + sphereRadius <= exclusionMax.Y
                && centre.Z - sphereRadius >= exclusionMin.Z && centre.Z + sphereRadius <= exclusionMax.Z);
        }

        /// <summary>Whether a corner pair is a real exclusion box: a volume with something inside it. The zero pair
        /// is how every "no box" request reaches the pass, and it answers false here, so one test covers both the
        /// unset case and a caller's own degenerate box.</summary>
        internal static bool IsExclusionBox(Vector3 min, Vector3 max) =>
            max.X > min.X && max.Y > min.Y && max.Z > min.Z;

        static ulong Quantise(float metres) => unchecked((ulong)(long)MathF.Round(metres * 1000f));   // millimetres

        // xxHash64's published primes. Fixed constants rather than System.HashCode, whose seed changes with every
        // process, so a signature, and so a rebuild count, is the same in every run.
        const ulong PointSignatureSeed = 0x27D4EB2F165667C5UL;
        const ulong PointSignaturePrimeA = 0x9E3779B185EBCA87UL;
        const ulong PointSignaturePrimeB = 0xC2B2AE3D27D4EB4FUL;

        /// <summary>
        /// Fold one whole 64-bit word into a signature with xxHash64's round: a multiply, a rotate and a multiply.
        /// The byte-wise FNV it replaced cost eight multiplies per word and was most of the point-shadow frame.
        /// <para>
        /// A ONE-WORD CHANGE CAN NEVER COLLIDE. For a fixed word the round is a bijection of the running value (an
        /// add, a rotate and an odd multiply), and for a fixed running value it is injective in the word (the word is
        /// multiplied by an odd prime), so a caster that moved, changed kind or changed mesh always changes its
        /// light's signature. A change in how many casters touch the light is left to the 64-bit odds.
        /// </para>
        /// Internal for the mixer tests.
        /// </summary>
        internal static void MixPointSignature(ref ulong hash, ulong word)
        {
            hash += word * PointSignaturePrimeB;
            hash = BitOperations.RotateLeft(hash, 31);
            hash *= PointSignaturePrimeA;
        }

        /// <summary>Fold a world matrix in as eight words, two elements each in row order, by their raw bits, so a
        /// caster that moves by one float step changes the signature.</summary>
        internal static void MixPointSignature(ref ulong hash, in Matrix4x4 model)
        {
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M11), FloatBits(model.M12)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M13), FloatBits(model.M14)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M21), FloatBits(model.M22)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M23), FloatBits(model.M24)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M31), FloatBits(model.M32)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M33), FloatBits(model.M34)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M41), FloatBits(model.M42)));
            MixPointSignature(ref hash, SignatureWord(FloatBits(model.M43), FloatBits(model.M44)));
        }

        static ulong SignatureWord(uint low, uint high) => low | (ulong)high << 32;

        static uint FloatBits(float value) => (uint)BitConverter.SingleToInt32Bits(value);

        /// <summary>One queued light asking for a map, with everything the budget and the pass need: where it is,
        /// how far it reaches, how much of its own fixture it clears as a sphere and as a box, what kind of map it
        /// wants, the cache key it is remembered by, how far it is from the eye (the budget's ranking) and which
        /// atlas row it ended up with.</summary>
        struct PointShadowRequest(int lightIndex, Vector3 position, float radius, float nearRadius,
            Vector3 exclusionMin, Vector3 exclusionMax, LightShadowMode mode, long key, float distanceSq)
        {
            /// <summary>Its index in the UPLOADED light order, which is the index the receiver's slot table is
            /// aligned to.</summary>
            public readonly int LightIndex = lightIndex;
            public readonly Vector3 Position = position;
            public readonly float Radius = radius;

            /// <summary>The metres of geometry around the bulb this light treats as its own fixture, which the
            /// pass leaves out of the map. Zero for a light in open space.</summary>
            public readonly float NearRadius = nearRadius;

            /// <summary>The same clearance as a world-space box, for a fixture a sphere cannot describe. The zero
            /// pair is no box.</summary>
            public readonly Vector3 ExclusionMin = exclusionMin;
            public readonly Vector3 ExclusionMax = exclusionMax;

            public readonly LightShadowMode Mode = mode;
            public readonly long Key = key;
            public readonly float DistanceSq = distanceSq;

            /// <summary>The atlas row it was given, or -1 when the cache had none to give.</summary>
            public int Slot = -1;
        }
    }
}
