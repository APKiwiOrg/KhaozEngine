using System;
using System.Reflection;
using KhaozEngine.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>Bitwise state equality for locomotion determinism parity (rule 8, owner decision D17).</summary>
internal static class LocomotionStateBits
{
    internal enum Policy { IncludeContinuityHandle, ExcludeContinuityHandle }

    internal static void AssertEqual<T>(T expected, T actual, LocomotionStateBits.Policy policy) =>
        AssertEqual(expected!, actual!, policy, "state");

    static void AssertEqual(object expected, object actual, Policy policy, string path)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        if (expected is float f)
        {
            Assert.True(BitConverter.SingleToInt32Bits(f) == BitConverter.SingleToInt32Bits((float)actual),
                $"{path}: expected {f:R}, actual {actual}");
            return;
        }
        if (expected is double d)
        {
            Assert.True(BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits((double)actual),
                $"{path}: expected {d:R}, actual {actual}");
            return;
        }
        Type type = expected.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(string))
        {
            Assert.True(expected.Equals(actual), $"{path}: {expected} against {actual}");
            return;
        }
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            // Each head or replay can own its interval, so geometric parity excludes CF-4 interval evidence.
            if (policy == Policy.ExcludeContinuityHandle && field.FieldType == typeof(SupportContinuityHandle)) continue;
            object? left = field.GetValue(expected), right = field.GetValue(actual);
            if (left is null) Assert.Null(right);
            else
            {
                Assert.NotNull(right);
                AssertEqual(left, right!, policy, path + "." + field.Name);
            }
        }
    }
}
