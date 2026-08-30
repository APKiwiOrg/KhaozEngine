using System;
using System.Collections.Generic;

namespace KhaozEngine.Objectives;

/// <summary>
/// The game-agnostic objective / goal tracker: signals -&gt; counters -&gt; declarative conditions -&gt; completion event.
/// </summary>
/// <remarks>
/// <para>
/// A game <see cref="Report(string, double)"/>s accumulators and <see cref="Observe(string, double)"/>s peaks for
/// opaque metric keys it chose ("ore.mined", "depth.max", ...). The tracker holds a <see cref="MetricScope.Persistent"/>
/// and a <see cref="MetricScope.Session"/> counter per key; every report updates both, and
/// <see cref="ResetScope(MetricScope)"/> clears a scope at the game's own run boundary (the framework never knows
/// what a "run" is). Objectives are <see cref="ObjectiveDefinition"/>s - AND-compositions of
/// <see cref="ObjectiveCondition"/>s over those keys. When all of an objective's conditions hold, it completes once,
/// stays completed, and fires <see cref="ObjectiveCompleted"/>; completion is idempotent and survives Capture/Restore.
/// </para>
/// <para>
/// <b>Constraints.</b> An <see cref="ObjectiveConditionKind.AtMost"/> is a constraint, not a goal: it holds until it
/// is violated, so an objective built only from those holds on empty counters. Nothing derives its completion (not
/// <see cref="Register(ObjectiveDefinition)"/>, not a report, not <see cref="Restore(ObjectivesSnapshot)"/>). The game
/// completes it by calling <see cref="EvaluateAll"/> at the point it decides the run is over, which is the only moment
/// "no upgrades were bought" means anything. Mixed objectives (a constraint alongside an AtLeast or Reached) are
/// evaluated normally, gated by their progress condition.
/// </para>
/// <para>
/// <b>Perf.</b> Objectives are indexed by the keys their conditions watch, so a report re-evaluates only the
/// objectives touching the changed key, never the full set - the contract that makes thousands of reports/sec against
/// hundreds of objectives cheap.
/// </para>
/// <para>
/// <b>Determinism.</b> Pure counters (double accumulation) and pure predicates; no RNG, no wall-clock. Given the same
/// ordered sequence of calls the tracker produces the same completions and the same <see cref="Capture"/> output,
/// which matters for a balance sim.
/// </para>
/// <para>
/// <b>Threading.</b> Single-threaded. Route all calls from the sim thread; completion handlers run synchronously
/// inside the triggering Report / Observe / Register / Restore call.
/// </para>
/// <para>
/// <b>Lifecycle.</b> Subscribe to <see cref="ObjectiveCompleted"/>, then <see cref="Register(ObjectiveDefinition)"/>
/// the definitions, then <see cref="Restore(ObjectivesSnapshot)"/> the save. Register-before-Restore is preferred but
/// not required: a completed id restored ahead of its definition binds silently when the definition registers.
/// </para>
/// </remarks>
public sealed class ObjectiveTracker
{
    private struct Cell
    {
        public double Sum;
        public double Max;
    }

    private sealed class MetricEntry
    {
        public Cell Persistent;
        public Cell Session;
    }

    private sealed class ObjectiveState
    {
        public ObjectiveState(ObjectiveDefinition definition)
        {
            Definition = definition;
            ConstraintOnly = AllConstraints(definition);
        }

        public ObjectiveDefinition Definition { get; }
        public bool Complete { get; set; }

        /// <summary>
        /// True when every condition is an <see cref="ObjectiveConditionKind.AtMost"/>, i.e. the objective is pure
        /// constraint with no progress condition to gate it. Computed once at registration: it decides eligibility on
        /// every evaluation, and the perf contract says an evaluation costs a walk of the conditions, not two.
        /// </summary>
        public bool ConstraintOnly { get; }

        private static bool AllConstraints(ObjectiveDefinition definition)
        {
            var conditions = definition.Conditions;   // never empty (ObjectiveDefinition enforces it)
            for (int i = 0; i < conditions.Count; i++)
                if (conditions[i].Kind != ObjectiveConditionKind.AtMost)
                    return false;
            return true;
        }
    }

    private readonly Dictionary<string, MetricEntry> _metrics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObjectiveState> _objectives = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ObjectiveState>> _byKey = new(StringComparer.Ordinal);

