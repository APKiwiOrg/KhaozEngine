using System;

namespace KhaozEngine.Movement;

/// <summary>How a navigation bake schedules its proof work. The setting never changes bake bytes and is not part of
/// <see cref="PhysicsNavBakeOptions"/> or the bake identity. A null execution, or a degree of 1, runs the serial build.
/// <para>Above degree 1 an ordinary contact profile proves its admissions, edges and Stair links on up to
/// <see cref="MaxDegreeOfParallelism"/> worker threads over one build snapshot. The capture's physics source and the
/// context's selected query view must both declare <see cref="KhaozEngine.Physics.IPhysicsConcurrentReadSource"/>.
/// Every provider the caller supplied (ground height and normal, medium, water field, registration, area classifier)
/// must answer as a deterministic function of its inputs, independent of call order and thread, and be safe for
/// concurrent reads, while the caller keeps its data fixed through capture and build. Legacy profiles always build
/// serially and never need the concurrent-read capability.</para></summary>
/// <param name="MaxDegreeOfParallelism">The most worker threads one contact profile build uses. A bake refuses a
/// value below 1 before any other check.</param>
public sealed record NavBakeExecution(int MaxDegreeOfParallelism)
{
    /// <summary>Default false. Opts each synchronous legacy profile into one thread-affine physics read lease.
    /// The caller asserts every provider used by this build is deterministic, stable and read-only, and callbacks
    /// neither mutate physics, acquire an incompatible explicit nested lease nor wait for another thread's mutation.
    /// The complete physics source and any selected movement query view must each declare
    /// <see cref="KhaozEngine.Physics.IPhysicsQueryLeaseSource"/>. The live lease authenticates the exact owner,
    /// view origin and captured geometry. Unsupported capability refuses after argument checks, before proofs.
    /// An existing explicit lease receives the backend's non-nesting refusal and remains live. The scope releases
    /// on success or failure. False retains the historical callback and outer-lease behavior. Ordinary contact
    /// profiles ignore this property. It never enables parallel legacy work or enters the bake identity.</summary>
    public bool UseStableLegacyReadScope { get; init; }

    /// <summary>The validated degree, 1 when <paramref name="execution"/> is null.</summary>
    internal static int Degree(NavBakeExecution? execution, string parameterName)
    {
        if (execution is null) return 1;
        if (execution.MaxDegreeOfParallelism < 1)
            throw new ArgumentOutOfRangeException(parameterName, execution.MaxDegreeOfParallelism,
                "The bake's maximum degree of parallelism must be at least 1.");
        return execution.MaxDegreeOfParallelism;
    }
}
