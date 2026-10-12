using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData("cylinder")]
    [InlineData("capsule")]
    [InlineData("equality")]
    public void CurvedTiltedWarmWitnessesReuseOwnedScratch(string shape)
    {
        using var scene = new SupportFootprintScene();
        bool equality = shape == "equality";
        scene.Install(shape == "cylinder" ? new CylinderShape(0.3f, 2) :
            equality ? new CapsuleShape(0.25f, 1) : new CapsuleShape(0.3f, 2),
            new(new(0, equality ? 0 : -0.25f, 0), equality ? new Quaternion(0, 0, 0.5f, MathF.Sqrt(3) / 2) :
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6)));
        var request = equality ? Request(z: 0.29f, cosine: 0.8f) : Request(z: 0.1f, lower: -2, upper: 2);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256]; var joins = new ulong[1024];
        var capability = Capability(scene.World);
        long coldBefore = GC.GetAllocatedBytesForCurrentThread();
        var cold = capability.QuerySupportFootprint(lease, request, members, joins);
        long coldBytes = GC.GetAllocatedBytesForCurrentThread() - coldBefore;
        Assert.Equal(CapsuleFeatureStatus.Complete, cold.Status);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var warm = capability.QuerySupportFootprint(lease, request, members, joins);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(CapsuleFeatureStatus.Complete, warm.Status);
        Assert.Equal(0, bytes);
        var (tilted, _, _) = TiltedScratch(scene.World);
        _output.WriteLine($"{shape} cold-query-bytes={coldBytes} tilted-array-payload={ArrayPayload(tilted)} " +
            $"whole-curved-array-payload={ArrayPayload(CurvedScratch(scene.World))} warm-bytes={bytes} {TiltedStatistics(scene.World)}");
    }

    static long ArrayPayload(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>();
        var sizes = new Dictionary<Type, int>();
        pending.Push(root);
        long bytes = 0;
        MethodInfo sizeOf = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!;
        while (pending.TryPop(out object? item))
        {
            Type type = item.GetType();
            if (!type.IsValueType && !seen.Add(item)) continue;
            if (item is Array array)
            {
                Type element = type.GetElementType()!;
                if (!sizes.TryGetValue(element, out int size))
                    sizes[element] = size = element.IsValueType ? (int)sizeOf.MakeGenericMethod(element).Invoke(null, null)! : IntPtr.Size;
                bytes += array.LongLength * size;
                foreach (object? entry in array)
                    if (entry is not null && (entry.GetType().Namespace ?? "").StartsWith("KhaozEngine.Physics.Bepu", StringComparison.Ordinal))
                        pending.Push(entry);
                continue;
            }
            if (!(type.Namespace ?? "").StartsWith("KhaozEngine.Physics.Bepu", StringComparison.Ordinal)) continue;
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                object? value = field.GetValue(item);
                if (value is not null && (value is Array ||
                    (value.GetType().Namespace ?? "").StartsWith("KhaozEngine.Physics.Bepu", StringComparison.Ordinal))) pending.Push(value);
            }
        }
        return bytes;
    }
}