    // Completed ids restored before their definition was registered; bound (silently) at Register time.
    private readonly HashSet<string> _restoredCompletedIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Raised once, the moment an objective's conditions all hold. Carries the objective id and its opaque
    /// metadata. Handlers run synchronously inside the triggering call; the objective is already marked complete
    /// when the handler runs, so a re-entrant report cannot re-fire it.
    /// </summary>
    public event Action<ObjectiveCompletion>? ObjectiveCompleted;

    // ----- registration ------------------------------------------------------

    /// <summary>Registers an objective definition and indexes it by the keys its conditions watch.</summary>
    /// <remarks>
    /// If the id was restored as completed ahead of registration, it binds as complete without firing. Otherwise the
    /// objective is evaluated once against the current counters and may complete + fire immediately, which is how a
    /// challenge added in a patch surfaces against lifetime totals that already satisfy it.
    /// <para>
    /// A constraint-only objective (every condition an <see cref="ObjectiveConditionKind.AtMost"/>) is skipped by that
    /// evaluation. It carries no progress condition, so it holds on empty counters and would otherwise complete the
    /// moment it registers. See <see cref="EvaluateAll"/>, which is what completes those.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">An objective with the same <see cref="ObjectiveDefinition.Id"/> is already registered.</exception>
    public void Register(ObjectiveDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (_objectives.ContainsKey(definition.Id))
            throw new ArgumentException($"An objective with id '{definition.Id}' is already registered.", nameof(definition));

        var state = new ObjectiveState(definition);
        _objectives.Add(definition.Id, state);

        foreach (var key in DistinctKeys(definition))
        {
            if (!_byKey.TryGetValue(key, out var list))
            {
                list = new List<ObjectiveState>();
                _byKey.Add(key, list);
            }
            list.Add(state);
        }

        if (_restoredCompletedIds.Remove(definition.Id))
            state.Complete = true; // completed before the save; restored ahead of this registration
        else
            TryComplete(state);
    }

    /// <summary>Registers each definition in turn (see <see cref="Register(ObjectiveDefinition)"/>).</summary>
    public void RegisterRange(IEnumerable<ObjectiveDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        foreach (var definition in definitions)
            Register(definition);
    }

    private static List<string> DistinctKeys(ObjectiveDefinition definition)
    {
        var keys = new List<string>(definition.Conditions.Count);
        foreach (var condition in definition.Conditions)
            if (!keys.Contains(condition.Key))
                keys.Add(condition.Key);
        return keys;
    }

    // ----- reporting ----------------------------------------------------------

    /// <summary>
    /// Adds <paramref name="amount"/> to the accumulator (Sum) of <paramref name="key"/> in both scopes, then
    /// re-evaluates only the objectives watching that key. Feeds <see cref="ObjectiveConditionKind.AtLeast"/> /
    /// <see cref="ObjectiveConditionKind.AtMost"/> conditions.
    /// </summary>
    public void Report(string key, double amount = 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var entry = GetOrAddEntry(key);
        entry.Persistent.Sum += amount;
        entry.Session.Sum += amount;
        Evaluate(key);
    }

    /// <summary>
    /// Raises the peak (Max) of <paramref name="key"/> in both scopes to <paramref name="value"/> if it is higher,
    /// then re-evaluates only the objectives watching that key. Feeds <see cref="ObjectiveConditionKind.Reached"/>
    /// conditions ("reach depth N").
    /// </summary>
    public void Observe(string key, double value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var entry = GetOrAddEntry(key);
        if (value > entry.Persistent.Max) entry.Persistent.Max = value;
        if (value > entry.Session.Max) entry.Session.Max = value;
        Evaluate(key);
    }

    /// <summary>
    /// Clears every counter (Sum and Max) in the given scope(s) across all keys. This is the game's run / prestige
    /// boundary; it does not touch completion state (a completed objective stays completed) and fires no events (a
    /// reset only lowers readings - completions come from progress, not from a reset).
    /// </summary>
    public void ResetScope(MetricScope scope)
    {
        bool resetPersistent = (scope & MetricScope.Persistent) != 0;
        bool resetSession = (scope & MetricScope.Session) != 0;
        if (!resetPersistent && !resetSession)
            return;

        foreach (var entry in _metrics.Values)
        {
            if (resetPersistent) entry.Persistent = default;
            if (resetSession) entry.Session = default;
        }
    }

