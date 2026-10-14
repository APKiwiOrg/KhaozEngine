using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics.Bepu;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Structural retained array payload, read only after sampling. Shared objects are counted once.</summary>
internal static class SupportCostScratch
{
    readonly record struct ArrayLayout(int Stride, bool ContainsReferences);

    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly MethodInfo Size = typeof(SupportCostScratch).GetMethod(nameof(SizeOf), BindingFlags.Static | BindingFlags.NonPublic)!;
    static readonly MethodInfo References = typeof(SupportCostScratch).GetMethod(nameof(ContainsReferences), BindingFlags.Static | BindingFlags.NonPublic)!;
    static readonly ConcurrentDictionary<Type, ArrayLayout> ArrayLayouts = new();
    static readonly ConcurrentDictionary<Type, FieldInfo[]> ReferenceFields = new();
    static int SizeOf<T>() => Unsafe.SizeOf<T>();
    static bool ContainsReferences<T>() => RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    static ArrayLayout Layout(Type type) => ArrayLayouts.GetOrAdd(type, static element =>
        new((int)Size.MakeGenericMethod(element).Invoke(null, null)!,
            (bool)References.MakeGenericMethod(element).Invoke(null, null)!));

    static FieldInfo[] StorageFields(Type type) => ReferenceFields.GetOrAdd(type, static owner =>
        Array.FindAll(owner.GetFields(Fields), static field =>
            !field.FieldType.IsPointer && !field.FieldType.IsByRef && Layout(field.FieldType).ContainsReferences));

    internal static long PayloadBytes(BepuPhysicsWorld world)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object? foot = typeof(FootSupport).GetField("t_scratch", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        object? backend = typeof(BepuPhysicsWorld).GetField("_neighborhoodScratch", Fields)!.GetValue(world);
        object? footprint = typeof(BepuPhysicsWorld).GetField("_footprintScratch", Fields)!.GetValue(world);
        object? installed = typeof(BepuPhysicsWorld).GetField("_installedSupportGeometry", Fields)!.GetValue(world);
        return Visit(foot, seen) + Visit(backend, seen) + Visit(footprint, seen) + Visit(installed, seen);
    }

    static long Visit(object? value, HashSet<object> seen)
    {
        if (value is null || value is string || value is Delegate) return 0;
        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum) return 0;
        // Boxed value copies cannot own a reference cycle. Their referenced storage is deduplicated below.
        if (!type.IsValueType && !seen.Add(value)) return 0;
        if (value is Array array)
        {
            Type element = type.GetElementType()!;
            ArrayLayout layout = Layout(element);
            long bytes = array.LongLength * layout.Stride;
            // Inline values are already counted in the array payload. Follow only elements that can retain storage.
            if (layout.ContainsReferences)
                foreach (object? item in array) bytes += Visit(item, seen);
            return bytes;
        }
        // These roots contain reusable geometry and managed collection storage, never the simulation or a world.
        long total = 0;
        foreach (FieldInfo field in StorageFields(type))
        {
            total += Visit(field.GetValue(value), seen);
        }
        return total;
    }
}
