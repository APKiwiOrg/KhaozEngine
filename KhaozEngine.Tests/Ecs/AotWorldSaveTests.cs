using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests.Ecs;

// Component set for the AOT save/load path: a multi-field struct, a single-field struct, and a zero-field tag.
public struct AotPos : IComponent { public float X; public float Y; }
public struct AotHp : IComponent { public int Value; }
public struct AotTag : IComponent { }

// A type only this file references, so the process-wide column-factory table starts empty for it.
public struct AotFactoryProbe : IComponent { public int N; }

// Source-generated context for the component structs: proves the WorldSerializer save/load path resolves per-type
// JSON through a JsonSerializerContext (the NativeAOT-safe path), not reflection. IncludeFields because they use fields.
[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(AotPos))]
[JsonSerializable(typeof(AotHp))]
[JsonSerializable(typeof(AotTag))]
internal partial class AotWorldJsonContext : JsonSerializerContext { }

public class AotWorldSaveTests
{
    [Fact]
    public void GenericBuilder_RoundTripsThroughSourceGenContext()
    {
        // The generic Create().Add<T>() seam + a source-generated component context = the NativeAOT-safe path.
        WorldSerializer Ser() => WorldSerializer.Create()
            .Add<AotPos>().Add<AotHp>().Add<AotTag>()
            .Build(AotWorldJsonContext.Default.Options);

        var w = new World();
        Entity a = w.Spawn();
        w.Set(a, new AotPos { X = 3f, Y = 4f });
        w.Set(a, new AotHp { Value = 42 });
        w.Set(a, new AotTag());                 // zero-field tag: presence must survive
        Entity b = w.Spawn();
        w.Set(b, new AotHp { Value = 7 });      // no tag: absence must survive

        World loaded = Ser().Load(Ser().Save(w));

        Assert.True(loaded.IsAlive(a));
        Assert.True(loaded.IsAlive(b));
        Assert.Equal(3f, loaded.Get<AotPos>(a).X);
        Assert.Equal(42, loaded.Get<AotHp>(a).Value);
        Assert.True(loaded.Has<AotTag>(a));
        Assert.False(loaded.Has<AotTag>(b));
        Assert.Equal(7, loaded.Get<AotHp>(b).Value);
    }

    [Fact]
    public void Builder_Add_PopulatesTypeKeyedColumnFactory()
    {
        // Before registration the process-wide table has no factory for this type.
        Assert.False(ComponentColumnFactory.TryGet(typeof(AotFactoryProbe), out _));

        WorldSerializer.Create().Add<AotFactoryProbe>().Build();

        // After the generic Add<T>, the reflection-free column factory is registered and builds a Column<AotFactoryProbe>.
        Assert.True(ComponentColumnFactory.TryGet(typeof(AotFactoryProbe), out ComponentColumnFactory.Entry entry));
        Assert.False(entry.IsTag);
        Column col = entry.Factory();
        Assert.IsType<Column<AotFactoryProbe>>(col);
    }

    [Fact]
    public void Load_UsesTypeKeyedFactory_ForFreshWorld_WithoutReflectionFallback()
    {
        // A serializer built through the generic seam registers the column factories, so a FRESH world (never touched
        // through the generic ECS API in this flow) rebuilds its columns from the Type-keyed table on load.
        WorldSerializer Ser() => WorldSerializer.Create().Add<AotPos>().Add<AotTag>().Build(AotWorldJsonContext.Default.Options);

        var w = new World();
        Entity e = w.Spawn();
        w.Set(e, new AotPos { X = 11f, Y = 22f });
        w.Set(e, new AotTag());
        string json = Ser().Save(w);

        World loaded = Ser().Load(json);
        Assert.Equal(11f, loaded.Get<AotPos>(e).X);
        Assert.True(loaded.Has<AotTag>(e));
    }
}
