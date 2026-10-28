using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Generation-owned composite residency. Draws and their witness change together after every resource is ready.</summary>
public sealed partial class MapRenderResidency : IDisposable
{
    readonly object _gate = new();
    readonly MapRenderBudgets _budgets;
    readonly MapRenderAdmission _admission;
    readonly MapRenderBuildQueue _queue;
    readonly Dictionary<MapSceneData, MapRenderAdmission.CpuLease> _metadataCharges = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<MapAssetExtentProofSet, MapRenderAdmission.CpuLease> _proofCharges = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<MapAssetClosure, MapRenderAdmission.CpuLease> _encodedCharges = new(ReferenceEqualityComparer.Instance);
    readonly List<Pending> _cancelled = [];
    readonly Queue<Owner> _retired = new();
    readonly Dictionary<Identity, Owner> _owners = new();
    readonly Dictionary<MapResourceReference, Owner> _acceptedReferences = new();
    readonly CompositeSink _recording = new();
    MapRenderAdmission.CpuLease? _recordingCharge;
    int _recordingCapacity;
    MapRenderReadiness? _lastReadiness;
    Pending? _pending;
    Batch? _accepted;
    MapRenderSourceState? _acceptedState;
    MapRenderTransition _transition = new(0, MapSceneStatus.Incomplete, null, null);
    bool _disposed;
    public IMapRenderDevice Device { get; }
    public MapRenderSourceState DesiredState { get; private set; }
    public MapRenderLifetime Lifetime { get; } = new();
    internal MapRenderTransition CurrentTransition => _transition;
    internal MapRenderAssetCache AssetCache { get; }
    internal IReadOnlyList<MapRenderPlan> AcceptedPlans => _accepted?.Plans ?? [];
    public MapRenderResidency(MapRenderSourceState initial, IMapRenderDevice device, MapRenderBudgets? budgets = null)
        : this(initial, device, budgets, TaskScheduler.Default) { }
    internal MapRenderResidency(MapRenderSourceState initial, IMapRenderDevice device, MapRenderBudgets? budgets, TaskScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(initial); ArgumentNullException.ThrowIfNull(device); ArgumentNullException.ThrowIfNull(scheduler);
        _budgets = budgets ?? new(); _budgets.Validate(); Device = device; DesiredState = initial;
        _admission = new(_budgets, Lifetime); AssetCache = new(_admission); _queue = new(_budgets, Lifetime, scheduler);
        RequestReplacement(initial);
    }
    public MapRenderTransition RequestReplacement(MapRenderSourceState proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        lock (_gate)
        {
            RequireLive(); long epoch = checked(_transition.Epoch + 1);
            CancelPending();
            try
            {
                Validate(proposal);
                proposal = proposal with { Windows = Array.AsReadOnly(proposal.Windows.ToArray()) };
                DesiredState = proposal;
                var witness = Witness(proposal, epoch, null);
                _pending = new(proposal, epoch);
                _transition = new(epoch, MapSceneStatus.Incomplete, _transition.AcceptedWitness, witness);
                Schedule();
            }
            catch (Exception error) when (Refusal(error) is not null)
            { CancelPending(); Refuse(Refusal(error)!.Value, epoch, error); }
            return _transition;
        }
    }
    public MapRenderTransition Invalidate(MapRenderSourceState proposal, MapNativeEditEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        lock (_gate)
        {
            RequireLive();
            foreach (var plan in AcceptedPlans) MapFoliageRenderCache.For(plan.FoliageContext).Invalidate(effects);
            return RequestReplacement(proposal);
        }
    }
    public MapRenderTransition SetWindows(IReadOnlyList<MapSceneScope> scopes)
    { ArgumentNullException.ThrowIfNull(scopes); lock (_gate) return RequestReplacement(DesiredState with { Windows = Array.AsReadOnly(scopes.ToArray()) }); }
    public MapRenderReadiness Update()
    {
        lock (_gate)
        {
            RequireLive(); DrainCancelled();
            try
            {
                Schedule();
                if (_pending is { Job.Completion.IsCompleted: true } pending && pending.Batch is null)
                {
                    PrepareCompleted(pending);
                }
                if (_pending?.Batch is not null) Advance(_pending);
            }
            catch (Exception error) when (Refusal(error) is not null)
            {
                long epoch = _transition.Epoch; CancelPending(); Refuse(Refusal(error)!.Value, epoch, error);
            }
            DrainRetirement(); AssetCache.TryTrim(); TrimRetention();
            return SnapshotReadiness();
        }
    }
    void PrepareCompleted(Pending pending)
    {
        var plans = pending.Job!.Completion.GetAwaiter().GetResult();
        _queue.Remove(pending.Job);
        pending.Batch = new(plans, pending.Job.Charge, pending.SourceCharge!);
        pending.SourceCharge = null;
        pending.Job = null;
        Fence(pending);
        _transition = _transition with { PendingWitness = Witness(pending.State, pending.Epoch, plans) };
        foreach (var resource in plans.SelectMany(p => p.Resources))
        {
            var id = Identity.Of(resource);
            if (pending.Resources.ContainsKey(id)) continue;
            pending.Resources.Add(id, resource);
            if (_owners.TryGetValue(id, out var existing)) pending.Owners.Add(id, existing);
        }
        if (Device is MapScene3DSink sink) sink.Declare(plans.SelectMany(p => p.Resources),
            plans.SelectMany(p => p.Resources).Where(r => pending.Owners.ContainsKey(Identity.Of(r)))
                .ToDictionary(r => r.Reference, r => pending.Owners[Identity.Of(r)].Handle));
    }
    void Validate(MapRenderSourceState state)
    {
        ArgumentNullException.ThrowIfNull(state.Source); ArgumentNullException.ThrowIfNull(state.Closure);
        ArgumentNullException.ThrowIfNull(state.Extents); ArgumentNullException.ThrowIfNull(state.Policy); ArgumentNullException.ThrowIfNull(state.Windows);
        if (!ReferenceEquals(state.Source.Data.Assets, state.Closure) || state.Source.ClosureHash != state.Closure.Hash || state.Extents.ClosureHash != state.Closure.Hash ||
            state.Source.RenderExtentsDigest != state.Extents.Digest || state.Source.WithRenderExtents(state.Extents).Source is null)
            throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Replacement requires the exact complete proof-bound source and closure.");
        if (DemandDiagnostics.Check(this, "MapRenderResidency.MaxActiveWindows.1", "MaxActiveWindows", 0, state.Windows.Count, _budgets.MaxActiveWindows, state.Windows.Count > _budgets.MaxActiveWindows, CapacityDemandLifetime.Inventory, allowanceSource: _budgets))
            throw new MapSceneCapacityException(nameof(MapRenderBudgets.MaxActiveWindows), state.Windows.Count, _budgets.MaxActiveWindows);
        foreach (var window in state.Windows) window.Validate();
        foreach (string id in state.Policy.MaterialResourceIds.Concat(state.Policy.TerrainMaterials.Values)) state.Closure.GetResource(id);
        foreach (string id in state.Policy.AnimatedAssets) state.Closure.GetAsset(id);
    }
    void Schedule()
    {
        if (_pending is { Job: null, Batch: null } pending) SchedulePending(pending);
    }
    void SchedulePending(Pending pending)
    {
        long bytes = Math.Min(_budgets.MaxQueuedBytes, pending.State.Source.Data.Limits.MaxPreparedBytes);
        int windows = pending.State.Windows.Count;
        if (windows == 0) bytes = 0;
        else if (_budgets.MaxPlanBytes <= bytes / windows) bytes = _budgets.MaxPlanBytes * windows;
        if (!_queue.CanEnqueue(1, bytes)) return;
        var source = pending.State.Source;
        var sourceCharge = RetainSource(pending.State);
        MapRenderAdmission.CpuLease charge;
        try { charge = _admission.ReserveCpu(bytes); } catch { sourceCharge.Dispose(); throw; }
        pending.SourceCharge = sourceCharge;
        pending.Job = _queue.Enqueue(1, bytes, token => Build(pending, bytes, charge, token), charge);
    }
    // The build reservation is the smaller of the queue and scene byte ceilings. Its plan-window share never binds before
    // a window does, because each window reports MaxPlanBytes itself and the build charges only inside windows.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    void DeriveBuild(MapSceneLimits buildLimits, string name, MapSceneLimits declared)
    {
        CapacityDemandOrigin[] origins = [.. MapDemandOrigins.Of(declared, nameof(MapSceneLimits.MaxPreparedBytes), declared.MaxPreparedBytes),
            new(nameof(MapRenderBudgets.MaxQueuedBytes), 0, _budgets.MaxQueuedBytes, _budgets)];
        MapDemandOrigins.Derive(buildLimits, nameof(MapSceneLimits.MaxPreparedBytes), origins);
        if (name != nameof(MapSceneLimits.MaxPreparedBytes)) MapDemandOrigins.Derive(buildLimits, name, origins);
    }
    MapRenderPlan[] Build(Pending pending, long bytes, MapRenderAdmission.CpuLease charge, CancellationToken token)
    {
        var plans = new List<MapRenderPlan>();
        var seenScopes = new HashSet<string>(StringComparer.Ordinal);
        var source = pending.State.Source;
        var buildLimits = source.Data.Limits with { MaxPreparedBytes = Math.Min(bytes, source.Data.Limits.MaxPreparedBytes) };
        string buildName = bytes < source.Data.Limits.MaxPreparedBytes && bytes == _budgets.MaxQueuedBytes ? nameof(MapRenderBudgets.MaxQueuedBytes) : nameof(MapSceneLimits.MaxPreparedBytes);
        if (DemandDiagnostics.IsObserved) DeriveBuild(buildLimits, buildName, source.Data.Limits);
        var admission = new MapSceneAdmission(buildLimits, preparationLimitName: buildName);
        using var buildDemandScope = DemandDiagnostics.Enter(admission, "MapRenderResidency.Build");
        using var activation = admission.Enter();
        try
        {
            foreach (var scope in pending.State.Windows)
            {
                token.ThrowIfCancellationRequested();
                using var demandScope = DemandDiagnostics.Enter(admission, "MapRenderResidency.Window");
                using var window = admission.LimitPreparation(_budgets.MaxPlanBytes, nameof(MapRenderBudgets.MaxPlanBytes));
                string scopeDigest = scope.Digest;
                if (!seenScopes.Add(scopeDigest)) continue;
                MapRenderContextLease? contextOwner;
                lock (_gate)
                {
                    Fence(pending);
                    contextOwner = AcceptedPlans.FirstOrDefault(p => ReferenceEquals(p.Scene.Source, source) && p.Key.SourceGeneration == source.SourceGeneration &&
                        p.Key.ClosureHash == source.ClosureHash && p.Key.ScopeDigest == scopeDigest && p.Input.ProofDigest == pending.State.Extents.Digest)?.RetainContext();
                }
                long reused = contextOwner?.CpuBytes ?? 0;
                try
                {
                    using var reusedContextView = admission.ReferenceWindow(reused);
                    using var attempt = admission.BeginPreparation();
                    var result = MapSceneAcquisition.Run(source, scope, admission);
                    if (result.Scene is not { } scene) throw new MapSceneSourceException(result.Status, string.Join("\n", result.Findings), result.Capacity);
                    if (contextOwner is null)
                    {
                        var captured = MapRenderPlan.CaptureFoliage(source, scope);
                        var receipt = captured.PreparationOwnership ?? throw new InvalidOperationException("Captured context has no ownership receipt.");
                        attempt.Publish(receipt);
                        contextOwner = new(captured, charge.Split(receipt.Bytes));
                    }
                    var context = contextOwner.Context;
                    var routing = MapPlacementRouting.Create(scene, pending.State.Policy.Placements, pending.State.Policy.AnimatedAssets).Routing
                        ?? throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Composite routing refused before selected decode.");
                    var cells = MapPlacementCellInventory.Capture(scene, routing);
                    var selected = MapRenderPlan.SelectedAssets(scene, context, cells);
                    var materials = MapRenderPlan.SelectedMaterials(scene, pending.State.Policy);
                    lock (_gate) Fence(pending);
                    token.ThrowIfCancellationRequested();
                    var decode = new MapRenderDecodeAllowance(admission, _admission, _budgets, pending.State.DecodeLimits);
                    var loaded = AssetCache.Acquire(pending.State.Closure, selected, materials, pending.State.Extents, decode.Budget, token, pending.SourceCharge!.Encoded, pending.State.AssetLimits);
                    if (loaded.Capacity is { } capacity) throw new MapSceneCapacityException(decode.Resolve(capacity));
                    if (loaded.Input is not { } input) throw new MapRenderAssetRefusal(loaded.Status, loaded.Detail ?? "Selected input refused.");
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        var plan = MapRenderPlan.Prepare(source, scene, input, pending.State.Policy, pending.Epoch, admission, context, contextOwner,
                            retained => contextOwner!.Own(charge.Split(retained)), cells);
                        attempt.Publish(scene.PreparationOwnership!); attempt.Publish(plan.PreparationOwnership!);
                        plans.Add(plan); contextOwner = null;
                    }
                    catch { if (!plans.Any(p => ReferenceEquals(p.Input, input))) input.Dispose(); throw; }
                }
                finally { contextOwner?.Dispose(); }
            }
            token.ThrowIfCancellationRequested();
            charge.ReduceTo(MapRenderPreparationInventory.Bytes(plans));
            admission.ReportRetained();
            return plans.ToArray();
        }
        catch { foreach (var plan in plans) plan.Dispose(); throw; }
    }
    void Advance(Pending pending)
    {
        Fence(pending);
        if (!pending.UploadsAdmitted)
        {
            pending.UploadAdmission ??= MapRenderUploadPreflight.Measure(pending.Resources.Where(p => !pending.Owners.ContainsKey(p.Key)).Select(p => Device.MeasureUploadCapacity(p.Value)),
                _budgets, pending.Batch!.Plans.Sum(p => p.Resources.Count), _recordingCapacity);
            var capacity = pending.UploadAdmission.Validate(Lifetime, _admission.HasRetiringUploads || _cancelled.Count > 0 || _retired.Count > 0);
            if (capacity is not null) { PendingCapacity(capacity); return; }
            pending.UploadsAdmitted = true;
        }
        foreach (var upload in pending.Uploads.Values) _ = upload.Ready;
        if (pending.PublishNext)
        {
            Publish(pending); return;
        }
        int applies = 0;
        foreach (var (id, resource) in pending.Resources)
        {
            if (pending.Owners.ContainsKey(id) || pending.Uploads.ContainsKey(id)) continue;
            if (DemandDiagnostics.Check(this, "MapRenderResidency.Applies", nameof(MapRenderBudgets.MaxAppliesPerUpdate), applies, 1, _budgets.MaxAppliesPerUpdate, applies >= _budgets.MaxAppliesPerUpdate, allowanceSource: _budgets)) break;
            Fence(pending);
            if (Device is IMapRenderUploadDependencies dependencies && !dependencies.CanBeginUpload(resource)) continue;
            var demand = Device.MeasureUpload(resource);
            using var reservation = _admission.TryReserve(demand, out var capacity);
            if (reservation is null) { PendingCapacity(capacity!); break; }
            // The upload may retain CPU inputs after cancellation until its renderer destruction callback.
            var borrow = pending.Batch!.Plans.First(p => p.Resources.Any(r => r.Reference == resource.Reference)).Input.Retain();
            reservation.OnRelease(borrow.Dispose);
            try { pending.Uploads.Add(id, new(Device.BeginUpload(resource, reservation), reservation)); }
            catch { reservation.Dispose(); throw; }
            applies++;
        }
        long available = _budgets.MaxUploadBytesPerUpdate;
        foreach (var upload in pending.Uploads.Values)
        {
            Fence(pending);
            long written = upload.Advance(available);
            if (DemandDiagnostics.Check(this, "MapRenderResidency.UploadBytes", nameof(MapRenderBudgets.MaxUploadBytesPerUpdate), _budgets.MaxUploadBytesPerUpdate - available, written, _budgets.MaxUploadBytesPerUpdate, written < 0 || written > available, allowanceSource: _budgets)) throw new InvalidOperationException("Device exceeded its incremental upload allowance.");
            available -= written; Lifetime.Uploaded(written);
            _ = upload.Ready;
        }
        PublishDependencies(pending);
        pending.PublishNext = pending.Owners.Count + pending.Uploads.Count == pending.Resources.Count && pending.Uploads.Values.All(u => u.Ready);
    }
    void Publish(Pending pending)
    {
        Fence(pending);
        foreach (var (id, upload) in pending.Uploads)
        {
            Fence(pending);
            var handle = upload.Publish();
            var owner = new Owner(id, handle);
            pending.Owners.Add(id, owner); pending.Created.Add(owner);
        }
        var references = new Dictionary<MapResourceReference, Owner>();
        foreach (var plan in pending.Batch!.Plans)
            foreach (var resource in plan.Resources) references.Add(resource.Reference, pending.Owners[Identity.Of(resource)]);
        PrepareRecording(references.Count);
        foreach (var plan in pending.Batch.Plans) plan.Inventory.Placements.AcceptPublishedClusters();
        var old = _accepted;
        var oldOwners = _owners.Values.ToArray(); _owners.Clear();
        foreach (var pair in pending.Owners) _owners.Add(pair.Key, pair.Value);
        _acceptedReferences.Clear(); foreach (var pair in references) _acceptedReferences.Add(pair.Key, pair.Value);
        if (Device is MapScene3DSink sink) sink.AcceptReferences(_acceptedReferences.Keys);
        _accepted = pending.Batch; pending.Batch = null; _acceptedState = pending.State;
        _transition = new(pending.Epoch, MapSceneStatus.CompleteForScope, _transition.PendingWitness! with { CompleteForScope = true }, null);
        _pending = null;
        foreach (var upload in pending.Uploads.Values) upload.Dispose();
        foreach (var owner in oldOwners) if (!_owners.ContainsKey(owner.Identity)) _retired.Enqueue(owner);
        old?.Dispose();
    }
    void PrepareRecording(int references)
    {
        if (references == _recordingCapacity ||
            (references < _recordingCapacity && references != 0 && references * 2L >= _recordingCapacity)) return;
        // Both backing stores coexist during replacement. Reserve before allocating, then drop the old maps
        // before returning their credit, including when an empty publication releases the high-water capacity.
        using var charge = _admission.ReserveCpu(references == 0 ? 0 : checked(512L + references * 512L));
        _recording.Prepare(references);
        _recordingCharge?.Dispose(); _recordingCharge = references == 0 ? null : charge.Retain(); _recordingCapacity = references;
    }
    public bool TryGetHandle(MapResourceReference resource, out MapRenderHandle handle)
    {
        lock (_gate)
        { RequireLive(); if (_acceptedReferences.TryGetValue(resource, out var owner)) { handle = owner.Handle; return true; } handle = default; return false; }
    }
    public void Record(MapFrameState frame, IMapDrawSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            RequireLive();
            _recording.Reset(sink, _acceptedReferences);
            var plans = _accepted?.Plans;
            if (plans is null) return;
            foreach (var plan in plans) plan.RegisterResident(_recording);
            foreach (var plan in plans) plan.Record(frame, _recording);
        }
    }
    void Fence(Pending pending)
    {
        if (_disposed || !ReferenceEquals(pending, _pending) || pending.Epoch != _transition.Epoch)
            throw new OperationCanceledException("Scene generation was superseded.");
    }
    void CancelPending()
    {
        if (_pending is not { } pending) return;
        pending.Job?.Cancellation.Cancel(); _cancelled.Add(pending); _pending = null;
        foreach (var upload in pending.Uploads.Values) upload.Dispose(); pending.Uploads.Clear();
        foreach (var owner in pending.Created) _retired.Enqueue(owner); pending.Created.Clear();
    }
    void DrainCancelled()
    {
        for (int i = _cancelled.Count - 1; i >= 0; i--)
        {
            var pending = _cancelled[i];
            if (pending.Job is { } job)
            {
                if (!job.Completion.IsCompleted) continue;
                if (job.Completion.IsCompletedSuccessfully) foreach (var plan in job.Completion.Result) plan.Dispose();
                else _ = job.Completion.Exception;
                _queue.Remove(job); job.Charge.Dispose();
            }
            pending.Batch?.Dispose(); pending.SourceCharge?.Dispose(); _cancelled.RemoveAt(i);
        }
    }
    void DrainRetirement()
    { for (int i = 0; !DemandDiagnostics.Check(this, "MapRenderResidency.Disposals", nameof(MapRenderBudgets.MaxDisposalsPerUpdate), i, 1, _budgets.MaxDisposalsPerUpdate, i >= _budgets.MaxDisposalsPerUpdate, allowanceSource: _budgets) && _retired.TryDequeue(out var owner); i++) Device.Release(owner.Handle); }
    void PendingCapacity(MapSceneCapacity capacity)
    {
        if (_transition.Capacity != capacity)
            _transition = _transition with { Capacity = capacity, Findings = [capacity.Message] };
    }
    void Refuse(MapSceneStatus status, long epoch, Exception error)
    {
        if (_acceptedState is not null) DesiredState = _acceptedState; _transition = new(epoch, status, _transition.AcceptedWitness, null)
        { Capacity = MapSceneCapacity.From(error), Findings = Array.AsReadOnly(new[] { error.Message }) };
    }
    static MapSceneStatus? Refusal(Exception error) => error switch
    {
        MapRenderAssetRefusal { Status: MapRenderAssetStatus.CapacityExceeded } or MapSceneCapacityException or OverflowException => MapSceneStatus.CapacityExceeded,
        MapRenderAssetRefusal { Status: MapRenderAssetStatus.Stale } => MapSceneStatus.Stale,
        MapRenderAssetRefusal { Status: MapRenderAssetStatus.NotRepresentable } => MapSceneStatus.NotRepresentable,
        MapSceneSourceException source => source.Status,
        MapRenderAssetRefusal or MapDocumentException or ArgumentException or InvalidOperationException => MapSceneStatus.Invalid,
        _ => null
    };
    static MapSceneWitness Witness(MapRenderSourceState state, long epoch, IReadOnlyList<MapRenderPlan>? plans)
    {
        string scope = MapPlacementDigest.Hash(w =>
        { w.WriteStartArray(); foreach (string digest in state.Windows.Select(s => s.Digest).Distinct().Order(StringComparer.Ordinal)) w.WriteStringValue(digest); w.WriteEndArray(); });
        MapSceneResourceKey[] Keys(Func<MapSceneWitness, IReadOnlyList<MapSceneResourceKey>> get) =>
            plans?.SelectMany(p => get(p.Inventory.Witness)).Distinct().OrderBy(k => k.Kind, StringComparer.Ordinal).ThenBy(k => k.Id, StringComparer.Ordinal).ToArray() ?? [];
        return new(state.Closure.Hash, state.Source.SourceGeneration, scope, Keys(w => w.Required), Keys(w => w.Present),
            Keys(w => w.KnownEmpty), Keys(w => w.Unavailable), false, false)
        { RenderExtentsDigest = state.Extents.Digest, PolicyDigest = state.Policy.Digest, Epoch = epoch };
    }
    void RequireLive() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        Pending[] waiting;
        lock (_gate)
        {
            if (_disposed) return; _disposed = true; CancelPending(); waiting = _cancelled.ToArray();
        }
        // A queued or held worker owns its readers until completion. Dispose never waits on a caller scheduler.
        foreach (var pending in waiting)
            if (pending.Job is { } job && !job.Completion.IsCompleted)
                _ = job.Completion.ContinueWith(_ =>
                {
                    lock (_gate)
                    {
                        DrainCancelled();
                        if (_cancelled.Count == 0) AssetCache.Dispose(); else AssetCache.TryTrim();
                        TrimRetention();
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        lock (_gate)
        {
            DrainCancelled();
            foreach (var owner in _owners.Values) _retired.Enqueue(owner); _owners.Clear(); _acceptedReferences.Clear();
            while (_retired.TryDequeue(out var owner)) Device.Release(owner.Handle);
            _accepted?.Dispose(); _accepted = null;
            _recording.Clear(); _recordingCharge?.Dispose(); _recordingCharge = null; _recordingCapacity = 0;
            if (_cancelled.Count == 0) AssetCache.Dispose(); else AssetCache.TryTrim();
            foreach (var lease in _metadataCharges.Values.Concat(_proofCharges.Values).Concat(_encodedCharges.Values)) lease.Dispose();
            _metadataCharges.Clear(); _proofCharges.Clear(); _encodedCharges.Clear();
        }
    }
    SourceRetention RetainSource(MapRenderSourceState state)
    {
        MapRenderAdmission.CpuLease? metadata = null, proof = null, encoded = null;
        try
        {
            metadata = Retain(_metadataCharges, state.Source.Data, state.Source.Data.CaptureWork.RetainedBytes);
            proof = Retain(_proofCharges, state.Extents, state.Extents.MetadataBytes);
            encoded = Retain(_encodedCharges, state.Closure, state.Closure.CapturedResources.Sum(r => (long)r.ByteLength));
            return new(metadata, proof, encoded);
        }
        catch { metadata?.Dispose(); proof?.Dispose(); encoded?.Dispose(); TrimRetention(); throw; }
        MapRenderAdmission.CpuLease Retain<T>(Dictionary<T, MapRenderAdmission.CpuLease> entries, T key, long bytes) where T : notnull
        {
            if (!entries.TryGetValue(key, out var lease))
            {
                lease = _admission.ReserveCpu(bytes);
                entries.Add(key, lease);
            }
            return lease.Retain();
        }
    }
    void TrimRetention()
    {
        Trim(_metadataCharges); Trim(_proofCharges); Trim(_encodedCharges);
        static void Trim<T>(Dictionary<T, MapRenderAdmission.CpuLease> entries) where T : notnull
        {
            foreach (var pair in entries)
                if (pair.Value.OwnerCount == 1) { pair.Value.Dispose(); entries.Remove(pair.Key); }
        }
    }
    sealed class SourceRetention(MapRenderAdmission.CpuLease metadata, MapRenderAdmission.CpuLease proof,
        MapRenderAdmission.CpuLease encoded) : IDisposable
    {
        internal MapRenderAdmission.CpuLease Encoded => encoded;
        public void Dispose() { metadata.Dispose(); proof.Dispose(); encoded.Dispose(); }
    }
    readonly record struct Identity(MapRenderKey Key, string Digest, MapBox3 Bounds)
    {
        internal static Identity Of(MapPreparedResource resource) => new(resource.Reference.Key, resource.PayloadDigest,
            resource.Payload is MapRigidResource && resource.Reference.Key.Kind == MapRenderKind.Foliage ? default : resource.Bounds);
    }
    sealed record Owner(Identity Identity, MapRenderHandle Handle);
    sealed class Batch(MapRenderPlan[] plans, IDisposable preparation, IDisposable source) : IDisposable
    {
        internal MapRenderPlan[] Plans => plans;
        public void Dispose() { foreach (var plan in plans) plan.Dispose(); preparation.Dispose(); source.Dispose(); }
    }
    sealed class Pending(MapRenderSourceState state, long epoch)
    {
        internal MapRenderSourceState State => state;
        internal long Epoch => epoch;
        internal MapRenderBuildQueue.Job? Job;
        internal SourceRetention? SourceCharge;
        internal Batch? Batch;
        internal readonly Dictionary<Identity, MapPreparedResource> Resources = new();
        internal readonly Dictionary<Identity, Owner> Owners = new();
        internal readonly Dictionary<Identity, MapRenderAdmittedUpload> Uploads = new();
        internal readonly List<Owner> Created = [];
        internal MapRenderUploadPreflight? UploadAdmission;
        internal bool PublishNext, UploadsAdmitted;
    }
    sealed class CompositeSink : IMapDrawSink
    {
        IMapDrawSink _sink = null!;
        Dictionary<MapResourceReference, Owner> _owners = null!;
        internal void Prepare(int count)
        {
            var canonical = new Dictionary<Identity, MapResourceReference>(count);
            var drawn = new HashSet<MapResourceReference>(count);
            var aliases = new Dictionary<MapResourceReference, MapResourceReference>(count);
            _canonical = canonical; _drawn = drawn; _aliases = aliases;
            _sink = null!; _owners = null!;
        }
        internal void Clear() => Prepare(0);
        internal void Reset(IMapDrawSink sink, Dictionary<MapResourceReference, Owner> owners)
        { _sink = sink; _owners = owners; _canonical.Clear(); _drawn.Clear(); _aliases.Clear(); }
        Dictionary<Identity, MapResourceReference> _canonical = new();
        HashSet<MapResourceReference> _drawn = new();
        Dictionary<MapResourceReference, MapResourceReference> _aliases = new();
        public void Register(MapPreparedResource resource)
        {
            if (_aliases.ContainsKey(resource.Reference)) return;
            if (!_owners.TryGetValue(resource.Reference, out var owner)) throw new InvalidOperationException("Only accepted resources can be recorded.");
            if (!_canonical.TryGetValue(owner.Identity, out var reference))
            { reference = resource.Reference; _canonical.Add(owner.Identity, reference); }
            if (_sink is MapScene3DSink native) native.RegisterReference(resource.Reference);
            else _sink.Register(resource.Payload is MapFoliageResource foliage ? resource with { Payload = foliage.Detached() } : resource);
            _aliases.Add(resource.Reference, reference);
        }
        public void Write(in MapDrawRecord draw)
        {
            var reference = _aliases[draw.Resource];
            if (_drawn.Add(reference)) _sink.Write(draw with { Resource = reference });
        }
    }
}
