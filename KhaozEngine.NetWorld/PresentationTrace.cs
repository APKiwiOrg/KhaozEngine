using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace KhaozEngine.NetWorld;

/// <summary>
/// A first-class, debug-gated per-frame trace of the client presentation layer (enable via
/// <see cref="WorldClientConfig.PresentationTraceEnabled"/>; read <see cref="WorldClient.PresentationTrace"/>). It
/// records, once per <see cref="WorldClient.AdvancePresentation"/> and per rendered entity, the RENDERED position
/// plus the internal signals a consumer cannot otherwise observe: the monotonic render clock, the fixed remote
/// interpolation delay, the render time (<c>clock - delay</c>), seconds since the last snapshot, whether a snapshot
/// arrived this frame, the local reconcile-error magnitude, and the per-remote snapshot-starvation hold flag. Dump
/// it to CSV (<see cref="WriteCsv"/>) and characterise a stutter by its signature. Zero cost when disabled (the
/// property is null). This is the engine-side promotion of the throwaway Ruinborne client-side position logger.
/// </summary>
public sealed class PresentationTrace
{
    /// <summary>One (frame, entity) sample. Per-frame scalars (<see cref="T"/>, <see cref="RenderTime"/>,
    /// <see cref="SinceSnapshot"/>, <see cref="SnapshotArrived"/>, <see cref="ReconcileError"/>) repeat across the
    /// frame's rows; <see cref="IsLocal"/>/<see cref="EntityId"/>/<see cref="Position"/>/<see cref="Held"/> are
    /// per-entity. <see cref="ReconcileError"/> and <see cref="Held"/> only apply to remotes/local respectively.</summary>
    public readonly record struct Row(
        double T, float Dt, double RenderTime, float InterpolationDelay, double SinceSnapshot,
        bool SnapshotArrived, float ReconcileError, bool IsLocal, int EntityId, Vector3 Position,
        float VerticalVelocity, bool Held);

    private readonly List<Row> rows = new();

    /// <summary>The recorded samples, in order.</summary>
    public IReadOnlyList<Row> Rows => rows;

    /// <summary>Number of recorded rows.</summary>
    public int Count => rows.Count;

    /// <summary>Clears all recorded rows (e.g. to trace only a specific window).</summary>
    public void Clear() => rows.Clear();

    internal void Add(in Row row) => rows.Add(row);

    /// <summary>Renders the trace as a CSV (invariant culture), one row per (frame, entity), header first.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.Append("t,dt,renderTime,interpDelay,sinceSnapshot,snapshotArrived,reconcileError,")
          .Append("entity,entityId,x,y,z,vertVel,held\n");
        foreach (Row r in rows)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "{0:0.00000},{1:0.00000},{2:0.00000},{3:0.00000},{4:0.00000},{5},{6:0.00000},{7},{8},{9:0.0000},{10:0.0000},{11:0.0000},{12:0.0000},{13}\n",
                r.T, r.Dt, r.RenderTime, r.InterpolationDelay, r.SinceSnapshot, r.SnapshotArrived ? 1 : 0,
                r.ReconcileError, r.IsLocal ? "local" : "remote", r.EntityId,
                r.Position.X, r.Position.Y, r.Position.Z, r.VerticalVelocity, r.Held ? 1 : 0);
        }
        return sb.ToString();
    }

    /// <summary>Writes <see cref="ToCsv"/> to <paramref name="path"/> (overwrites).</summary>
    public void WriteCsv(string path) => File.WriteAllText(path, ToCsv());
}
