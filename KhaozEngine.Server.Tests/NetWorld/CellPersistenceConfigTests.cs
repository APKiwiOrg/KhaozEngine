using System;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class CellPersistenceConfigTests
{
    [Fact]
    public void RegisterMigration_IsFluent()
    {
        var cfg = new CellPersistenceConfig { SchemaVersion = 3 };
        CellPersistenceConfig same = cfg.RegisterMigration(1, b => b).RegisterMigration(2, b => b);
        Assert.Same(cfg, same);
    }

    [Fact]
    public void RegisterMigration_DuplicateFromVersion_ThrowsImmediately()
    {
        var cfg = new CellPersistenceConfig();
        cfg.RegisterMigration(1, b => b);
        Assert.Throws<ArgumentException>(() => cfg.RegisterMigration(1, b => b));
    }

    [Fact]
    public void RegisterMigration_NullMigrate_Throws()
    {
        var cfg = new CellPersistenceConfig();
        Assert.Throws<ArgumentNullException>(() => cfg.RegisterMigration(1, null!));
    }
}
