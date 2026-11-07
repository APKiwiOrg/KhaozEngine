using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Cold wire encoding. Layouts contain every named metric, including unavailable entries.
// Only Available entries consume numeric vector slots. Zero is always an explicit value.
internal static class RendererPerfRawJson
{
    internal sealed record MetricSchema(string? Name, string Unit, string Source, RendererPerfMetricStatus Status, string? Reason);
    internal sealed record PassSchema(string Id, int Occurrence, string? ParentId, int? ParentOccurrence,
        uint BeginMarker, uint EndMarker, bool DeviceOnly);
    internal sealed record CpuSchema(string Id, string? ParentId);
    internal sealed record Schema(string Format, List<MetricSchema> Metrics, List<int[]> Layouts,
        List<PassSchema> Passes, List<CpuSchema> CpuSpans, List<int[]> ZeroRuns);
    readonly record struct PassKey(int Schema, ulong? ElapsedTicks, int DurationSchema, long? DurationBits);
    sealed record CpuValue(int Schema, long StartTicks, long EndTicks);
    sealed record Scalar(int Schema, double? Value);
    sealed record Vector(int Layout, ImmutableArray<double> Values, int ZeroRuns);
    sealed record CpuIntervals(long OriginTicks, int[] Schemas, ulong[] StartOffsets, ulong[] Durations);
    sealed record WireReport(string Schema, RendererPerfManifest Manifest, RendererPerfRunIdentity Identity,
        ImmutableArray<RendererPerfRecipeReport> Recipes, ImmutableArray<RendererPerfBlockReport> Blocks,
        RendererPerfMemoryReport Memory, ImmutableArray<GpuPipelineCompilerReport> Pipelines,
        ImmutableArray<RendererPerfControlReport> Controls, RendererPerfAssessment Assessment,
        RendererPerfPublication Publication, ImmutableArray<RendererPerfExpectedSidecar> Sidecars,
        ImmutableArray<RendererPerfFrameSample> Conditioning, ImmutableArray<RendererPerfBatchReport> Batches,
        ImmutableArray<RendererPerfStartupReport> Startup, RendererPerfCompilerEvidence Compiler,
        RendererPerfGpuClockVerification GpuClock, [property: JsonPropertyOrder(100)] Schema Raw)
    {
        internal RendererPerfReport Report()
        {
            var report = new RendererPerfReport(Schema, Manifest, Identity, Recipes, Blocks, Memory,
                Pipelines, Controls, Assessment, Publication, Sidecars)
            { Conditioning = Conditioning, Batches = Batches, Startup = Startup, Compiler = Compiler };
            RendererPerfGpuClockPolicy.ValidateClaim(report, GpuClock);
            return report;
        }
    }

    internal static byte[] Serialize(RendererPerfReport report, int maximumBytes)
    {
        var tables = new Tables(new("metric-vectors/2", [], [], [], [], []));
        var wire = new WireReport(report.Schema, report.Manifest, report.Identity, report.Recipes, report.Blocks,
            report.Memory, report.Pipelines, report.Controls, report.Assessment, report.Publication, report.Sidecars,
            report.Conditioning, report.Batches, report.Startup, report.Compiler, report.GpuClock, tables.Schema);
        using var stream = new BoundedStream(maximumBytes);
        JsonSerializer.Serialize(stream, wire, tables.Options);
        return stream.ToArray();
    }

    internal static RendererPerfReport Parse(ReadOnlySpan<byte> bytes, int maximumBytes)
    {
        try
        {
            using JsonDocument document = RendererPerfManifest.ReadDocument(bytes, maximumBytes);
            if (!document.RootElement.TryGetProperty("schema", out var schemaValue) || schemaValue.ValueKind != JsonValueKind.String)
                throw new JsonException("Report schema is missing or invalid.");
            string? version = schemaValue.GetString();
            if (version != "khaozengine.renderer-perf-report/2")
                throw new RendererPerfProtocolException("Schema", "Unsupported renderer report version " + version + ", expected renderer-perf-report/2.");
            if (document.RootElement.TryGetProperty("pipelines", out var pipelines) && pipelines.ValueKind == JsonValueKind.Array && pipelines.GetArrayLength() > 1024
                || document.RootElement.TryGetProperty("compiler", out var compiler) && compiler.ValueKind == JsonValueKind.Object
                && compiler.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 1024)
                throw new JsonException("Compiler report/result count exceeds the unique recipe capacity.");
            if (!document.RootElement.TryGetProperty("raw", out var raw)) throw new JsonException("Raw metric schema is required.");
            Schema schema = raw.Deserialize<Schema>(RendererPerfManifest.JsonOptions) ?? throw new JsonException("Null raw schema.");
            var tables = new Tables(schema);
            tables.Validate();
            return (document.RootElement.Deserialize<WireReport>(tables.Options) ?? throw new JsonException("Null report.")).Report();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new RendererPerfProtocolException("Json", error.Message, error);
        }
    }

