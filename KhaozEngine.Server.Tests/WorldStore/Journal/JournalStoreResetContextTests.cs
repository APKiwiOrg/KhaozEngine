using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class JournalStoreResetContextTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Reset_context_preserves_every_public_identifier_and_the_original_refusal(bool legacy, bool operationScope)
    {
        var scope = operationScope ? JournalStoreFailureScope.OperationStreams : JournalStoreFailureScope.WholeStore;
        string[] keys = operationScope ? ["stream/a", "stream/b"] : [];
        var refusal = legacy
            ? new JournalStoreException(JournalStoreFailureKind.Timeout, JournalStoreFailureCertainty.DefinitelyNotCommitted,
                scope, keys, "consumer refusal")
            : new JournalStoreException(JournalStoreRefusal.ApplicationLockNotGranted, JournalStoreFailureKind.Timeout,
                JournalStoreFailureCertainty.DefinitelyNotCommitted, scope, keys, "lock refusal");
        PropertyInfo[] identifiers = typeof(JournalStoreException)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(property => property.CanWrite).ToArray();
        Assert.NotEmpty(identifiers);
        for (int i = 0; i < identifiers.Length; i++) identifiers[i].SetValue(refusal, Sample(identifiers[i].PropertyType, i));
        JournalStoreException wrapped = refusal.WithResetContext("reset refused");
        Assert.Same(refusal, wrapped.InnerException);
        Assert.Equal(refusal.Reason, wrapped.Reason);
        Assert.Equal(refusal.Kind, wrapped.Kind);
        Assert.Equal(refusal.Certainty, wrapped.Certainty);
        Assert.Equal(refusal.Scope, wrapped.Scope);
        Assert.Equal(refusal.StreamKeys, wrapped.StreamKeys);
        Assert.Empty(identifiers.Where(property => !Matches(property.GetValue(refusal), property.GetValue(wrapped)))
            .Select(property => property.Name));
    }

    private static object Sample(Type type, int index)
    {
        Type valueType = Nullable.GetUnderlyingType(type) ?? type;
        if (valueType == typeof(string)) return "identifier/" + index;
        if (valueType == typeof(long)) return 100L + index;
        if (valueType == typeof(int)) return 10 + index;
        if (valueType == typeof(Guid)) return Guid.Parse("11223344-5566-7788-9900-aabbccddeeff");
        if (valueType == typeof(TimeSpan)) return TimeSpan.FromSeconds(17);
        if (valueType == typeof(Type)) return typeof(decimal);
        if (valueType == typeof(JournalOperationStage)) return JournalOperationStage.Commit;
        if (valueType == typeof(IReadOnlyList<long>)) return new long[] { 2, 3 };
        if (valueType == typeof(IReadOnlyList<string>)) return new[] { "object/a", "object/b" };
        if (valueType == typeof(IReadOnlyList<JournalHostObjectFinding>))
            return new[] { new JournalHostObjectFinding(JournalStoreRefusal.HostTriggerUnsafe, null, "journal_stream", null, "host_trigger") };
        throw new Xunit.Sdk.XunitException("Add a sample for public identifier type " + type.Name);
    }

    private static bool Matches(object? expected, object? actual)
        => expected is IEnumerable left && actual is IEnumerable right && expected is not string
            ? left.Cast<object>().SequenceEqual(right.Cast<object>())
            : Equals(expected, actual);
}
