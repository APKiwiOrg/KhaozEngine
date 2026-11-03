using System;
using System.Collections.Generic;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using BepuSim = BepuPhysics.Simulation;
using BepuStaticHandle = BepuPhysics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

public sealed partial class BepuPhysicsWorld
{
    /// <summary>Lets InstalledPoseFixture and CapsuleSweepCharacterizationTests inspect the actual owned Bepu buffers.</summary>
    internal BepuSim SimulationForTests => _sim;

    /// <summary>Lets SupportFootprintScenes and CertifiedGeneralSweepFixture resolve installed static identities.</summary>
    internal IReadOnlyDictionary<int, (BepuStaticHandle Handle, TypedIndex Shape)> StaticHandlesForTests => _handles;

    /// <summary>Lets CertifiedHullSweepLifetimeTests locate native bodies for owned geometry mutation.</summary>
    internal IReadOnlyDictionary<int, (BepuPhysics.BodyHandle Handle, TypedIndex Shape)> DynamicHandlesForTests => _dynamics;

    /// <summary>Lets PhysicsQueryViewLifecycleTests verify reverse registration identity without changing it.</summary>
    internal IReadOnlyDictionary<int, int> ReverseStaticHandlesForTests => _reverseHandles;

    /// <summary>Lets PhysicsQueryViewLifecycleTests compare registration counts before and after borrowed reads.</summary>
    internal (int Statics, int Dynamics, int Constraints) RegistrationCountsForTests =>
        (_handles.Count, _dynamics.Count, _constraints.Count);

    /// <summary>Lets ConcurrentReadGateTests observe actual reader admission and interval scratch ownership.</summary>
    internal QueryGate QueryGateForTests => _queryGate;

    /// <summary>Lets support world-form and StaticMutationLeaseTests inspect published cache identities and captures.</summary>
    internal InstalledSupportGeometryCache InstalledSupportGeometryForTests => _installedSupportGeometry;

    /// <summary>Lets CertifiedHullSweepTests allocate malformed geometry with the owner's real allocator.</summary>
    internal BufferPool PoolForTests => _pool;

    /// <summary>Lets CertifiedSweepLifetimeTests replace a captured static to prove stale evidence is refused.</summary>
    internal Dictionary<int, InstalledSweepGeometry?> InstalledSweepStaticsForTests => _installedSweepStatics;

    /// <summary>Lets CertifiedCompoundSweepLifetimeTests inject stale dynamic topology independently of native geometry.</summary>
    internal Dictionary<int, InstalledSweepGeometry?> InstalledSweepDynamicsForTests => _installedSweepDynamics;

    /// <summary>Lets CertifiedStoredBoundsResidualTests verify its contact fixture has no unproved stored bounds.</summary>
    internal IReadOnlySet<int> UnprovedSweepStaticsForTests => _unprovedSweepStatics;

    /// <summary>Lets CertifiedQueryTimePruningTests inspect the selected magnitude bounds after each native mutation.</summary>
    internal CapsuleSweepSelection SweepSelectionForTests => _sweepSelection;

    /// <summary>Lets StaticMutationLeaseTests hold an ordinary query interval independently of explicit read leases.</summary>
    internal IDisposable EnterQueryForTests() => EnterQuery();

    /// <summary>Lets BepuCertifiedCapsuleSweepTests model a mutation that missed sweep publication bookkeeping.</summary>
    internal void AdvanceUnhandledGenerationForTests()
    {
        using QueryOperation operation = EnterMutation();
    }

    /// <summary>Lets CertifiedGeneralSweepFixture mutate native geometry through the existing publication protocol.</summary>
    internal void MutateForTests(Action<BepuSim> mutation, int? changedStatic)
    {
        using QueryOperation operation = EnterMutation();
        var token = BeginSweepMutation();
        mutation(_sim);
        CompleteSweepMutation(token, changedStatic, false);
    }

    /// <summary>Lets CertifiedGeneralSweepFixture inject a native fault before its first static publication.</summary>
    internal StaticHandle InstallForTests(BoxShape shape, Pose pose,
        Action<BepuSim, BepuStaticHandle> beforePublication)
    {
        using QueryOperation operation = EnterStaticMutation();
        var token = BeginSweepMutation();
        TypedIndex index = ShapeFactory.Add(_sim, _pool, shape);
        BepuStaticHandle native = _sim.Statics.Add(new BepuPhysics.StaticDescription(pose.Position, pose.Orientation, index));
        int id = _nextId++;
        AddStaticEntry(id, native, index);
        beforePublication(_sim, native);
        CompleteSweepMutation(token, id, false);
        return new(id);
    }

    /// <summary>Lets ContactNavigationReuseFixture observe this thread's actual backend sessions and source domains.</summary>
    internal IEnumerable<FootprintReuseSession> CurrentThreadReuseSessionsForTests
    {
        get
        {
            foreach (var pair in _footprintReuseSessions)
                if (pair.Key.Thread == Environment.CurrentManagedThreadId) yield return pair.Value;
        }
    }
}