    sealed class Tables
    {
        internal readonly Schema Schema;
        internal readonly JsonSerializerOptions Options;
        readonly Dictionary<MetricSchema, int> _metrics = [];
        readonly Dictionary<string, int> _layouts = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> _zeroRuns = new(StringComparer.Ordinal);
        readonly Dictionary<ImmutableDictionary<string, RendererPerfMetric>, Vector> _encoded = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<Vector, ImmutableDictionary<string, RendererPerfMetric>> _decoded = new(new VectorComparer());
        readonly Dictionary<(int, long?), RendererPerfMetric> _scalars = [];
        readonly Dictionary<PassSchema, int> _passes = [];
        readonly Dictionary<CpuSchema, int> _cpuSpans = [];
        readonly Dictionary<PassKey, RendererPerfPassSample> _decodedPasses = [];
        internal Tables(Schema schema)
        {
            Schema = schema;
            Options = new(RendererPerfManifest.JsonOptions)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver
                {
                    Modifiers = { RendererPerfCompilerValidation.RequireScalarMembers }
                }
            };
            Options.Converters.Add(new MetricConverter(this));
            Options.Converters.Add(new MapConverter(this));
            Options.Converters.Add(new PassConverter(this));
            Options.Converters.Add(new CpuConverter(this));
            Options.Converters.Add(new CpuIntervalsConverter(this));
        }
        internal void Validate()
        {
            if (Schema.Format != "metric-vectors/2") throw new JsonException("Unknown raw metric format.");
            if (Schema.Passes.Distinct().Count() != Schema.Passes.Count || Schema.CpuSpans.Distinct().Count() != Schema.CpuSpans.Count)
                throw new JsonException("Duplicate pass or CPU schema.");
            foreach (var pass in Schema.Passes)
                if (!RendererPerfManifest.Text(pass.Id) || pass.Occurrence < 0 || (pass.ParentId is null) != (pass.ParentOccurrence is null)
                    || (pass.DeviceOnly ? pass.BeginMarker != 0 || pass.EndMarker != 0
                        : pass.BeginMarker < 1 || pass.BeginMarker >= pass.EndMarker || pass.EndMarker > 510))
                    throw new RendererPerfProtocolException("Sample", "Invalid frozen pass schema.");
            foreach (var cpu in Schema.CpuSpans)
                if (!RendererPerfManifest.Text(cpu.Id)) throw new RendererPerfProtocolException("Sample", "Missing CPU span ID.");
            var distinct = new HashSet<MetricSchema>();
            foreach (var schema in Schema.Metrics)
            {
                if (schema is null || !distinct.Add(schema) || schema.Name is not null && !RendererPerfManifest.Text(schema.Name))
                    throw new JsonException("Null, duplicate or unnamed metric schema.");
                RendererPerfReportJson.Metric(new RendererPerfMetric(schema.Status == RendererPerfMetricStatus.Available ? 0 : null,
                    schema.Unit, schema.Source, schema.Status, schema.Reason));
            }
            var layouts = new HashSet<string>(StringComparer.Ordinal);
            foreach (int[] layout in Schema.Layouts)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (!layouts.Add(string.Join(',', layout))) throw new JsonException("Duplicate metric layout.");
                foreach (int id in layout)
                {
                    MetricSchema metric = Get(id);
                    if (metric.Name is null || !names.Add(metric.Name)) throw new JsonException("Layout requires unique named metrics.");
                }
            }
            var runs = new HashSet<string>(StringComparer.Ordinal);
            foreach (int[] pattern in Schema.ZeroRuns)
                if (pattern is null || pattern.Length == 0 || pattern.Any(n => n < 0) || !runs.Add(string.Join(',', pattern)))
                    throw new JsonException("Empty, negative or duplicate zero-run pattern.");
        }
        MetricSchema Get(int id) => (uint)id < (uint)Schema.Metrics.Count ? Schema.Metrics[id]
            : throw new JsonException("Metric schema reference is outside the table.");
        internal int Register(string? name, RendererPerfMetric metric)
        {
            RendererPerfReportJson.Metric(metric);
            var schema = new MetricSchema(name, metric.Unit, metric.Source, metric.Status, metric.Reason);
            if (!_metrics.TryGetValue(schema, out int id))
            { id = Schema.Metrics.Count; _metrics.Add(schema, id); Schema.Metrics.Add(schema); }
            return id;
        }
        internal RendererPerfMetric Decode(int id, double? value)
        {
            var schema = Get(id);
            long? bits = value is { } number ? BitConverter.DoubleToInt64Bits(number) : null;
            if (!_scalars.TryGetValue((id, bits), out var result))
            {
                result = new(value, schema.Unit, schema.Source, schema.Status, schema.Reason);
                RendererPerfReportJson.Metric(result);
                _scalars.Add((id, bits), result);
            }
            return result;
        }
        internal Vector Encode(ImmutableDictionary<string, RendererPerfMetric> metrics)
        {
            if (_encoded.TryGetValue(metrics, out var vector)) return vector;
            var ids = new List<int>(metrics.Count);
            var values = ImmutableArray.CreateBuilder<double>();
            var zeros = ImmutableArray.CreateBuilder<int>();
            int run = 0;
            foreach (var pair in metrics.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!RendererPerfManifest.Text(pair.Key)) throw new RendererPerfProtocolException("Metric", "Missing metric name.");
                ids.Add(Register(pair.Key, pair.Value));
                if (pair.Value.Status == RendererPerfMetricStatus.Available)
                {
                    double number = pair.Value.Value!.Value;
                    if (BitConverter.DoubleToInt64Bits(number) == 0) run++;
                    else { zeros.Add(run); run = 0; values.Add(number); }
                }
            }
            string key = string.Join(',', ids);
            if (!_layouts.TryGetValue(key, out int layout))
            { layout = Schema.Layouts.Count; _layouts.Add(key, layout); Schema.Layouts.Add([.. ids]); }
            zeros.Add(run);
            string pattern = string.Join(',', zeros);
            if (!_zeroRuns.TryGetValue(pattern, out int zeroRuns))
            { zeroRuns = Schema.ZeroRuns.Count; _zeroRuns.Add(pattern, zeroRuns); Schema.ZeroRuns.Add([.. zeros]); }
            vector = new(layout, values.ToImmutable(), zeroRuns);
            _encoded.Add(metrics, vector);
            return vector;
        }
        internal ImmutableDictionary<string, RendererPerfMetric> Decode(Vector vector)
        {
            if ((uint)vector.Layout >= (uint)Schema.Layouts.Count) throw new JsonException("Metric layout reference is outside the table.");
            if ((uint)vector.ZeroRuns >= (uint)Schema.ZeroRuns.Count) throw new JsonException("Zero-run reference is outside the table.");
            int[] layout = Schema.Layouts[vector.Layout];
            int[] runs = Schema.ZeroRuns[vector.ZeroRuns];
            long count = vector.Values.Length;
            if (runs.Length != vector.Values.Length + 1
                || vector.Values.Any(n => !double.IsFinite(n) || BitConverter.DoubleToInt64Bits(n) == 0))
                throw new JsonException("Invalid zero-run metric vector.");
            foreach (int run in runs) count += run;
            if (count != layout.Count(id => Get(id).Status == RendererPerfMetricStatus.Available))
                throw new JsonException("A metric vector has missing or extra values.");
            if (_decoded.TryGetValue(vector, out var cached)) return cached;
            int offset = 0, zeros = runs[0];
            var metrics = ImmutableDictionary.CreateBuilder<string, RendererPerfMetric>(StringComparer.Ordinal);
            foreach (int id in layout)
            {
                var schema = Get(id);
                double? value = null;
                if (schema.Status == RendererPerfMetricStatus.Available)
                {
                    if (zeros > 0) { value = 0; zeros--; }
                    else { value = vector.Values[offset++]; zeros = runs[offset]; }
                }
                metrics.Add(schema.Name!, Decode(id, value));
            }
            if (offset != vector.Values.Length) throw new JsonException("A metric vector has extra values.");
            var result = metrics.ToImmutable();
            _decoded.Add(vector, result);
            return result;
        }
        static int Intern<T>(T key, List<T> values, Dictionary<T, int> indices) where T : notnull
        {
            if (!indices.TryGetValue(key, out int id))
            { id = values.Count; values.Add(key); indices.Add(key, id); }
            return id;
        }
        sealed class PassConverter(Tables tables) : JsonConverter<RendererPerfPassSample>
        {
            public override RendererPerfPassSample Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                if (reader.TokenType != JsonTokenType.StartArray || !reader.Read() || !reader.TryGetInt32(out int id)
                    || !reader.Read()) throw new JsonException("Invalid pass tuple.");
                if (reader.TokenType != JsonTokenType.StartArray || !reader.Read()) throw new JsonException("Invalid optional pass ticks.");
                ulong? ticks = null;
                if (reader.TokenType != JsonTokenType.EndArray)
                {
                    if (!reader.TryGetUInt64(out ulong number)) throw new JsonException("Invalid pass ticks.");
                    ticks = number;
                    if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Extra pass ticks.");
                }
                if (!reader.Read() || !reader.TryGetInt32(out int durationId) || !reader.Read()) throw new JsonException("Invalid pass duration.");
                if (reader.TokenType != JsonTokenType.StartArray || !reader.Read()) throw new JsonException("Invalid optional pass duration.");
                double? duration = null;
                if (reader.TokenType != JsonTokenType.EndArray)
                {
                    if (!reader.TryGetDouble(out double number)) throw new JsonException("Invalid pass duration value.");
                    duration = number;
                    if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Extra pass duration values.");
                }
                if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray || tables.Get(durationId).Name is not null)
                    throw new JsonException("A pass requires one scalar duration and exactly four tuple entries.");
                var metric = tables.Decode(durationId, duration);
                var key = new PassKey(id, ticks, durationId, duration is { } scalar ? BitConverter.DoubleToInt64Bits(scalar) : null);
                if ((uint)id >= (uint)tables.Schema.Passes.Count) throw new JsonException("Foreign pass schema.");
                if (tables._decodedPasses.TryGetValue(key, out var cached)) return cached;
                var schema = tables.Schema.Passes[id];
                var result = new RendererPerfPassSample(schema.Id, schema.Occurrence, schema.ParentId, schema.ParentOccurrence,
                    schema.BeginMarker, schema.EndMarker, ticks, metric, schema.DeviceOnly);
                tables._decodedPasses.Add(key, result);
                return result;
            }
            public override void Write(Utf8JsonWriter writer, RendererPerfPassSample value, JsonSerializerOptions options)
            {
                var schema = new PassSchema(value.Id, value.Occurrence, value.ParentId, value.ParentOccurrence,
                    value.BeginMarker, value.EndMarker, value.DeviceOnly);
                int id = Intern(schema, tables.Schema.Passes, tables._passes);
                writer.WriteStartArray(); writer.WriteNumberValue(id);
                writer.WriteStartArray();
                if (value.ElapsedTicks is { } ticks) writer.WriteNumberValue(ticks);
                writer.WriteEndArray();
                writer.WriteNumberValue(tables.Register(null, value.Duration));
                writer.WriteStartArray();
                if (value.Duration.Value is { } number) writer.WriteNumberValue(number);
                writer.WriteEndArray();
                writer.WriteEndArray();
            }
        }
        sealed class CpuIntervalsConverter(Tables tables) : JsonConverter<ImmutableArray<RendererPerfCpuSpan>>
        {
            public override ImmutableArray<RendererPerfCpuSpan> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                var columns = JsonSerializer.Deserialize<CpuIntervals>(ref reader, RendererPerfManifest.JsonOptions)
                    ?? throw new JsonException("Null CPU intervals.");
                if (columns.Schemas.Length != columns.StartOffsets.Length || columns.Schemas.Length != columns.Durations.Length)
                    throw new JsonException("CPU interval column lengths differ.");
                var spans = ImmutableArray.CreateBuilder<RendererPerfCpuSpan>(columns.Schemas.Length);
                for (int i = 0; i < columns.Schemas.Length; i++)
                {
                    if ((uint)columns.Schemas[i] >= (uint)tables.Schema.CpuSpans.Count) throw new JsonException("Foreign CPU schema.");
                    var schema = tables.Schema.CpuSpans[columns.Schemas[i]];
                    Int128 start = (Int128)columns.OriginTicks + columns.StartOffsets[i];
                    Int128 end = start + columns.Durations[i];
                    if (start > long.MaxValue || end > long.MaxValue) throw new JsonException("CPU interval ticks overflow Int64.");
                    spans.Add(new(schema.Id, schema.ParentId, (long)start, (long)end));
                }
                return spans.MoveToImmutable();
            }
            public override void Write(Utf8JsonWriter writer, ImmutableArray<RendererPerfCpuSpan> spans, JsonSerializerOptions options)
            {
                long origin = spans.IsEmpty ? 0 : spans.Min(s => s.StartTicks);
                int[] schemas = new int[spans.Length];
                ulong[] starts = new ulong[spans.Length], durations = new ulong[spans.Length];
                for (int i = 0; i < spans.Length; i++)
                {
                    var span = spans[i];
                    if (span.EndTicks < span.StartTicks) throw new JsonException("CPU interval ends before it starts.");
                    schemas[i] = Intern(new CpuSchema(span.Id, span.ParentId), tables.Schema.CpuSpans, tables._cpuSpans);
                    starts[i] = (ulong)((Int128)span.StartTicks - origin);
                    durations[i] = (ulong)((Int128)span.EndTicks - span.StartTicks);
                }
                JsonSerializer.Serialize(writer, new CpuIntervals(origin, schemas, starts, durations), RendererPerfManifest.JsonOptions);
            }
        }
        sealed class CpuConverter(Tables tables) : JsonConverter<RendererPerfCpuSpan>
        {
            public override RendererPerfCpuSpan Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                var value = JsonSerializer.Deserialize<CpuValue>(ref reader, RendererPerfManifest.JsonOptions) ?? throw new JsonException("Null CPU span.");
                if ((uint)value.Schema >= (uint)tables.Schema.CpuSpans.Count) throw new JsonException("Foreign CPU schema.");
                var schema = tables.Schema.CpuSpans[value.Schema];
                return new(schema.Id, schema.ParentId, value.StartTicks, value.EndTicks);
            }
            public override void Write(Utf8JsonWriter writer, RendererPerfCpuSpan value, JsonSerializerOptions options)
            {
                int id = Intern(new CpuSchema(value.Id, value.ParentId), tables.Schema.CpuSpans, tables._cpuSpans);
                JsonSerializer.Serialize(writer, new CpuValue(id, value.StartTicks, value.EndTicks), RendererPerfManifest.JsonOptions);
            }
        }
        sealed class MetricConverter(Tables tables) : JsonConverter<RendererPerfMetric>
        {
            public override bool HandleNull => true;
            public override RendererPerfMetric Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            {
                var scalar = JsonSerializer.Deserialize<Scalar>(ref reader, RendererPerfManifest.JsonOptions) ?? throw new JsonException("Null metric.");
                if (tables.Get(scalar.Schema).Name is not null) throw new JsonException("A scalar cannot use a named vector metric.");
                return tables.Decode(scalar.Schema, scalar.Value);
            }
            public override void Write(Utf8JsonWriter writer, RendererPerfMetric value, JsonSerializerOptions options)
                => JsonSerializer.Serialize(writer, new Scalar(tables.Register(null, value), value.Value), RendererPerfManifest.JsonOptions);
        }
        sealed class MapConverter(Tables tables) : JsonConverter<ImmutableDictionary<string, RendererPerfMetric>>
        {
            public override bool HandleNull => true;
            public override ImmutableDictionary<string, RendererPerfMetric> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
                => tables.Decode(JsonSerializer.Deserialize<Vector>(ref reader, RendererPerfManifest.JsonOptions) ?? throw new JsonException("Null vector."));
            public override void Write(Utf8JsonWriter writer, ImmutableDictionary<string, RendererPerfMetric> value, JsonSerializerOptions options)
                => JsonSerializer.Serialize(writer, tables.Encode(value), RendererPerfManifest.JsonOptions);
        }
    }
    sealed class VectorComparer : IEqualityComparer<Vector>
    {
        public bool Equals(Vector? x, Vector? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.Layout != y.Layout || x.ZeroRuns != y.ZeroRuns || x.Values.Length != y.Values.Length) return false;
            for (int i = 0; i < x.Values.Length; i++)
                if (BitConverter.DoubleToInt64Bits(x.Values[i]) != BitConverter.DoubleToInt64Bits(y.Values[i])) return false;
            return true;
        }
        public int GetHashCode(Vector value)
        {
            var hash = new HashCode(); hash.Add(value.Layout);
            foreach (double item in value.Values) hash.Add(BitConverter.DoubleToInt64Bits(item));
            hash.Add(value.ZeroRuns);
            return hash.ToHashCode();
        }
    }
    sealed class BoundedStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > maximum) throw new JsonException("Document exceeds its bounded byte capacity.");
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > maximum) throw new JsonException("Document exceeds its bounded byte capacity.");
            base.Write(buffer.ToArray(), 0, buffer.Length);
        }
    }
}
