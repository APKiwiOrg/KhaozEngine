using System;
using System.Collections.Generic;

namespace KhaozEngine.Ecs;

/// <summary>Captures and restores signature creation order independently of the live rows in a snapshot.</summary>
internal static class WorldArchetypeHistory
{
    public static List<List<string>> Capture(World world, Func<Type, string> keyFor)
    {
        var history = new List<List<string>>();
        foreach (Archetype archetype in world.SaveArchetypes)
        {
            var signature = new List<string>(archetype.TypeIds.Length);
            foreach (int id in archetype.TypeIds)
                signature.Add(keyFor(world.Registry.TypeOf(id)));
            history.Add(signature);
        }
        return history;
    }

    public static void Restore(World world, List<List<string>>? history, IReadOnlyDictionary<string, Type> types)
    {
        if (history is null || history.Count == 0 || history[0] is not { Count: 0 })
            throw new InvalidOperationException("Archetype history must start with the empty signature.");

        var ordered = new List<Archetype>(history.Count);
        var retained = new Dictionary<ArchetypeSignature, Archetype>();
        foreach (List<string>? signature in history)
        {
            if (signature is null)
                throw new InvalidOperationException("Archetype history contains a null signature.");
            var ids = new int[signature.Count];
            var unique = new HashSet<int>();
            for (int i = 0; i < signature.Count; i++)
            {
                string? name = signature[i];
                if (name is null || !types.TryGetValue(name, out Type? type))
                    throw new InvalidOperationException($"Unknown component type '{name}' in archetype history.");
                int id = world.Registry.RegisterType(type);
                if (!unique.Add(id))
                    throw new InvalidOperationException($"Repeated component type '{name}' in archetype history.");
                ids[i] = id;
            }
            Array.Sort(ids);
            var key = new ArchetypeSignature(ids);
            if (retained.ContainsKey(key))
                throw new InvalidOperationException("Archetype history contains a repeated signature.");
            if (!world.Archetypes.TryGetValue(key, out Archetype? archetype))
                archetype = new Archetype(ids, world.Registry);
            retained.Add(key, archetype);
            ordered.Add(archetype);
        }

        foreach (Archetype archetype in world.ArchetypeOrder)
            if (archetype.Count != 0 && !retained.ContainsKey(new ArchetypeSignature(archetype.TypeIds)))
                throw new InvalidOperationException("Archetype history omits a live signature.");

        // Component-by-component loading can create empty prefixes absent from the saved world. Drop only those
        // unlisted empty archetypes, keeping live instances and their row/column references in the entity records.
        world.Archetypes.Clear();
        foreach (var (key, archetype) in retained)
            world.Archetypes.Add(key, archetype);
        world.ArchetypeOrder.Clear();
        world.ArchetypeOrder.AddRange(ordered);
        world.ArchetypeGen++;
    }
}
