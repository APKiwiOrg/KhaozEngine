using System;
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>
/// A unit of work's declared component access: the component types it <see cref="Reads"/> (read-only) and
/// <see cref="Writes"/> (read+write). This is the shared access-declaration vocabulary of the parallel-job-system
/// program. For a <see cref="World.ParallelForEach{T1}(RefAction{T1}, KhaozEngine.Simulation.IJobScheduler?)"/> the
/// safety contract is enforced at runtime (per-row-pure + the hazard guard); an explicit <see cref="AccessSet"/>
/// is the <em>declaration</em> the system scheduler (layer 3) reuses to decide which systems may run concurrently:
/// two units may overlap only if their access sets do not <see cref="ConflictsWith"/>.
/// </summary>
/// <remarks>
/// Keyed by <see cref="Type"/> (global and stable), not per-world component ids, so a declaration is portable
/// across worlds. A type listed in <see cref="Writes"/> is never also listed in <see cref="Reads"/> (write is the
/// stronger claim). Build one with <see cref="Access"/> (e.g. <c>Access.Write&lt;Position&gt;().Read&lt;Velocity&gt;()</c>).
/// </remarks>
public sealed class AccessSet
{
    private readonly Type[] reads;   // read-only types, sorted by full name, deduped, disjoint from writes
    private readonly Type[] writes;  // read+write types, sorted by full name, deduped

    internal AccessSet(Type[] reads, Type[] writes)
    {
        this.reads = reads;
        this.writes = writes;
    }

    /// <summary>An empty declaration (touches nothing); never conflicts with anything.</summary>
    public static readonly AccessSet None = new(Array.Empty<Type>(), Array.Empty<Type>());

    /// <summary>Component types read but not written.</summary>
    public IReadOnlyList<Type> Reads => reads;

    /// <summary>Component types written (and therefore also read).</summary>
    public IReadOnlyList<Type> Writes => writes;

    /// <summary>
    /// True if these two units cannot safely run concurrently: one writes a type the other reads or writes
    /// (write-write or read-write hazard). Two purely-reading units never conflict.
    /// </summary>
    public bool ConflictsWith(AccessSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (Type w in writes) if (other.Touches(w)) return true;
        foreach (Type w in other.writes) if (Touches(w)) return true;
        return false;
    }

    private bool Touches(Type t) => Array.IndexOf(reads, t) >= 0 || Array.IndexOf(writes, t) >= 0;
}

/// <summary>
/// Fluent builder for an <see cref="AccessSet"/>. Start from <see cref="Access.Read{T}"/> /
/// <see cref="Access.Write{T}"/>; chain <see cref="Read{T}"/> / <see cref="Write{T}"/>; it implicitly converts to
/// an <see cref="AccessSet"/>. Declaring a type as both read and write keeps only the write (the stronger claim).
/// </summary>
public sealed class AccessBuilder
{
    private readonly HashSet<Type> reads = new();
    private readonly HashSet<Type> writes = new();

    /// <summary>Declares <typeparamref name="T"/> as read-only.</summary>
    public AccessBuilder Read<T>() where T : struct, IComponent { reads.Add(typeof(T)); return this; }

    /// <summary>Declares <typeparamref name="T"/> as read+write.</summary>
    public AccessBuilder Write<T>() where T : struct, IComponent { writes.Add(typeof(T)); return this; }

    /// <summary>Builds the immutable <see cref="AccessSet"/> (writes win over reads; both sorted for stable equality).</summary>
    public AccessSet Build()
    {
        var w = new List<Type>(writes);
        var r = new List<Type>();
        foreach (Type t in reads) if (!writes.Contains(t)) r.Add(t);
        r.Sort(ByName);
        w.Sort(ByName);
        return new AccessSet(r.ToArray(), w.ToArray());
    }

    public static implicit operator AccessSet(AccessBuilder builder) => builder.Build();

    private static int ByName(Type a, Type b) =>
        string.CompareOrdinal(a.FullName ?? a.Name, b.FullName ?? b.Name);
}

/// <summary>Entry points for building an <see cref="AccessSet"/> declaration.</summary>
public static class Access
{
    /// <summary>Starts a declaration with <typeparamref name="T"/> read-only.</summary>
    public static AccessBuilder Read<T>() where T : struct, IComponent => new AccessBuilder().Read<T>();

    /// <summary>Starts a declaration with <typeparamref name="T"/> read+write.</summary>
    public static AccessBuilder Write<T>() where T : struct, IComponent => new AccessBuilder().Write<T>();
}
