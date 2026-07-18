using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file sealed class RecordSystem : ISystem
{
    private readonly List<string> _log;
    private readonly string _name;
    public RecordSystem(List<string> log, string name) { _log = log; _name = name; }
    public void Update(World world, float dt) => _log.Add(_name);
}

public class SystemGroupsTests
{
    [Fact]
    public void GroupsRunInDefinedOrderRegistrationWithin()
    {
        var log = new List<string>();
        var w = new World();
        w.AddSystem(new RecordSystem(log, "a1"), "alpha");
        w.AddSystem(new RecordSystem(log, "a2"), "alpha");
        w.AddSystem(new RecordSystem(log, "b1"), "beta");
        w.SetGroupOrder("beta", "alpha");
        w.Update(0f);
        Assert.Equal(new[] { "b1", "a1", "a2" }, log.ToArray());
    }

    [Fact]
    public void SetGroupOrderPreservesUnlistedGroups()
    {
        var log = new List<string>();
        var w = new World();
        w.AddSystem(new RecordSystem(log, "a"), "alpha");
        w.AddSystem(new RecordSystem(log, "b"), "beta");
        w.AddSystem(new RecordSystem(log, "c"), "gamma");
        w.SetGroupOrder("gamma");                       // only gamma listed
        w.Update(0f);
        Assert.Equal("gamma", w.SystemGroups[0]);
        Assert.Equal(new[] { "c", "a", "b" }, log.ToArray());   // gamma first, alpha/beta preserved after
    }

    [Fact]
    public void UpdateGroupRunsOnlyThatGroupAndRepeats()
    {
        var log = new List<string>();
        var w = new World();
        w.AddSystem(new RecordSystem(log, "sim"), "simulation");
        w.AddSystem(new RecordSystem(log, "draw"), "presentation");
        w.UpdateGroup("simulation", 0f);
        w.UpdateGroup("simulation", 0f);                // fixed-timestep shape
        Assert.Equal(new[] { "sim", "sim" }, log.ToArray());
    }

    [Fact]
    public void UnknownGroupThrows()
    {
        var w = new World();
        Assert.Throws<ArgumentException>(() => w.UpdateGroup("nope", 0f));
    }

    [Fact]
    public void DefaultGroupBackwardCompatible()
    {
        var log = new List<string>();
        var w = new World();
        w.AddSystem(new RecordSystem(log, "x"));
        w.AddSystem(new RecordSystem(log, "y"));
        w.Update(0f);
        Assert.Equal(new[] { "x", "y" }, log.ToArray());
        Assert.Equal(new[] { "default" }, w.SystemGroups.ToArray());
    }
}
