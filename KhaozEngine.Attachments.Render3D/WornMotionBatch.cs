using System;
using System.Collections.Generic;
using System.Numerics;
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
    {
        ArgumentNullException.ThrowIfNull(motion);
        _entries.Add(new Entry(motion, skeleton, palette, model, deltaSeconds, level));
    }

    /// <summary>Discards the entries, retaining storage for the next frame.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>Validates every entry, then joins all wearer steps before returning. Entries remain queued.</summary>
    /// <exception cref="AttachmentSubmissionArgumentException">A motion was added more than once.</exception>
    public void Step()
    {
        if (_entries.Count == 0) return;

        try
        {
            for (int index = 0; index < _entries.Count; index++)
            {
                Entry entry = _entries[index];
                if (!_uniqueMotions.Add(entry.Motion))
                    throw new AttachmentSubmissionArgumentException(AttachmentSubmissionRefusal.DuplicateMotion,
                        $"Motion at batch entry {index} was already added.", null) { EntryIndex = index };
                entry.Motion.PrepareStep(entry.Skeleton, entry.Palette.Span, entry.Model, entry.DeltaSeconds, entry.Level);
            }
        }
        finally
        {
            _uniqueMotions.Clear();
        }

        _scheduler.For(_entries.Count, _stepEntry);
    }

    private void StepEntry(int index)
    {
        Entry entry = _entries[index];
        entry.Motion.StepPrepared(entry.Model, entry.DeltaSeconds, entry.Level);
    }

    private readonly record struct Entry(WornMotion Motion, Skeleton Skeleton, ReadOnlyMemory<Matrix4x4> Palette,
        Matrix4x4 Model, float DeltaSeconds, MotionLevel Level);
}
