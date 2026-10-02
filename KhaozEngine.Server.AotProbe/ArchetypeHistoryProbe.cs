using System.Linq;
using KhaozEngine.Ecs;

/// <summary>Checks historical-signature continuation and legacy migration in the native probe process.</summary>
internal static class ArchetypeHistoryProbe
{
    public static bool Matches(WorldSerializer serializer)
    {
        var uninterrupted = new World();
        Entity first = uninterrupted.Spawn();
        uninterrupted.Set(first, new ProbePosition { X = 3f, Y = 4f });
        uninterrupted.Set(first, new ProbeElite());
        Entity second = uninterrupted.Spawn();
        uninterrupted.Set(second, new ProbePosition { X = 7f, Y = 8f });
        uninterrupted.Set(second, new ProbeHealth { Value = 42 });
        uninterrupted.Despawn(first);

        World restored = serializer.Load(serializer.Save(uninterrupted));
        Entity continued = uninterrupted.Spawn();
        Entity resumed = restored.Spawn();
        uninterrupted.Set(continued, new ProbePosition { X = 9f, Y = 10f });
        uninterrupted.Set(continued, new ProbeElite());
        restored.Set(resumed, new ProbePosition { X = 9f, Y = 10f });
        restored.Set(resumed, new ProbeElite());

        Entity[] expected = [continued, second];
        bool orderMatches = uninterrupted.Query().With<ProbePosition>().Entities().SequenceEqual(expected)
            && restored.Query().With<ProbePosition>().Entities().SequenceEqual(expected);
        bool stateMatches = continued == new Entity(0, 2) && resumed == continued
            && !restored.IsAlive(first) && restored.Has<ProbeElite>(resumed)
            && restored.Get<ProbePosition>(resumed).X == 9f
            && restored.Get<ProbePosition>(second).Y == 8f
            && restored.Get<ProbeHealth>(second).Value == 42
            && uninterrupted.Spawn() == new Entity(2, 1) && restored.Spawn() == new Entity(2, 1);
        return orderMatches && stateMatches && LegacyMigrationsMatch(serializer);
    }

    static bool LegacyMigrationsMatch(WorldSerializer serializer)
    {
        // This executable owns the migration table for its process. Unit tests need not retain global hooks.
        bool fromZero = false;
        WorldSerializer.RegisterMigration(0, root =>
        {
            fromZero = true;
            root["NextId"] = 4;
            return root;
        });
        const string legacy = "{\"FormatVersion\":0,\"NextId\":0,\"FreeIds\":[],\"Entities\":[]}";
        World upgraded = serializer.Load(legacy);
        bool builtInMatches = fromZero && upgraded.Spawn() == new Entity(4, 1);

        fromZero = false;
        bool fromOne = false;
        WorldSerializer.RegisterMigration(1, root =>
        {
            fromOne = true;
            root["NextId"] = 5;
            return root;
        });
        World callerUpgraded = serializer.Load(legacy);
        return builtInMatches && fromZero && fromOne && callerUpgraded.Spawn() == new Entity(5, 1);
    }
}
