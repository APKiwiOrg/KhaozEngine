using System;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Content;

/// <summary>
/// The eighteen types' <c>&lt;Field&gt;Index</c> constants against the schemas that define them. A positional
/// row walk reads a field by its INDEX, so an index constant that does not match its schema position is a
/// reader silently pointed at a neighbouring value of the same kind, which no other fact would catch.
/// <para>
/// <b>The pairing is read off the constants rather than pinned in a list here.</b> Every
/// <c>&lt;X&gt;Field</c> constant has to have an <c>&lt;X&gt;Index</c> beside it, and the count has to match
/// the schema's, so ADDING a field to a schema without its index constant fails here rather than in a
/// reader months later.
/// </para>
/// <para>
/// Every registry a fact builds is its OWN, so nothing here writes process-global state and no
/// <c>DisableParallelization</c> collection is needed.
/// </para>
/// </summary>
public class ContentTypeFieldIndexTests
{
    const string FieldSuffix = "Field";

    const string IndexSuffix = "Index";

    /// <summary>The eighteen types of spec 8.1, each paired with the class that declares its schema.</summary>
    static readonly (string TypeKey, Type Declaring)[] Eighteen =
    [
        (InstanceContentTypeIds.ModTypeKey, typeof(ModContentType)),
        (InstanceContentTypeIds.ModGroupTypeKey, typeof(ModGroupContentType)),
        (InstanceContentTypeIds.ModTierTypeKey, typeof(ModTierContentType)),
        (InstanceContentTypeIds.ModTierWeightTypeKey, typeof(ModTierWeightContentType)),
        (InstanceContentTypeIds.StatLineTypeKey, typeof(StatLineContentType)),
        (InstanceContentTypeIds.RarityRuleTypeKey, typeof(RarityRuleContentType)),
        (InstanceContentTypeIds.RarityWeightTypeKey, typeof(RarityWeightContentType)),
        (InstanceContentTypeIds.RarityKindLimitTypeKey, typeof(RarityKindLimitContentType)),
        (InstanceContentTypeIds.UniqueTemplateTypeKey, typeof(UniqueTemplateContentType)),
        (InstanceContentTypeIds.UniqueLineTypeKey, typeof(UniqueLineContentType)),
        (InstanceContentTypeIds.UniqueSocketTypeKey, typeof(UniqueSocketContentType)),
        (InstanceContentTypeIds.SocketTypeTypeKey, typeof(SocketTypeContentType)),
        (InstanceContentTypeIds.SocketTagRuleTypeKey, typeof(SocketTagRuleContentType)),
        (InstanceContentTypeIds.RareNameWordTypeKey, typeof(RareNameWordContentType)),
        (InstanceContentTypeIds.RareNameWordWeightTypeKey, typeof(RareNameWordWeightContentType)),
        (InstanceContentTypeIds.CraftingCurrencyTypeKey, typeof(CraftingCurrencyContentType)),
        (InstanceContentTypeIds.CurrencyStepTypeKey, typeof(CurrencyStepContentType)),
        (InstanceContentTypeIds.CurrencyGuardTypeKey, typeof(CurrencyGuardContentType)),
    ];

    public static TheoryData<string> TypeKeys
    {
        get
        {
            var data = new TheoryData<string>();
            foreach ((string typeKey, _) in Eighteen)
            {
                data.Add(typeKey);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(TypeKeys))]
    public void Every_field_index_constant_is_that_fields_position_in_the_schema(string typeKey)
    {
        Type declaring = Declaring(typeKey);
        ContentFieldSchema schema = Schema(declaring);
        Dictionary<string, string> constantByFieldName = FieldConstants(declaring);

        // One index constant per schema field and no orphan left behind by a field that was removed.
        Assert.Equal(schema.Fields.Count, constantByFieldName.Count);

        for (int index = 0; index < schema.Fields.Count; index++)
        {
            string fieldName = schema.Fields[index].Name;
            Assert.True(
                constantByFieldName.TryGetValue(fieldName, out string? constant),
                typeKey + "." + fieldName + " has no " + FieldSuffix + " constant");

            string indexConstant = constant[..^FieldSuffix.Length] + IndexSuffix;
            Assert.Equal(index, Literal(declaring, indexConstant));
        }
    }

    [Fact]
    public void The_table_above_carries_every_type_the_band_registers()
    {
        var registry = new ContentTypeRegistry();
        InstanceContentTypes.Register(registry);

        var registered = new List<string>();
        foreach (ContentTypeRegistration registration in registry.ByTypeId)
        {
            registered.Add(registration.TypeKey);
        }

        var covered = new List<string>();
        foreach ((string typeKey, _) in Eighteen)
        {
            covered.Add(typeKey);
        }

        registered.Sort(StringComparer.Ordinal);
        covered.Sort(StringComparer.Ordinal);
        Assert.Equal(registered, covered);
    }

    static Type Declaring(string typeKey)
    {
        foreach ((string key, Type declaring) in Eighteen)
        {
            if (string.Equals(key, typeKey, StringComparison.Ordinal))
            {
                return declaring;
            }
        }

        throw new InvalidOperationException(typeKey);
    }

    static ContentFieldSchema Schema(Type declaring)
    {
        MethodInfo? create = declaring.GetMethod(
            "CreateSchema", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        Assert.NotNull(create);
        object? schema = create.Invoke(null, null);
        return Assert.IsType<ContentFieldSchema>(schema);
    }

    /// <summary>The field NAME each <c>&lt;X&gt;Field</c> constant carries, mapped back to the constant.</summary>
    static Dictionary<string, string> FieldConstants(Type declaring)
    {
        var byFieldName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (FieldInfo field in declaring.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (!field.IsLiteral || field.FieldType != typeof(string) || !field.Name.EndsWith(FieldSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            object? value = field.GetRawConstantValue();
            byFieldName.Add(Assert.IsType<string>(value), field.Name);
        }

        return byFieldName;
    }

    static int Literal(Type declaring, string name)
    {
        FieldInfo? constant = declaring.GetField(name, BindingFlags.Public | BindingFlags.Static);
        Assert.True(constant is not null && constant.IsLiteral, declaring.Name + " declares no " + name);
        return Assert.IsType<int>(constant.GetRawConstantValue());
    }
}
