using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics.Bepu;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Structural retained array payload, read only after sampling. Shared objects are counted once.</summary>
internal static class SupportCostScratch
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly MethodInfo Size = typeof(SupportCostScratch).GetMethod(nameof(SizeOf), BindingFlags.Static | BindingFlags.NonPublic)!;
    static int SizeOf<T>() => Unsafe.SizeOf<T>();

    internal static long PayloadBytes(BepuPhysicsWorld world)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object? foot = typeof(FootSupport).GetField("t_scratch", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        object? backend = typeof(BepuPhysicsWorld).GetField("_neighborhoodScratch", Fields)!.GetValue(world);
        return Visit(foot, seen) + Visit(backend, seen);
    }

    static long Visit(object? value, HashSet<object> seen)
    {
        if (value is null || value is string || value is Delegate || !seen.Add(value)) return 0;
        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum) return 0;
        if (value is Array array)
        {
            Type element = type.GetElementType()!;
            long bytes = array.LongLength * (int)Size.MakeGenericMethod(element).Invoke(null, null)!;
            if (!element.IsPrimitive && !element.IsEnum)
                foreach (object? item in array) bytes += Visit(item, seen);
            return bytes;
        }
        // These roots contain reusable geometry and managed collection storage, never the simulation or a world.
        long total = 0;
        foreach (FieldInfo field in type.GetFields(Fields))
        {
            if (field.FieldType.IsPrimitive || field.FieldType.IsEnum) continue;
            total += Visit(field.GetValue(value), seen);
        }
        return total;
    }
}
