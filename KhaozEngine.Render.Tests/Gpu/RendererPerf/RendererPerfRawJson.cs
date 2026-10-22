using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        List<PassSchema> Passes, List<CpuSchema> CpuSpans);
    sealed record PassValue(int Schema, ulong? ElapsedTicks, RendererPerfMetric Duration);
    sealed record CpuValue(int Schema, long StartTicks, long EndTicks);
    sealed record Scalar(int Schema, double? Value);
    sealed record Vector(int Layout, ImmutableArray<double> Values, ImmutableArray<int> ZeroRuns);
    sealed record WireReport(string Schema, RendererPerfManifest Manifest, RendererPerfRunIdentity Identity,
        ImmutableArray<RendererPerfRecipeReport> Recipes, ImmutableArray<RendererPerfBlockReport> Blocks,
        RendererPerfMemoryReport Memory, ImmutableArray<GpuPipelineCompilerReport> Pipelines,
        ImmutableArray<RendererPerfControlReport> Controls, RendererPerfAssessment Assessment,
        RendererPerfPublication Publication, ImmutableArray<RendererPerfSidecar> Sidecars,
        ImmutableArray<RendererPerfFrameSample> Conditioning, ImmutableArray<RendererPerfBatchReport> Batches,
        ImmutableArray<RendererPerfStartupReport> Startup, [property: JsonPropertyOrder(100)] Schema Raw)
    {
        internal RendererPerfReport Report() => new(Schema, Manifest, Identity, Recipes, Blocks, Memory,
            Pipelines, Controls, Assessment, Publication, Sidecars)
        { Conditioning = Conditioning, Batches = Batches, Startup = Startup };
    }

    internal static byte[] Serialize(RendererPerfReport report, int maximumBytes)
    {
        var tables = new Tables(new("metric-vectors/1", [], [], [], []));
        var wire = new WireReport(report.Schema, report.Manifest, report.Identity, report.Recipes, report.Blocks,
            report.Memory, report.Pipelines, report.Controls, report.Assessment, report.Publication, report.Sidecars,
            report.Conditioning, report.Batches, report.Startup, tables.Schema);
        using var stream = new BoundedStream(maximumBytes);
        JsonSerializer.Serialize(stream, wire, tables.Options);
        return stream.ToArray();
    }

    internal static RendererPerfReport Parse(ReadOnlySpan<byte> bytes, int maximumBytes)
    {
        try
        {
            using JsonDocument document = RendererPerfManifest.ReadDocument(bytes, maximumBytes);
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
        readonly Dictionary<ImmutableDictionary<string, RendererPerfMetric>, Vector> _encoded = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<Vector, ImmutableDictionary<string, RendererPerfMetric>> _decoded = new(new VectorComparer());
        readonly Dictionary<(int, long?), RendererPerfMetric> _scalars = [];
        readonly Dictionary<PassSchema, int> _passes = [];
        readonly Dictionary<CpuSchema, int> _cpuSpans = [];
        readonly Dictionary<PassValue, RendererPerfPassSample> _decodedPasses = [];
        internal Tables(Schema schema)
        {
            Schema = schema;
            Options = new(RendererPerfManifest.JsonOptions);
            Options.Converters.Add(new MetricConverter(this));
            Options.Converters.Add(new MapConverter(this));
            Options.Converters.Add(new PassConverter(this));
            Options.Converters.Add(new CpuConverter(this));
        }
        internal void Validate()
        {
            if (Schema.Format != "metric-vectors/1") throw new JsonException("Unknown raw metric format.");
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
            vector = new(layout, values.ToImmutable(), zeros.ToImmutable());
            _encoded.Add(metrics, vector);
            return vector;
        }
        internal ImmutableDictionary<string, RendererPerfMetric> Decode(Vector vector)
        {
            if ((uint)vector.Layout >= (uint)Schema.Layouts.Count) throw new JsonException("Metric layout reference is outside the table.");
            if (_decoded.TryGetValue(vector, out var cached)) return cached;
            int[] layout = Schema.Layouts[vector.Layout];
            long count = vector.Values.Length;
            if (vector.ZeroRuns.Length != vector.Values.Length + 1 || vector.ZeroRuns.Any(n => n < 0)
                || vector.Values.Any(n => BitConverter.DoubleToInt64Bits(n) == 0))
                throw new JsonException("Invalid zero-run metric vector.");
            foreach (int run in vector.ZeroRuns) count += run;
            if (count != layout.Count(id => Get(id).Status == RendererPerfMetricStatus.Available))
                throw new JsonException("A metric vector has missing or extra values.");
            int offset = 0, zeros = vector.ZeroRuns[0];
            var metrics = ImmutableDictionary.CreateBuilder<string, RendererPerfMetric>(StringComparer.Ordinal);
            foreach (int id in layout)
            {
                var schema = Get(id);
                double? value = null;
                if (schema.Status == RendererPerfMetricStatus.Available)
                {
                    if (zeros > 0) { value = 0; zeros--; }
                    else { value = vector.Values[offset++]; zeros = vector.ZeroRuns[offset]; }
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
                var value = JsonSerializer.Deserialize<PassValue>(ref reader, options) ?? throw new JsonException("Null pass.");
                if ((uint)value.Schema >= (uint)tables.Schema.Passes.Count) throw new JsonException("Foreign pass schema.");
                if (tables._decodedPasses.TryGetValue(value, out var cached)) return cached;
                var schema = tables.Schema.Passes[value.Schema];
                var result = new RendererPerfPassSample(schema.Id, schema.Occurrence, schema.ParentId, schema.ParentOccurrence,
                    schema.BeginMarker, schema.EndMarker, value.ElapsedTicks, value.Duration, schema.DeviceOnly);
                tables._decodedPasses.Add(value, result);
                return result;
            }
            public override void Write(Utf8JsonWriter writer, RendererPerfPassSample value, JsonSerializerOptions options)
            {
                var schema = new PassSchema(value.Id, value.Occurrence, value.ParentId, value.ParentOccurrence,
                    value.BeginMarker, value.EndMarker, value.DeviceOnly);
                int id = Intern(schema, tables.Schema.Passes, tables._passes);
                JsonSerializer.Serialize(writer, new PassValue(id, value.ElapsedTicks, value.Duration), options);
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
        public bool Equals(Vector? x, Vector? y) => ReferenceEquals(x, y) || x is not null && y is not null
            && x.Layout == y.Layout && x.Values.AsSpan().SequenceEqual(y.Values.AsSpan())
            && x.ZeroRuns.AsSpan().SequenceEqual(y.ZeroRuns.AsSpan());
        public int GetHashCode(Vector value)
        {
            var hash = new HashCode(); hash.Add(value.Layout);
            foreach (double item in value.Values) hash.Add(item);
            foreach (int run in value.ZeroRuns) hash.Add(run);
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