    private MetricEntry GetOrAddEntry(string key)
    {
        if (!_metrics.TryGetValue(key, out var entry))
        {
            entry = new MetricEntry();
            _metrics.Add(key, entry);
        }
        return entry;
    }

    // ----- evaluation ---------------------------------------------------------

    private void Evaluate(string key)
    {
        if (!_byKey.TryGetValue(key, out var watchers))
            return;
        // Capture the count: a completion handler that registers a new objective for this key appends to the same
        // list, but Register already evaluated it, so we skip the tail here (and completion is idempotent regardless).
        int count = watchers.Count;
        for (int i = 0; i < count; i++)
            TryComplete(watchers[i]);
    }

    /// <summary>
    /// Evaluates every registered, not-yet-completed objective and fires <see cref="ObjectiveCompleted"/> for any now
    /// satisfied. Call after bulk-registering objectives that may already be met by current counters (e.g. a challenge
    /// shipped in a patch that a player's lifetime totals already satisfy) if you did not follow Register-then-report.
    /// </summary>
    /// <remarks>
    /// This is also the ONLY call that can complete a constraint-only objective (every condition an
    /// <see cref="ObjectiveConditionKind.AtMost"/>, e.g. "buy no upgrades this run"). Such an objective holds on empty
    /// counters, so nothing derives its completion from a report: the game calls this at the point it decides the run
    /// is over, which is the moment the constraint means anything.
    /// </remarks>
    public void EvaluateAll()
    {
        foreach (var state in _objectives.Values)
            TryCompleteNow(state);
    }

    // Derived evaluation: Register, a report on a watched key, and the Restore sweep all come through here. A
    // constraint-only objective is never completed from one. Every AtMost holds on empty counters, so "no violation
    // reported" reads identically to "nothing has happened yet", and completing on that fires the objective before
    // the game has reported anything at all. Only the game knows when its run ended, and it says so by calling
    // EvaluateAll.
    private void TryComplete(ObjectiveState state)
    {
        if (state.ConstraintOnly)
            return;
        TryCompleteNow(state);
    }

    private void TryCompleteNow(ObjectiveState state)
    {
        if (state.Complete)
            return;

        var conditions = state.Definition.Conditions;
        for (int i = 0; i < conditions.Count; i++)
            if (!conditions[i].IsSatisfiedBy(Read(conditions[i])))
                return;

        state.Complete = true; // set before firing so a re-entrant report cannot re-complete/re-fire it
        ObjectiveCompleted?.Invoke(new ObjectiveCompletion(state.Definition.Id, state.Definition.Metadata));
    }

    private double Read(ObjectiveCondition condition)
    {
        if (!_metrics.TryGetValue(condition.Key, out var entry))
            return 0;
        Cell cell = condition.Scope == MetricScope.Persistent ? entry.Persistent : entry.Session;
        return condition.UsesMax ? cell.Max : cell.Sum;
    }

    // ----- introspection ------------------------------------------------------

