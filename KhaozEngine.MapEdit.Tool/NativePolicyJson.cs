using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapEdit;

/// <summary>Closed metadata serialization. Reflection enumerates the public value contract, never live resources.</summary>
internal static class NativePolicyJson
{
    internal static readonly JsonSerializerOptions Options = Create();
    static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(NativeJson.Options) { IgnoreReadOnlyProperties = true };
        o.Converters.Add(new VectorConverter()); o.Converters.Add(new DirectionConverter());
        o.Converters.Add(new ColourConverter()); o.Converters.Add(new MotionConverter()); return o;
    }
    internal static void Validate(JsonElement value, Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null) { if (value.ValueKind == JsonValueKind.Null) return; type = nullable; }
        if (value.ValueKind == JsonValueKind.Null) throw new ArgumentException("NullNativePolicyValue");
        if (type.IsEnum)
        { if (value.ValueKind != JsonValueKind.String || !Enum.GetNames(type).Contains(value.GetString(), StringComparer.Ordinal)) throw new ArgumentException("InvalidEnum"); return; }
        if (type == typeof(string)) { if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("InvalidString"); return; }
        if (type == typeof(bool)) { if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("InvalidBoolean"); return; }
        if (type.IsPrimitive || type == typeof(decimal))
        { if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double d) || !double.IsFinite(d) || !float.IsFinite((float)d)) throw new ArgumentException("InvalidNumber"); return; }
        var dictionary = type.GetInterfaces().Append(type).FirstOrDefault(t => t.IsGenericType &&
            (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) || t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        if (dictionary is not null)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("InvalidDictionary");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (dictionary.GetGenericArguments()[0] == typeof(ushort) && (!ushort.TryParse(p.Name, out var id) || id.ToString(System.Globalization.CultureInfo.InvariantCulture) != p.Name)) throw new ArgumentException("InvalidMaterialIdentity");
                if (string.IsNullOrWhiteSpace(p.Name) || !names.Add(p.Name)) throw new ArgumentException("DuplicateOrEmptyIdentity"); Validate(p.Value, dictionary.GetGenericArguments()[1]);
            }
            return;
        }
        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException("InvalidArray");
            Type item = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            foreach (var child in value.EnumerateArray()) Validate(child, item); return;
        }
        var members = Members(type);
        NativeJson.Object(value, string.Join(' ', members.Select(p => p.Name)));
        foreach (var member in members) Validate(value.GetProperty(member.Name), member.Type);
    }
    internal static (string Name, Type Type)[] Members(Type type)
    {
        if (type == typeof(FoliageMotionProfile)) return new[] { "bendScale", "maxLean", "windScale", "shiver" }.Select(n => (n, typeof(float))).ToArray();
        return type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => (Name: JsonNamingPolicy.CamelCase.ConvertName(f.Name), Type: f.FieldType))
            .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is not null && p.GetIndexParameters().Length == 0)
                .Select(p => (Name: JsonNamingPolicy.CamelCase.ConvertName(p.Name), Type: p.PropertyType))).ToArray();
    }
    sealed class VectorConverter : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        { using var d = JsonDocument.ParseValue(ref reader); var v = d.RootElement; return new(v.GetProperty("x").GetSingle(), v.GetProperty("y").GetSingle(), v.GetProperty("z").GetSingle()); }
        public override void Write(Utf8JsonWriter w, Vector3 v, JsonSerializerOptions options)
        { w.WriteStartObject(); w.WriteNumber("x", v.X); w.WriteNumber("y", v.Y); w.WriteNumber("z", v.Z); w.WriteEndObject(); }
    }
    sealed class DirectionConverter : JsonConverter<Vector2>
    {
        public override Vector2 Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        { using var d = JsonDocument.ParseValue(ref reader); var v = d.RootElement; return new(v.GetProperty("x").GetSingle(), v.GetProperty("y").GetSingle()); }
        public override void Write(Utf8JsonWriter w, Vector2 v, JsonSerializerOptions options)
        { w.WriteStartObject(); w.WriteNumber("x", v.X); w.WriteNumber("y", v.Y); w.WriteEndObject(); }
    }
    sealed class ColourConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        { using var d = JsonDocument.ParseValue(ref reader); var v = d.RootElement; return new(v.GetProperty("r").GetSingle(), v.GetProperty("g").GetSingle(), v.GetProperty("b").GetSingle(), v.GetProperty("a").GetSingle()); }
        public override void Write(Utf8JsonWriter w, Color v, JsonSerializerOptions options)
        { w.WriteStartObject(); w.WriteNumber("r", v.R); w.WriteNumber("g", v.G); w.WriteNumber("b", v.B); w.WriteNumber("a", v.A); w.WriteEndObject(); }
    }
    sealed class MotionConverter : JsonConverter<FoliageMotionProfile>
    {
        public override FoliageMotionProfile Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        { using var d = JsonDocument.ParseValue(ref reader); var v = d.RootElement; return new(v.GetProperty("bendScale").GetSingle(), v.GetProperty("maxLean").GetSingle(), v.GetProperty("windScale").GetSingle(), v.GetProperty("shiver").GetSingle()); }
        public override void Write(Utf8JsonWriter w, FoliageMotionProfile v, JsonSerializerOptions options)
        { w.WriteStartObject(); w.WriteNumber("bendScale", v.BendScale); w.WriteNumber("maxLean", v.MaxLean); w.WriteNumber("windScale", v.WindScale); w.WriteNumber("shiver", v.Shiver); w.WriteEndObject(); }
    }
}
