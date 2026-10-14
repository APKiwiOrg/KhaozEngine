using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Physics;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Structural retained array payload plus query scratch native capacity, read only after sampling.
/// Shared objects are counted once.</summary>
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

    // Sample only once the measured operations have returned. An operation restores its loaned set before it
    // returns, so every set is idle and visible here, whether its scratch is pooled or attached to a held lease.
    internal static long PayloadBytes(BepuPhysicsWorld world)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object? foot = typeof(FootSupport).GetField("t_scratch", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        object? installed = typeof(BepuPhysicsWorld).GetField("_installedSupportGeometry", Fields)!.GetValue(world);
        long total = Visit(foot, seen) + Visit(installed, seen);
        foreach (QueryScratch scratch in ConcurrentReadGateTests.Gate(world).ScratchPool.Constructed())
            total += QueryScratchBytes(scratch, seen);
        return total;
    }

    // The scratch's own Bepu pool is counted by capacity and never walked: its blocks are native, and Bepu 2.4.0
    // keeps them in a byte*[] that the array layout probe cannot instantiate a generic method for.
    static long QueryScratchBytes(QueryScratch scratch, HashSet<object> seen) =>
        Visit(scratch.Overlaps.Idle, seen) + Visit(scratch.SweepCandidates.Idle, seen) + Visit(scratch.Contacts.Idle, seen) +
        Visit(scratch.Footprints.Idle, seen) + Visit(scratch.Neighborhoods.Idle, seen) + scratch.NativeCapacity;

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
