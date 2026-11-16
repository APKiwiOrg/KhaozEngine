using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Lazy public snapshots and direct logical writing over ordinal dense-pose access.</summary>
internal static class CoverageEvidenceProjection
{
    internal static IReadOnlyList<CoverageDomainEvidence> Domains(CoverageCampaign input) => ImmutableArray.CreateRange(
        input.Domains.Select((domain, index) => new CoverageDomainEvidence(domain.Id, domain.Kind, domain.AuthoredFrameIds,
            domain.BlendPathIds, new DenseRows(input, index))).ToArray());

    sealed class DenseRows(CoverageCampaign input, int domainIndex) : IReadOnlyList<CoverageDenseRowEvidence>
    {
        public int Count => input.Domains[domainIndex].DenseRows.Count;
        // The public projection is a copy the caller owns, so the decoded row is released before it is returned.
        public CoverageDenseRowEvidence this[int index]
        {
            get { using var row = input.ReadDenseRow(domainIndex, index); return DenseEvidence(row.Value); }
        }
        public IEnumerator<CoverageDenseRowEvidence> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    static CoverageDenseRowEvidence DenseEvidence(CoverageDenseRow row)
    {
        var pose = row.Sample;
        var palette = new float[pose.Palette.Length][];
        for (int i = 0; i < palette.Length; i++) palette[i] = CoverageBuildInputJson.Values(pose.Palette[i]);
        var commands = new CoverageCommandPoseEvidence[pose.Commands.Length];
        for (int i = 0; i < commands.Length; i++)
        {
            var c = pose.Commands[i];
            commands[i] = new(c.Id, c.EquipmentInstanceId, CoverageBuildInputJson.Values(c.EquipmentWorld),
                CoverageBuildInputJson.Values(c.CoverageWorld), c.TransitionWeight);
        }
        return new(row.DenseIndex, row.PrecedingPhaseTick, row.ResolvedStateKey,
            new(pose.Id, pose.DomainId, pose.AuthoredFrameId, pose.BlendPathId, CoverageBuildInputJson.Values(pose.ActorModel),
                Array.AsReadOnly(palette), Array.AsReadOnly(commands)), row.Looped, row.Clamped);
    }

    internal static void WriteTo(Utf8JsonWriter writer, CoverageEvidenceManifest evidence, CoverageCampaign input, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        Property("formatVersion", evidence.FormatVersion); Property("tool", evidence.Tool); Property("scope", evidence.Scope);
        Property("identity", evidence.Identity); Property("inputSha256", evidence.InputSha256); Property("tableSha256", evidence.TableSha256);
        Property("toolCodeSha256", evidence.ToolCodeSha256); Property("render3DCodeSha256", evidence.Render3DCodeSha256);
        writer.WriteStartArray("domains");
        for (int d = 0; d < input.Domains.Count; d++)
        {
            var domain = input.Domains[d];
            writer.WriteStartObject(); Property("id", domain.Id); Property("kind", domain.Kind);
            Property("authoredFrameIds", domain.AuthoredFrameIds); Property("blendPathIds", domain.BlendPathIds);
            writer.WriteStartArray("denseRows");
            for (int r = 0; r < domain.DenseRows.Count; r++) WriteDenseRow(writer, input, d, r);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        Collection("samples", evidence.Samples); Collection("candidateSampleIds", evidence.CandidateSampleIds);
        Collection("candidates", evidence.Candidates); Collection("entries", evidence.Entries); Collection("pairResults", evidence.PairResults);
        Collection("selectedClaims", evidence.SelectedClaims); Collection("holes", evidence.Holes);
        writer.WritePropertyName("replay");
        if (evidence.Replay == null) writer.WriteNullValue();
        else evidence.Replay.WriteTo(writer, options);
        Property("complete", evidence.Complete);
        writer.WriteEndObject(); writer.Flush();

        void Property<T>(string name, T value)
        {
            writer.WritePropertyName(name); JsonSerializer.Serialize(writer, value, options);
            writer.Flush();
        }
        void Collection<T>(string name, IEnumerable<T> values)
        {
            writer.WriteStartArray(name);
            foreach (var value in values) { JsonSerializer.Serialize(writer, value, options); writer.Flush(); }
            writer.WriteEndArray();
        }
    }

    // Each decoded row is released before the next read. No numeric evidence copy is needed.
    static void WriteDenseRow(Utf8JsonWriter writer, CoverageCampaign input, int domainIndex, int rowIndex)
    {
        using var lease = input.ReadDenseRow(domainIndex, rowIndex);
        var row = lease.Value;
        var pose = row.Sample;
        writer.WriteStartObject();
        writer.WriteNumber("denseIndex", row.DenseIndex); writer.WriteNumber("precedingPhaseTick", row.PrecedingPhaseTick);
        writer.WriteNumber("resolvedStateKey", row.ResolvedStateKey);
        writer.WriteStartObject("sample");
        writer.WriteString("id", pose.Id); writer.WriteString("domainId", pose.DomainId);
        writer.WriteString("authoredFrameId", pose.AuthoredFrameId); writer.WriteString("blendPathId", pose.BlendPathId);
        writer.WritePropertyName("actorModel"); Matrix(writer, pose.ActorModel);
        writer.WriteStartArray("palette");
        foreach (var matrix in pose.Palette) Matrix(writer, matrix);
        writer.WriteEndArray(); writer.WriteStartArray("commands");
        foreach (var command in pose.Commands)
        {
            writer.WriteStartObject(); writer.WriteNumber("id", command.Id); writer.WriteNumber("equipmentInstanceId", command.EquipmentInstanceId);
            writer.WritePropertyName("equipmentWorld"); Matrix(writer, command.EquipmentWorld);
            writer.WritePropertyName("coverageWorld"); Matrix(writer, command.CoverageWorld);
            writer.WriteNumber("transitionWeight", command.TransitionWeight); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
        writer.WriteBoolean("looped", row.Looped); writer.WriteBoolean("clamped", row.Clamped); writer.WriteEndObject(); writer.Flush();
    }

    static void Matrix(Utf8JsonWriter writer, Matrix4x4 matrix)
    {
        writer.WriteStartArray();
        foreach (float value in CoverageBuildInputJson.Values(matrix)) writer.WriteNumberValue(value);
        writer.WriteEndArray(); writer.Flush();
    }
}
