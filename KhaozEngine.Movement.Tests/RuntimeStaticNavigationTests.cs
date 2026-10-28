using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public class RuntimeStaticNavigationTests
{
    [Fact]
    public void AuthoredCaptureRefusesAttachedRuntimeStaticsBeforeSampling()
    {
        using var scene = Scene();
        using var context = Context(scene);
        Set(scene);
        int samples = 0;
        var refusal = Assert.Throws<InvalidOperationException>(() =>
            PhysicsNavBake.Capture(context, Options, _ => { samples++; return 0; }));
        Assert.Contains("runtime statics", refusal.Message);
        Assert.Equal(0, samples);
        using var known = scene.Acquire();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProfilePreflightRefusesAttachedRuntimeStaticsForEitherController(bool contact)
    {
        using var scene = Scene();
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        Set(scene);
        MoveTuning tuning = contact ? Tuning : Tuning with { Controller = MovementControllerKind.Legacy, Water = default };
        var refusal = Assert.Throws<InvalidOperationException>(() => capture.BuildProfile(tuning, default));
        Assert.Contains("runtime statics", refusal.Message);
        using var known = scene.Acquire();
    }

    [Fact]
    public void MultiProfilePreflightRefusesRuntimeStaticsBeforeAnyWorker()
    {
        using var scene = Scene();
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        Set(scene);
        int workers = 0;
        using var hooks = ContactNavigationBuildHooks.Install(new() { WorkerPinned = _ => workers++ });
        NavBakeProfile[] profiles =
        [
            new("a-legacy", Tuning with { Controller = MovementControllerKind.Legacy, Water = default }, default),
            new("b-contact", Tuning, default),
        ];
        var refusal = Assert.Throws<InvalidOperationException>(() =>
            GroundNavigationBake.Create(capture, ContactBakeFile.Sources(), profiles, new(2)));
        Assert.Contains("runtime statics", refusal.Message);
        Assert.Equal(0, workers);
    }

    [Fact]
    public void RemovingAStaticDoesNotMakeAnOldCaptureCurrentAgain()
    {
        using var scene = Scene();
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        Set(scene);
        Remove(scene);
        Assert.Throws<InvalidOperationException>(() => capture.BuildProfile(Tuning, default));
        using var fresh = PhysicsNavBake.Capture(context, Options, _ => 0);
        Assert.NotEmpty(fresh.BuildProfile(Tuning, default).Space.Layers);
        using var known = scene.Acquire();
    }

    [Fact]
    public void SanctionedAddAndRemoveDuringCaptureRefuseAllColumns()
    {
        using var scene = Scene();
        using var context = Context(scene);
        int samples = 0;
        Assert.Throws<InvalidOperationException>(() => PhysicsNavBake.Capture(context, Options, _ =>
        {
            if (++samples == 1) { Set(scene); Remove(scene); }
            return 0;
        }));
        Assert.Equal(1, samples);
        using var known = scene.Acquire();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SanctionedChangesBetweenWorkerFirstReadsRefuseTheWholeBuild(bool removeAgain)
    {
        using var scene = Scene();
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        var pinned = new List<int>();
        using var hooks = ContactNavigationBuildHooks.Install(new()
        {
            WorkerPinned = worker =>
            {
                pinned.Add(worker);
                if (worker == 0)
                {
                    Set(scene);
                    if (removeAgain) Remove(scene);
                }
            },
        });
        Assert.Throws<InvalidOperationException>(() =>
            capture.BuildProfile(Tuning, default, GroundProfileOptions.Default, new(2)));
        Assert.Equal([0], pinned);
        using var known = scene.Acquire();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SanctionedChangesAfterTheFinalProofBandRefusePublication(bool removeAgain)
    {
        using var scene = Scene();
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        GroundNavigation baseline = capture.BuildProfile(Tuning, default);
        Assert.Empty(baseline.Space.Links);
        int bands = ContactNavigationWorkers.BandCount(2, baseline.Space.Layers.Sum(layer => layer.Height));
        int completed = 0;
        using var hooks = ContactNavigationBuildHooks.Install(new()
        {
            BandCompleted = (phase, _) =>
            {
                if (phase != ContactNavigationPhase.Edges || Interlocked.Increment(ref completed) != bands) return;
                Set(scene);
                if (removeAgain) Remove(scene);
            },
        });
        Assert.Throws<InvalidOperationException>(() =>
            capture.BuildProfile(Tuning, default, GroundProfileOptions.Default, new(2)));
        Assert.Equal(bands, completed);
        using var known = scene.Acquire();
    }

    static OrdinaryContactScene Scene() => new(Tuning,
        install: (world, builder) => Box(world, builder, "floor", new(-8, -0.2f, -8), new(8, 0, 8)));

    static void Set(OrdinaryContactScene scene) =>
        scene.Registration.ApplyRuntimeStatics([RuntimeStaticRegistrationTests.Wall()]);

    static void Remove(OrdinaryContactScene scene) =>
        scene.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Remove(new("world", "wall"))]);
}
