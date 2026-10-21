using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace KhaozEngine.Tests.Gpu;

internal static class CoveragePerfReportJson
{
    static readonly JsonSerializerOptions Options = CreateOptions();
    internal static string Serialize(CoveragePerfReport report) => JsonSerializer.Serialize(new Document(
        CoveragePerfReport.Schema, report, CoveragePerfBudgets.Evaluate(report), report.Blocks.Select(b =>
            CoveragePerfStatistics.Summarize(b, report.Identity.Execution.ClockFrequency)).ToImmutableArray()), Options);

    internal static CoveragePerfReport Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<Document>(json, Options) ?? throw new InvalidDataException("Missing report.");
        if (document.Schema != CoveragePerfReport.Schema) throw new InvalidDataException("Unsupported coverage performance report schema.");
        return document.Report;
    }

    internal static string Markdown(CoveragePerfReport report)
    {
        var verdict = CoveragePerfBudgets.Evaluate(report);
        var text = new StringBuilder();
        text.AppendLine($"Coverage performance {report.Manifest?.Preset ?? "unknown"}, {report.Identity.RunId}");
        text.AppendLine();
        var run = report.Identity;
        text.AppendLine(FormattableString.Invariant($"Workload {run.WorkloadSha}, authorization commit {run.AuthorizationCommitSha}, workflow {run.WorkflowSha}, run {run.WorkflowRunId} attempt {run.WorkflowRunAttempt}, process {run.ProcessId}."));
        text.AppendLine();
        text.AppendLine($"CPU {verdict.Cpu.Status}, both reversed C1 deltas at most 2 ms.");
        text.AppendLine($"GPU {verdict.Gpu.Status}, both reversed Total deltas at most 15 percent on {report.Manifest?.Backend}.");
        foreach (var pair in verdict.Cpu.Pairs.Concat(verdict.Gpu.Pairs).Concat(verdict.SharedSubmission))
            text.AppendLine(FormattableString.Invariant($"- {pair.Name}, blocks {pair.OffBlock}/{pair.OnBlock}: {Number(pair.Microseconds)} us, {Number(pair.Percent)} percent, {pair.Status}."));
        if (verdict.Cpu.PooledDeltaMilliseconds.HasValue)
            text.AppendLine($"Pooled CPU delta {Number(verdict.Cpu.PooledDeltaMilliseconds)} ms, order difference {Number(verdict.Cpu.OrderDifferenceMilliseconds)} ms.");
        if (verdict.OneActor is { } one) text.AppendLine($"One actor observed delta {Number(one.Microseconds)} us per actor. No extrapolation or gate.");
        text.AppendLine();
        text.AppendLine("Shared submission");
        text.AppendLine();
        text.AppendLine(verdict.SharedSubmissionAcceptance);
        foreach (var observation in verdict.SharedSubmissionObservations)
        {
            SubmissionCounts(observation, "warm-up", observation.Warmup);
            SubmissionCounts(observation, "measured", observation.Measured);
        }
        text.AppendLine();
        text.AppendLine("Warm-up frames may carry cold-setup submissions and completion fences outside every gate. Measured frames require one frame submission and the control fence schedule.");
        foreach (var block in verdict.WarmupSubmissionDeviations) WarmupDeviations(block);
        text.AppendLine();
        foreach (string disclosure in verdict.SharedSubmissionDisclosure) text.AppendLine("- " + disclosure);
        text.AppendLine();
        text.AppendLine("Frame wall");
        text.AppendLine();
        string limit = report.Manifest?.MaxFrameMilliseconds.ToString(CultureInfo.InvariantCulture) ?? "unresolved";
        text.AppendLine($"Wall spans are outside the aggregate and never enter cpu.framePreparation or a gate. The {limit} ms frame limit applies to measured frames only. Warm-up frames are bounded by the cooperative unit deadline.");
        long frequency = report.Identity.Execution.ClockFrequency;
        foreach (var block in report.Blocks)
        {
            Wall(block, "warm-up", block.Warmup, frequency);
            Wall(block, "measured", block.Samples, frequency);
        }
        text.AppendLine();
        foreach (var block in report.Blocks)
            if (block.Session is { } info)
                text.AppendLine($"Block {block.Block.Index} adapter {info.Adapter}, vendor {info.Vendor}, device {info.Device}, hardware identity {info.HardwareIdentityBasis}.");
        text.AppendLine($"Allocation {verdict.AllocationProof.Status}, native proof {verdict.AllocationProof.NativeProofSha}, reconciled {verdict.AllocationProof.Reconciled}. Timing observations are informational only.");
        var reasons = verdict.Cpu.Reasons.Concat(verdict.Gpu.Reasons)
            .Concat(verdict.SharedSubmission.SelectMany(p => p.Reasons)).Concat(verdict.OneActor?.Reasons ?? []).Distinct();
        foreach (string reason in reasons) text.AppendLine("- Unproved: " + reason.Replace('\r', ' ').Replace('\n', ' '));
        return text.ToString();

        // The spans printed beside the maximum wall all belong to that one frame, never to independent maxima.
        void Wall(CoveragePerfBlockResult block, string phase, ImmutableArray<CoveragePerfFrameSample> samples, long frequency)
        {
            var wall = CoveragePerfStatistics.Wall(samples, frequency);
            string line = FormattableString.Invariant($"- Block {block.Block.Index} {block.Block.Condition} {phase} wall, {wall.Frames} frames: ");
            if (wall.MaxFrameWallFrame is not int frame || wall.FrameWall is not { } frameWall)
            {
                text.AppendLine(line + "no frame wall recorded.");
                return;
            }
            var worst = samples.First(s => s.FrameIndex == frame);
            text.AppendLine(line + FormattableString.Invariant($"maximum frame wall {Number(frameWall.Maximum)} ms at frame {frame}. That frame spent {Spent(worst.FrameEntryToAggregateTicks)} from frame entry to aggregate, {Spent(worst.PresentToHostReturnTicks)} from Present entry to host return and {Spent(worst.HostReturnToFrameEndTicks)} from host return to frame end."));

            string Spent(long? ticks) => ticks is >= 0 && frequency > 0 ? Number(ticks.Value * 1000d / frequency) + " ms" : "no recorded time";
        }

        void SubmissionCounts(CoveragePerfSharedSubmissionObservation observation, string phase, CoveragePerfSubmissionSummary counts) =>
            text.AppendLine($"- Block {observation.Block} {observation.Condition} {phase}, {counts.Frames} frames: {counts.Submissions} submissions, {counts.SubmittedCompletionFences} completion fences, {counts.SubmissionParticipants} participants, {counts.CompletionConsumers} consumers, {counts.ProductionWaits} production waits.");

        void WarmupDeviations(CoveragePerfWarmupSubmissionDeviations block)
        {
            string line = FormattableString.Invariant($"- Block {block.Block} {block.Condition} warm-up cold setup outside every gate");
            if (block.SteadySubmissions is not int submissions || block.SteadyCompletionFences is not int fences)
                text.AppendLine(line + ": the measured steady schedule is unresolved, so no warm-up deviation is listed.");
            else if (block.Frames.IsDefaultOrEmpty)
                text.AppendLine(line + FormattableString.Invariant($": none. Every warm-up frame matches the measured steady schedule of {submissions} submissions and {fences} completion fences."));
            else
                text.AppendLine(line + FormattableString.Invariant($", measured steady schedule {submissions} submissions and {fences} completion fences: ")
                    + string.Join(", ", block.Frames.Select(f => FormattableString.Invariant(
                        $"frame {f.FrameIndex} with {f.Submissions} submissions and {f.SubmittedCompletionFences} completion fences"))) + ".");
        }
    }

    internal static void WriteReturnedReport(CoveragePerfReport report, string jsonPath, string markdownPath)
    {
        if (!report.ReturnedByRun) throw new InvalidOperationException("Durable publication requires Run's returned report, never its publication candidate.");
        File.WriteAllText(jsonPath, Serialize(report), new UTF8Encoding(false));
        File.WriteAllText(markdownPath, Markdown(report), new UTF8Encoding(false));
    }

    static string Number(double? value) => value?.ToString("0.#########", CultureInfo.InvariantCulture) ?? "unresolved";

    static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Kind != JsonTypeInfoKind.Object || info.Type.Namespace != typeof(CoveragePerfReport).Namespace ||
                !info.Type.Name.StartsWith("CoveragePerf", StringComparison.Ordinal)) return;
            // The harness contract is internal to the test assembly. Include its explicit internal data properties,
            // without exposing generated record equality members or private implementation fields.
            foreach (var property in info.Type.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (property.GetMethod?.IsAssembly != true) continue;
                var json = info.CreateJsonPropertyInfo(property.PropertyType, JsonNamingPolicy.CamelCase.ConvertName(property.Name));
                json.Get = property.GetValue;
                if (property.SetMethod is not null) json.Set = property.SetValue;
                info.Properties.Add(json);
            }
            if (info.CreateObject is null && info.Type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null) is not null) info.CreateObject = () => Activator.CreateInstance(info.Type, true)!;
        });
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            TypeInfoResolver = resolver,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new ManifestConverter());
        options.Converters.Add(new PartialArrayConverterFactory());
        return options;
    }

    sealed record Document(string Schema, CoveragePerfReport Report, CoveragePerfVerdict Verdict,
        ImmutableArray<CoveragePerfBlockSummary> Summaries);

    // An exception may leave a raw sample's array uninitialized. Preserve that absence as null rather than
    // throwing during publication or turning it into evidence that an empty result completed successfully.
    sealed class PartialArrayConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(PartialArrayConverter<>).MakeGenericType(type.GetGenericArguments()), true)!;
    }

    sealed class PartialArrayConverter<T> : JsonConverter<ImmutableArray<T>>
    {
        public override ImmutableArray<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? default : ImmutableArray.CreateRange(JsonSerializer.Deserialize<T[]>(ref reader, options)!);
        public override void Write(Utf8JsonWriter writer, ImmutableArray<T> value, JsonSerializerOptions options)
        {
            if (value.IsDefault) { writer.WriteNullValue(); return; }
            writer.WriteStartArray();
            foreach (T item in value) JsonSerializer.Serialize(writer, item, options);
            writer.WriteEndArray();
        }
    }

    sealed class ManifestConverter : JsonConverter<CoveragePerfManifest>
    {
        public override CoveragePerfManifest Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return CoveragePerfManifest.Parse(document.RootElement.GetProperty("utf8Base64").GetBytesFromBase64());
        }
        public override void Write(Utf8JsonWriter writer, CoveragePerfManifest value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteBase64String("utf8Base64", value.Utf8.AsSpan());
            writer.WritePropertyName("document");
            writer.WriteRawValue(value.Utf8.AsSpan());
            writer.WriteEndObject();
        }
    }
}
