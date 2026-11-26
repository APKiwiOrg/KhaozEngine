using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Determinism;
using KhaozEngine.Render3D;
using KhaozEngine.Simulation;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Reuses frame input storage and schedules independent wearer motion steps together.</summary>
/// <remarks>
/// Pose every wearer before stepping and queue the draws afterwards. The batch and its motions are caller-owned.
/// Calls that access them must not overlap. Keep input palettes unchanged through <see cref="Step"/>.
/// </remarks>
public sealed class WornMotionBatch
{
    private readonly IJobScheduler _scheduler;
    private readonly List<Entry> _entries = new();
    private readonly HashSet<WornMotion> _uniqueMotions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<WornClothSlot, WornMotion> _clothClaims = new(ReferenceEqualityComparer.Instance);
    private readonly List<int> _work = new();
    private readonly Action<int> _stepEntry;

    /// <summary>Creates a reusable batch that borrows the caller's scheduler.</summary>
    public WornMotionBatch(IJobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _scheduler = scheduler;
        _stepEntry = StepEntry;
    }

    /// <summary>Adds a motion and its posed frame. The batch borrows the palette memory.</summary>
    public void Add(WornMotion motion, Skeleton skeleton, ReadOnlyMemory<Matrix4x4> palette,
        in Matrix4x4 model, float deltaSeconds, MotionLevel level = MotionLevel.Simulated)
        => Add(motion, skeleton, palette, model, deltaSeconds, level, null);

    /// <summary>Adds a posed wearer and an optional claim on the prewarmed cloth slot.</summary>
    public void Add(WornMotion motion, Skeleton skeleton, ReadOnlyMemory<Matrix4x4> palette,
        in Matrix4x4 model, float deltaSeconds, MotionLevel level, WornClothSelection? cloth)
    {
        ArgumentNullException.ThrowIfNull(motion);
        _entries.Add(new Entry(motion, skeleton, palette, model, deltaSeconds, level, cloth));
    }

    /// <summary>Discards the entries, retaining storage for the next frame.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>Validates every entry, then joins all wearer steps before returning. Entries remain queued.</summary>
    /// <exception cref="AttachmentSubmissionArgumentException">A motion was added more than once.</exception>
    public void Step()
    {
        if (_entries.Count == 0) return;

        using var scope = DeterministicFpScope.Enter();
        try
        {
            for (int index = 0; index < _entries.Count; index++)
            {
                Entry entry = _entries[index];
                if (!_uniqueMotions.Add(entry.Motion))
                    throw new AttachmentSubmissionArgumentException(AttachmentSubmissionRefusal.DuplicateMotion,
                        $"Motion at batch entry {index} was already added.", null) { EntryIndex = index };
                if (entry.Cloth is { } cloth)
                {
                    ArgumentNullException.ThrowIfNull(cloth.Slot);
                    if (!_clothClaims.TryAdd(cloth.Slot, entry.Motion))
                        throw new AttachmentSubmissionArgumentException(AttachmentSubmissionRefusal.DuplicateClothSlot,
                            $"Cloth slot at batch entry {index} was already selected.", null) { EntryIndex = index };
                }
                entry.Motion.PrepareStep(entry.Skeleton, entry.Palette.Span, entry.Model, entry.DeltaSeconds,
                    entry.Level, entry.Cloth);
            }

            for (int index = 0; index < _entries.Count; index++)
            {
                Entry entry = _entries[index];
                // Finish a possible outgoing handoff before the new claimant enters a worker.
                if (entry.Motion.ActiveClothSlot is { } active && _clothClaims.TryGetValue(active, out var next) &&
                    !ReferenceEquals(next, entry.Motion))
                    Advance(entry);
                else
                    _work.Add(index);
            }

            _scheduler.For(_work.Count, _stepEntry);
        }
        finally
        {
            _uniqueMotions.Clear();
            _clothClaims.Clear();
            _work.Clear();
        }
    }

    private void StepEntry(int index)
    {
        // The scheduler can hand this job to a thread with different floating-point controls.
        using var scope = DeterministicFpScope.Enter();
        Advance(_entries[_work[index]]);
    }

    private static void Advance(in Entry entry) =>
        entry.Motion.StepPrepared(entry.Palette.Span, entry.Model, entry.DeltaSeconds, entry.Level, entry.Cloth);

    private readonly record struct Entry(WornMotion Motion, Skeleton Skeleton, ReadOnlyMemory<Matrix4x4> Palette,
        Matrix4x4 Model, float DeltaSeconds, MotionLevel Level, WornClothSelection? Cloth);
}
