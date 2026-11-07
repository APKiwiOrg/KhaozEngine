using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfWorkflowManifestFieldParityTests
{
    [Fact]
    public void Selector_field_table_matches_the_managed_record_constructors()
    {
        Match table = Regex.Match(RendererPerfWorkflowText.Selector(), @"(?ms)^\$manifestFieldTable = @'\n(?<rows>.*?)^'@$");
        Assert.True(table.Success, "Missing anchored manifest field table");
        string[] actual = table.Groups["rows"].Value.TrimEnd('\n').Split('\n');
        foreach (string row in actual)
            Assert.Matches(@"\A[A-Za-z][A-Za-z0-9]*\|[a-z][A-Za-z0-9]*\|(?:string|integer|decimal|boolean|enum-string|string-map|(?:object|array):[A-Za-z][A-Za-z0-9]*)\|(?:non-null|nullable)\z", row);
        Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count());

        var expected = new List<string>();
        var visited = new HashSet<Type>();
        var nullability = new NullabilityInfoContext();
        AddFields(typeof(RendererPerfManifest));
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

        void AddFields(Type record)
        {
            if (!visited.Add(record)) return;
            ConstructorInfo constructor = Assert.Single(record.GetConstructors());
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                Assert.False(parameter.HasDefaultValue, "Field presence must follow required constructor parameters");
                NullabilityInfo info = nullability.Create(parameter);
                Assert.NotEqual(NullabilityState.Unknown, info.ReadState);
                string nullable = info.ReadState == NullabilityState.Nullable ? "nullable" : "non-null";
                string name = RendererPerfManifest.JsonOptions.PropertyNamingPolicy!.ConvertName(parameter.Name!);
                Type type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                string kind;
                if (type == typeof(string)) kind = "string";
                else if (type == typeof(int)) kind = "integer";
                else if (type == typeof(decimal)) kind = "decimal";
                else if (type == typeof(bool)) kind = "boolean";
                else if (type.IsEnum) kind = "enum-string";
                else if (type == typeof(ImmutableDictionary<string, string>))
                {
                    Assert.All(info.GenericTypeArguments, argument => Assert.Equal(NullabilityState.NotNull, argument.ReadState));
                    kind = "string-map";
                }
                else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
                {
                    Type element = type.GetGenericArguments()[0];
                    kind = "array:" + element.Name;
                    AddFields(element);
                }
                else
                {
                    Assert.Equal(typeof(RendererPerfManifest).Namespace, type.Namespace);
                    kind = "object:" + type.Name;
                    AddFields(type);
                }
                expected.Add($"{record.Name}|{name}|{kind}|{nullable}");
            }
        }
    }
}
