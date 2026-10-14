using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;
using static KhaozEngine.Tests.Movement.Fixtures.SmallStairScene;

namespace KhaozEngine.Tests.Movement;

public class NavBakeExecutionTests
{
    [Fact]
    public void DegreeBelowOneIsRefusedBeforeEveryOtherArgument()
    {
        using var scene = new SmallStairScene();
        using PhysicsNavBake capture = scene.Capture();
        var zero = new NavBakeExecution(0);
        Assert.Equal("execution", Assert.Throws<ArgumentOutOfRangeException>(() =>
            capture.BuildProfile(StairTuning, default, GroundProfileOptions.Default, zero)).ParamName);
        Assert.Equal("execution", Assert.Throws<ArgumentOutOfRangeException>(() =>
            capture.BuildProfile(StairTuning, default, null, zero)).ParamName);
        Assert.Equal("execution", Assert.Throws<ArgumentOutOfRangeException>(() =>
            capture.BuildProfile(StairTuning with { MaxSlopeRadians = 0.1f }, default, GroundProfileOptions.Default,
                new NavBakeExecution(-3))).ParamName);
        Assert.Equal("execution", Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroundNavigationBake.Create(null!, null!, null!, zero)).ParamName);
        Assert.Equal("execution", Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroundNavigationBake.Create(capture, ContactBakeFile.Sources(),
                [new NavBakeProfile("player", StairTuning, default)], zero)).ParamName);
    }

    [Fact]
    public void NullExecutionRunsTheSerialBuild()
    {
        using var scene = new SmallStairScene();
        using PhysicsNavBake capture = scene.Capture();
        SameGraphBits(capture.BuildProfile(StairTuning, default, GroundProfileOptions.Default),
            capture.BuildProfile(StairTuning, default, GroundProfileOptions.Default, null));
        NavBakeProfile[] profiles = [new("player", StairTuning, default)];
        Assert.Equal(ContactBakeFile.Write(GroundNavigationBake.Create(capture, ContactBakeFile.Sources(), profiles)),
            ContactBakeFile.Write(GroundNavigationBake.Create(capture, ContactBakeFile.Sources(), profiles, null)));
    }

#nullable disable
    [Fact]
    public void ExistingNullAndDefaultOptionCallFormsStayUnambiguous()
    {
        using var scene = new SmallStairScene();
        using PhysicsNavBake capture = scene.Capture();
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => capture.BuildProfile(StairTuning, default, null)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => capture.BuildProfile(StairTuning, default, default)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() =>
            capture.BuildProfile(StairTuning, default, null, null)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() =>
            capture.BuildProfile(StairTuning, default, default, default)).ParamName);
    }
#nullable restore

    [Fact]
    public void AllLegacyBuildAboveDegreeOneOnAnUndeclaredBackendRunsSerially()
    {
        using var world = new ObservedPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(8f, 0.1f, 8f)), Pose.At(new Vector3(0f, -0.1f, 0f)));
        Assert.IsNotAssignableFrom<IPhysicsConcurrentReadSource>(world);
        using var context = new GroundMoveContext((_, _) => 0f, physics: world);
        MoveTuning legacy = GroundTraversalProbeTests.Tuning;
        Assert.Equal(MovementControllerKind.Legacy, legacy.Controller);
        PhysicsNavBakeOptions options = Options with
        {
            MinX = -0.5f,
            MaxX = 0.5f,
            MinZ = -0.5f,
            MaxZ = 0.5f,
            MaxSlopeRadians = legacy.MaxSlopeRadians,
        };
        using var capture = PhysicsNavBake.Capture(context, options, _ => 0);
        GroundNavigation serial = capture.BuildProfile(legacy, default);
        SameGraphBits(serial, capture.BuildProfile(legacy, default, GroundProfileOptions.Default, new NavBakeExecution(8)));
        NavBakeProfile[] profiles = [new("legacy", legacy, default)];
        Assert.Equal(ContactBakeFile.Write(GroundNavigationBake.Create(capture, ContactBakeFile.Sources(), profiles)),
            ContactBakeFile.Write(GroundNavigationBake.Create(capture, ContactBakeFile.Sources(), profiles,
                new NavBakeExecution(8))));
    }

    [Fact]
    public void BorrowedWrapperWithoutTheCapabilityRefusesContactAboveDegreeOne()
    {
        using var scene = new SmallStairScene(concurrentView: false);
        Assert.IsAssignableFrom<IPhysicsConcurrentReadSource>(scene.Physics);
        Assert.IsNotAssignableFrom<IPhysicsConcurrentReadSource>(scene.View);
        using PhysicsNavBake capture = scene.Capture();
        // Existing argument checks keep their place ahead of the capability.
        Assert.Throws<ArgumentException>(() => capture.BuildProfile(StairTuning with { MaxSlopeRadians = 0.1f }, default,
            GroundProfileOptions.Default, new NavBakeExecution(2)));
        Assert.Throws<NotSupportedException>(() =>
            capture.BuildProfile(StairTuning, default, GroundProfileOptions.Default, new NavBakeExecution(2)));
        Assert.Throws<NotSupportedException>(() => GroundNavigationBake.Create(capture, ContactBakeFile.Sources(),
            [new NavBakeProfile("player", StairTuning, default)], new NavBakeExecution(2)));
        // The refusal leaves the capture, world and view usable, and degree 1 still builds serially.
        GroundNavigation serial = capture.BuildProfile(StairTuning, default, GroundProfileOptions.Default,
            new NavBakeExecution(1));
        using var declared = new SmallStairScene();
        SameGraphBits(declared.Build(null), serial);
        scene.AssertUsable();
    }
}