    /// <summary>True if the objective is registered and completed.</summary>
    public bool IsComplete(string objectiveId)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectiveId);
        return _objectives.TryGetValue(objectiveId, out var state) && state.Complete;
    }

    /// <summary>True if an objective with this id is registered.</summary>
    public bool IsRegistered(string objectiveId)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectiveId);
        return _objectives.ContainsKey(objectiveId);
    }

    /// <summary>Reads the current accumulator (Sum) of a key in a single scope (0 if never reported).</summary>
    public double GetSum(string key, MetricScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        RequireSingleScope(scope);
        if (!_metrics.TryGetValue(key, out var entry))
            return 0;
        return scope == MetricScope.Persistent ? entry.Persistent.Sum : entry.Session.Sum;
    }

    /// <summary>Reads the current peak (Max) of a key in a single scope (0 if never observed).</summary>
    public double GetMax(string key, MetricScope scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        RequireSingleScope(scope);
        if (!_metrics.TryGetValue(key, out var entry))
            return 0;
        return scope == MetricScope.Persistent ? entry.Persistent.Max : entry.Session.Max;
    }

    /// <summary>Builds a live progress view (completion + per-condition current/target) for one objective.</summary>
    /// <exception cref="ArgumentException">No objective with that id is registered.</exception>
    public ObjectiveProgress GetProgress(string objectiveId)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectiveId);
        if (!_objectives.TryGetValue(objectiveId, out var state))
            throw new ArgumentException($"No objective registered with id '{objectiveId}'.", nameof(objectiveId));
        return BuildProgress(state);
    }

    /// <summary>Builds a live progress view for every registered objective, in registration order.</summary>
    public IReadOnlyList<ObjectiveProgress> GetAllProgress()
    {
        var result = new List<ObjectiveProgress>(_objectives.Count);
        foreach (var state in _objectives.Values)
            result.Add(BuildProgress(state));
        return result;
    }

    private ObjectiveProgress BuildProgress(ObjectiveState state)
    {
        var conditions = state.Definition.Conditions;
        var rows = new ConditionProgress[conditions.Count];
        for (int i = 0; i < conditions.Count; i++)
        {
            var condition = conditions[i];
            double current = Read(condition);
            rows[i] = new ConditionProgress(
                condition.Kind, condition.Key, condition.Scope, current, condition.Target, condition.IsSatisfiedBy(current));
        }
        return new ObjectiveProgress(state.Definition.Id, state.Complete, rows);
    }

    private static void RequireSingleScope(MetricScope scope)
    {
        if (scope != MetricScope.Persistent && scope != MetricScope.Session)
            throw new ArgumentException("Scope must be exactly one of Persistent or Session.", nameof(scope));
    }

    // ----- persistence --------------------------------------------------------

    /// <summary>
    /// Captures the full tracker state (non-empty counter cells + completed ids) as a plain, serializable snapshot
    /// the game folds into its own save. Deterministic: cells are ordered by key then scope, completed ids sorted,
    /// so identical state yields identical output.
    /// </summary>
    public ObjectivesSnapshot Capture()
    {
        var snapshot = new ObjectivesSnapshot();

        var keys = new List<string>(_metrics.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var entry = _metrics[key];
            AddCell(snapshot, key, MetricScope.Persistent, entry.Persistent);
            AddCell(snapshot, key, MetricScope.Session, entry.Session);
        }

        var completed = new List<string>();
        foreach (var pair in _objectives)
            if (pair.Value.Complete)
                completed.Add(pair.Key);
        completed.Sort(StringComparer.Ordinal);
        snapshot.Completed = completed;

        return snapshot;
    }

    private static void AddCell(ObjectivesSnapshot snapshot, string key, MetricScope scope, in Cell cell)
    {
        if (cell.Sum == 0 && cell.Max == 0)
            return;
        snapshot.Metrics.Add(new MetricCellSnapshot { Key = key, Scope = scope, Sum = cell.Sum, Max = cell.Max });
    }

    /// <summary>
    /// Restores tracker state from a snapshot: replaces all counters, marks the completed ids (without re-firing), and
    /// then evaluates the remaining objectives so any now satisfied by restored counters (e.g. a patched-in challenge
    /// already met by lifetime totals) completes and fires exactly once. A completed id whose definition is not yet
    /// registered is remembered and binds when it registers.
    /// <para>
    /// A constraint-only objective is restored from the snapshot's completed ids like any other, but its completion is
    /// never derived here: a save that records no violation is indistinguishable from a save where the run had not
    /// started. Call <see cref="EvaluateAll"/> if the game wants those judged against the restored counters.
    /// </para>
    /// </summary>
    public void Restore(ObjectivesSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _metrics.Clear();
        _restoredCompletedIds.Clear();
        foreach (var state in _objectives.Values)
            state.Complete = false;

        if (snapshot.Metrics != null)
        {
            foreach (var row in snapshot.Metrics)
            {
                if (string.IsNullOrEmpty(row.Key))
                    continue;
                var entry = GetOrAddEntry(row.Key);
                if (row.Scope == MetricScope.Persistent)
                {
                    entry.Persistent.Sum = row.Sum;
                    entry.Persistent.Max = row.Max;
                }
                else if (row.Scope == MetricScope.Session)
                {
                    entry.Session.Sum = row.Sum;
                    entry.Session.Max = row.Max;
                }
            }
        }

        if (snapshot.Completed != null)
        {
            foreach (var id in snapshot.Completed)
            {
                if (string.IsNullOrEmpty(id))
                    continue;
                if (_objectives.TryGetValue(id, out var state))
                    state.Complete = true;
                else
                    _restoredCompletedIds.Add(id);
            }
        }

        foreach (var state in _objectives.Values)
            TryComplete(state);   // derived: a restored counter set is not the game saying its run is over
    }
}
