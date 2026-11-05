using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapDocumentExceptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_constructors_keep_the_cause_and_leave_the_reason_unset(bool withInner)
    {
        var inner = new InvalidOperationException();
        var refusal = withInner ? new MapDocumentException("consumer refusal", inner) : new MapDocumentException("consumer refusal");
        Assert.Null(refusal.Reason);
        Assert.Same(withInner ? inner : null, refusal.InnerException);
    }

    [Fact]
    public void Structured_constructor_keeps_the_reason_and_original_cause()
    {
        var inner = new InvalidOperationException();
        var refusal = new MapDocumentException(MapDocumentRefusal.ResolverContractMismatch, "refused resolver", inner);
        Assert.Equal(MapDocumentRefusal.ResolverContractMismatch, refusal.Reason);
        Assert.Same(inner, refusal.InnerException);
    }

    [Fact]
    public void Findings_are_an_ordered_owned_read_only_snapshot()
    {
        var first = new MapDocumentFinding(MapDocumentRefusal.ResolverContractMismatch, "first") { ResolverVersion = 1 };
        var second = first with { ResolverVersion = 3 };
        var source = new List<MapDocumentFinding> { first, second };
        var refusal = new MapDocumentException(first.Reason, "refused document") { Findings = source };
        source.Reverse();
        source.Clear();
        Assert.NotNull(refusal.Findings);
        Assert.Equal(new[] { first, second }, refusal.Findings);
        if (refusal.Findings is IList<MapDocumentFinding> list)
        {
            Assert.Throws<NotSupportedException>(() => list[0] = second);
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
        Assert.Equal(new[] { first, second }, refusal.Findings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Source_context_preserves_every_identifier_and_the_original_refusal(bool legacy)
    {
        PropertyInfo[] identifiers = typeof(MapDocumentException)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanWrite).ToArray();
        Assert.Contains(identifiers, p => p.Name == nameof(MapDocumentException.SourceName));
        var refusal = legacy ? new MapDocumentException("consumer refusal")
            : new MapDocumentException(MapDocumentRefusal.ResolverContractMismatch, "resolver refusal");
        for (int i = 0; i < identifiers.Length; i++) identifiers[i].SetValue(refusal, Sample(identifiers[i], i));

        MapDocumentException named = refusal.WithSourceName("map.json");

        Assert.Equal(refusal.Reason, named.Reason);
        Assert.Same(refusal, named.InnerException);
        Assert.Equal("map.json", named.SourceName);
        Assert.Empty(identifiers.Where(p => p.Name != nameof(MapDocumentException.SourceName) &&
            !Matches(p.GetValue(refusal), p.GetValue(named))).Select(p => p.Name));
    }

    static object Sample(PropertyInfo property, int index)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(string)) return property.Name;
        if (type == typeof(int)) return 1000 + index;
        if (type == typeof(MapSupportRecipe)) return (MapSupportRecipe)(2000 + index);
        if (type == typeof(IReadOnlyList<MapDocumentFinding>))
            return new[] { new MapDocumentFinding(MapDocumentRefusal.ResolverContractMismatch, property.Name) { ResolverVersion = index } };
        throw new InvalidOperationException($"Add a sample for identifier {property.Name} of type {property.PropertyType}.");
    }

    static bool Matches(object? expected, object? actual) =>
        expected is IEnumerable sequence and not string && actual is IEnumerable other
            ? sequence.Cast<object?>().SequenceEqual(other.Cast<object?>()) : Equals(expected, actual);
}
