using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageTableBuilderTests
{
    [Fact]
    public void Direction_cancellation_does_not_create_sample_holes_or_change_exact_scalar_rows()
    {
        var input = CoverageBuildFixture.Input();
        var baseline = CoverageTableBuilder.Build(input);
        var original = input.Topology.Source;
        var skin = original.SkinVertices.ToArray();
        for (int i = 0; i < skin.Length; i++) skin[i].Normal = i % 2 == 0 ? Vector3.UnitY : -Vector3.UnitY;
        var source = new CoverageSourceTopology("cancelling-directions", original.SourceSha256, original.Positions,
            original.Vertices, original.Triangles, skin);
        var topology = CoverageRefinement.Build(source, input.Topology.Map);
        var samples = input.Samples.Select(s => new CoverageSample(s.Id, s.DomainId, s.AuthoredFrameId, s.BlendPathId,
            s.StateKey, s.ActorModel, s.Palette, s.Commands.ToArray().Select(c => c with { Topology = topology }).ToArray()));
        var changed = new CoverageBuildInput(topology, input.InverseBind, input.Domains, samples,
            input.CandidateSampleIds, input.Identity, input.InputSha256);
        var result = CoverageTableBuilder.Build(changed);
        Assert.True(result.Evidence.Complete);
        Assert.Empty(result.Evidence.Holes);
        Assert.Equal(4, result.Evidence.PairResults.Count);
        Assert.Equal(baseline.Evidence.PairResults.Select(p => p.Valid), result.Evidence.PairResults.Select(p => p.Valid));
        Assert.Equal(baseline.Table.EntryCount, result.Table.EntryCount);
        for (int entry = 0; entry < result.Table.EntryCount; entry++)
            Assert.Equal(baseline.Table.Entry(entry).ToArray(), result.Table.Entry(entry).ToArray());
    }

    [Fact]
    public void Configuration_identity_ignores_serialization_and_asset_locations()
    {
        using var files = new CoverageBuildFiles("task8");
        string before = CoverageBuildInputJson.Read(files.ManifestPath).Identity.ConfigurationSha256;
        var attachment = files.Configuration["attachment"]!.DeepClone();
        var companion = files.Configuration["companion"]!.DeepClone();
        var parts = files.Configuration["parts"]!.DeepClone();
        files.Configuration.Clear();
        files.Configuration["parts"] = parts;
        files.Configuration["companion"] = companion;
        files.Configuration["attachment"] = attachment;
        files.Configuration["formatVersion"] = 1;
        byte[] reformatted = System.Text.Encoding.UTF8.GetBytes(files.Configuration.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        files.Manifest["configuration"] = files.Save("moved-configuration.json", reformatted);
        files.Write();
        Assert.Equal(before, CoverageBuildInputJson.Read(files.ManifestPath).Identity.ConfigurationSha256);
        string assetPath = attachment!["path"]!.GetValue<string>();
        byte[] asset = System.IO.File.ReadAllBytes(System.IO.Path.Combine(files.Folder, assetPath));
        files.Configuration["attachment"] = files.Save("moved-attachment.json", asset);
        files.WriteConfiguration();
        Assert.Equal(before, CoverageBuildInputJson.Read(files.ManifestPath).Identity.ConfigurationSha256);
    }

    [Fact]
    public void Every_entry_sample_pair_is_checked_and_only_valid_pairs_are_selected()
    {
        var input = CoverageBuildFixture.Input();
        var first = CoverageTableBuilder.Build(input);
        var second = CoverageTableBuilder.Build(input);
        var pairs = first.Evidence.PairResults;
        Assert.Equal(4, pairs.Count);
        Assert.Equal(2, pairs.Count(p => p.Valid));
        Assert.Equal(2, pairs.Count(p => !p.Valid));
        foreach (var candidate in first.Evidence.Candidates)
        {
            int entry = Enumerable.Range(0, first.Table.EntryCount).Single(i => CoverageBuildInputJson.Hash(CoverageTableBuilder.ScalarBytes(first.Table.Entry(i).ToArray())) == candidate.ScalarSha256);
            var pair = pairs.Single(p => p.EntryIndex == entry && p.SampleId == candidate.SampleId);
            Assert.True(pair.Valid, pair.ToString());
        }
        Assert.NotEmpty(first.Evidence.SelectedClaims);
        Assert.All(first.Evidence.SelectedClaims, claim => Assert.True(claim.Valid));
        Assert.NotEmpty(first.Evidence.Domains);
        Assert.NotEmpty(first.Evidence.Domains[0].AuthoredFrameIds);
        Assert.Empty(first.Evidence.Holes);
        byte[] bytes = CoverageRecessionTableBinary.Write(first.Table);
        Assert.Equal(bytes, CoverageRecessionTableBinary.Write(second.Table));
        Assert.Equal(first.Evidence.Write(), second.Evidence.Write());
        var verified = CoverageSampleVerifier.Verify(input, CoverageRecessionTableBinary.Read(bytes, "roundtrip"));
        Assert.True(verified.Complete);
        Assert.Equal(4, verified.PairResults.Count);
        Assert.All(verified.SelectedClaims, claim => Assert.True(claim.Valid));
    }

    [Fact]
    public void Repeated_key_requires_one_entry_valid_for_every_pose()
    {
        var result = CoverageTableBuilder.Build(CoverageBuildFixture.Input(repeatedKey: true));
        Assert.Equal(4, result.Evidence.PairResults.Count);
        Assert.Equal(2, result.Evidence.PairResults.Count(p => p.Valid));
        Assert.Single(result.Evidence.Holes);
        Assert.False(result.Table.TrySelect(new(1), out int entry));
        Assert.Equal(-1, entry);
        Assert.False(result.Evidence.Complete);
    }

    [Fact]
    public void Equal_scalar_words_share_one_candidate_and_provenance_must_match()
    {
        var input = CoverageBuildFixture.Input(duplicateCandidate: true);
        var result = CoverageTableBuilder.Build(input);
        Assert.Equal(6, result.Evidence.PairResults.Count);
        Assert.Equal(2, result.Table.EntryCount);
        var wrongIdentity = result.Table.Identity with { StateSchemaSha256 = new string('b', 64) };
        var wrong = CoverageRecessionTable.Create(wrongIdentity, result.Table.Representatives,
            Enumerable.Range(0, result.Table.EntryCount).SelectMany(i => result.Table.Entry(i).ToArray()).ToArray(), result.Table.States);
        var refusal = Assert.Throws<CoverageBuildValidationException>(() => CoverageSampleVerifier.Verify(input, wrong));
        Assert.Equal(CoverageBuildValidationRefusal.ProvenanceMismatch, refusal.Reason);
        Assert.True(CoverageSampleVerifier.Verify(input, result.Table).Complete);
    }

    [Theory]
    [InlineData("out-of-range")]
    [InlineData("wrong-group")]
    [InlineData("missing")]
    public void Representatives_must_own_the_exact_group_and_match_the_candidate_vector(string defect)
    {
        var input = CoverageBuildFixture.Input();
        var result = CoverageTableBuilder.Build(input);
        int[] reps = result.Table.Representatives.ToArray();
        int group = Array.FindIndex(reps, x => x >= 0);
        reps[group] = defect switch { "out-of-range" => input.Topology.VertexCount, "wrong-group" => reps.First(x => x >= 0 && x != reps[group]), _ => -1 };
        var wrong = CoverageRecessionTable.Create(result.Table.Identity, reps,
            Enumerable.Range(0, result.Table.EntryCount).SelectMany(i => result.Table.Entry(i).ToArray()).ToArray(), result.Table.States);
        var refusal = Assert.Throws<CoverageBuildValidationException>(() => CoverageSampleVerifier.Verify(input, wrong));
        Assert.Equal(CoverageBuildValidationRefusal.RepresentativeMismatch, refusal.Reason);
        Assert.Equal(group, refusal.GroupIndex);
        Assert.True(CoverageSampleVerifier.Verify(input, result.Table).Complete);
    }
    [Theory]
    [InlineData("empty-frames", CoverageBuildInputRefusal.EmptyInventory)]
    [InlineData("empty-blend-domain", CoverageBuildInputRefusal.EmptyInventory)]
    [InlineData("missing-frame", CoverageBuildInputRefusal.MissingInventorySample)]
    [InlineData("missing-path", CoverageBuildInputRefusal.MissingInventorySample)]
    [InlineData("wrong-domain", CoverageBuildInputRefusal.DomainMembership)]
    [InlineData("wrong-frame", CoverageBuildInputRefusal.DomainMembership)]
    [InlineData("unknown-candidate", CoverageBuildInputRefusal.UnknownCandidate)]
    [InlineData("duplicate-sample", CoverageBuildInputRefusal.InvalidOrDuplicateId)]
    public void Domain_inventories_are_complete_and_samples_belong_to_their_owner(string defect, CoverageBuildInputRefusal reason)
    {
        using var files = new CoverageBuildFiles();
        var original = files.Manifest.DeepClone().AsObject();
        var domain = files.Manifest["domains"]![0]!;
        switch (defect)
        {
            case "empty-frames": domain["authoredFrameIds"] = new JsonArray(); break;
            case "empty-blend-domain": domain["kind"] = "blend-path"; break;
            case "missing-frame": domain["authoredFrameIds"]!.AsArray().Add("missing"); break;
            case "missing-path": domain["blendPathIds"]!.AsArray().Add("missing"); break;
            case "wrong-domain": files.Manifest["samples"]![0]!["domainId"] = "unknown"; break;
            case "wrong-frame": files.Manifest["samples"]![0]!["authoredFrameId"] = "foreign"; break;
            case "unknown-candidate": files.Manifest["candidateSampleIds"]![0] = "unknown"; break;
            case "duplicate-sample": files.Manifest["samples"]![1]!["id"] = "a"; break;
        }
        files.Write();
        Assert.Equal(reason, Assert.Throws<CoverageBuildDataException>(() => CoverageBuildInput.Read(files.ManifestPath)).Reason);
        File.WriteAllText(files.ManifestPath, original.ToJsonString());
        Assert.Equal(2, CoverageBuildInput.Read(files.ManifestPath).Samples.Count);
    }

    [Theory]
    [InlineData("duplicate", CoverageBuildInputRefusal.DuplicateMember, "$.formatVersion")]
    [InlineData("unknown", CoverageBuildInputRefusal.UnknownMember, "$.unknown")]
    [InlineData("nonfinite", CoverageBuildInputRefusal.NonFiniteNumber, "$.samples[0].actorModel[0]")]
    [InlineData("hash", CoverageBuildInputRefusal.HashMismatch, "$.host.sha256")]
    [InlineData("version", CoverageBuildInputRefusal.UnsupportedVersion, "$.formatVersion")]
    [InlineData("candidate-capacity", CoverageBuildInputRefusal.CapacityExceeded, "$.candidateSampleIds")]
    public void Strict_reader_refuses_malformed_inputs_without_changing_accepted_outputs(string defect, CoverageBuildInputRefusal reason, string path)
    {
        using var files = new CoverageBuildFiles();
        string original = files.Manifest.ToJsonString();
        Assert.Equal(0, Run(files, "build"));
        byte[] table = File.ReadAllBytes(files.TablePath), evidence = File.ReadAllBytes(files.EvidencePath);
        string edited = defect switch
        {
            "duplicate" => original.Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1", StringComparison.Ordinal),
            "unknown" => original.Insert(1, "\"unknown\":0,"),
            "version" => original.Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal),
            _ => original,
        };
        if (defect == "nonfinite")
        {
            files.Manifest["samples"]![0]!["actorModel"]![0] = 12345;
            edited = files.Manifest.ToJsonString().Replace("12345", "1e999", StringComparison.Ordinal);
        }
        if (defect == "hash")
        {
            files.Manifest["host"]!["sha256"] = new string('b', 64);
            edited = files.Manifest.ToJsonString();
        }
        if (defect == "candidate-capacity")
        {
            files.Manifest["candidateSampleIds"] = new JsonArray(Enumerable.Repeat("a", 4097).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            edited = files.Manifest.ToJsonString();
        }
        File.WriteAllText(files.ManifestPath, edited);
        var refusal = Assert.Throws<CoverageBuildDataException>(() => CoverageBuildInput.Read(files.ManifestPath));
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(path, refusal.Path);
        Assert.Equal(2, Run(files, "build"));
        Assert.Equal(table, File.ReadAllBytes(files.TablePath));
        Assert.Equal(evidence, File.ReadAllBytes(files.EvidencePath));
        File.WriteAllText(files.ManifestPath, original);
        Assert.Equal(0, Run(files, "verify-samples"));
    }

    [Fact]
    public void Source_digests_bind_exact_inputs_fixed_recipes_and_authored_configuration()
    {
        using var files = new CoverageBuildFiles();
        var first = CoverageBuildInput.Read(files.ManifestPath);
        Assert.Equal(files.Manifest["host"]!["sha256"]!.GetValue<string>(), first.Identity.HostSha256);
        var map = HostCoverageMapJson.Read(File.ReadAllBytes(Path.Combine(files.Folder, "map.json")), "map");
        Assert.Equal(HostCoverageMapJson.SemanticSha256(map), first.Identity.HostMapSha256);
        Assert.Equal(CoverageBuildInputJson.RefinementHash(first.Topology), first.Identity.RefinementSha256);
        Assert.Equal(CoverageBuildInputJson.ArithmeticHash(), first.Identity.ArithmeticSha256);
        files.Configuration["parts"]![0]!["socket"] = CoverageBuildFiles.Matrix(Matrix4x4.CreateTranslation(.25f, 0, 0));
        files.WriteConfiguration();
        var changed = CoverageBuildInput.Read(files.ManifestPath);
        Assert.NotEqual(first.Identity.ConfigurationSha256, changed.Identity.ConfigurationSha256);
        Assert.Equal(first.Identity.RefinementSha256, changed.Identity.RefinementSha256);
        var refined = new HostCoverageMap(map.HostSha256, map.Topology, map.Regions,
            map.Refinement.Select(r => r with { Level = 2 }), map.Authoring);
        var topology = CoverageRefinement.Build(first.Topology.Source, refined);
        Assert.NotEqual(first.Identity.RefinementSha256, CoverageBuildInputJson.RefinementHash(topology));
        Assert.Equal(0, Run(files, "build"));
        Assert.Equal(0, Run(files, "verify-samples"));
    }

    [Fact]
    public void Cli_refuses_holes_and_resumed_provenance_without_overwriting_accepted_files()
    {
        using var files = new CoverageBuildFiles();
        Assert.Equal(0, Run(files, "build"));
        byte[] table = File.ReadAllBytes(files.TablePath), evidence = File.ReadAllBytes(files.EvidencePath);
        files.Manifest["samples"]![1]!["stateKey"] = 1;
        files.Write();
        Assert.Equal(1, Run(files, "build"));
        Assert.Equal(table, File.ReadAllBytes(files.TablePath));
        Assert.Equal(evidence, File.ReadAllBytes(files.EvidencePath));
        files.Manifest["samples"]![1]!["stateKey"] = 2;
        files.Manifest["stateSchemaSha256"] = new string('b', 64);
        files.Write();
        Assert.Equal(1, Run(files, "build"));
        Assert.Equal(1, Run(files, "verify-samples"));
        Assert.Equal(table, File.ReadAllBytes(files.TablePath));
        Assert.Equal(evidence, File.ReadAllBytes(files.EvidencePath));
        files.Manifest["stateSchemaSha256"] = CoverageBuildFixture.Hash;
        files.Write();
        Assert.Equal(0, Run(files, "verify-samples"));
    }

    [Fact]
    public void Serialized_row_verification_detects_invalid_selected_geometry_without_candidate_construction()
    {
        var input = CoverageBuildFixture.Input();
        var result = CoverageTableBuilder.Build(input);
        // Keep all source identities, representatives and keys, but replace the rows by zero recession.
        var wrong = CoverageRecessionTable.Create(result.Table.Identity, result.Table.Representatives,
            new float[result.Table.EntryCount * result.Table.PositionGroupCount], result.Table.States);
        var evidence = CoverageSampleVerifier.Verify(input, CoverageRecessionTableBinary.Read(CoverageRecessionTableBinary.Write(wrong), "changed"));
        Assert.Equal(4, evidence.PairResults.Count);
        Assert.False(evidence.Complete);
        Assert.Contains(evidence.SelectedClaims, c => !c.Valid && c.Failure.Code != CoverageFailureCode.None);
    }

    [Fact]
    public void Candidate_order_cannot_change_canonical_rows_or_greedy_ties()
    {
        var input = CoverageBuildFixture.Input(duplicateCandidate: true);
        var reordered = new CoverageBuildInput(input.Topology, input.InverseBind, input.Domains, input.Samples,
            input.CandidateSampleIds.Reverse(), input.Identity, input.InputSha256);
        Assert.Equal(CoverageRecessionTableBinary.Write(CoverageTableBuilder.Build(input).Table),
            CoverageRecessionTableBinary.Write(CoverageTableBuilder.Build(reordered).Table));
    }

    [Fact]
    public void Blend_domain_membership_and_typed_sidecar_refusals_remain_strict()
    {
        using var files = new CoverageBuildFiles();
        files.Manifest["domains"]![0]!["kind"] = "blend-path";
        files.Manifest["domains"]![0]!["blendPathIds"]!.AsArray().Add("walk-to-idle");
        files.Manifest["samples"]![0]!["blendPathId"] = "walk-to-idle";
        files.Write();
        Assert.Equal(CoverageDomainKind.BlendPath, CoverageBuildInput.Read(files.ManifestPath).Domains[0].Kind);
        string path = Path.Combine(files.Folder, "map.json");
        byte[] original = File.ReadAllBytes(path);
        string json = Encoding.UTF8.GetString(original).Insert(1, "\"unknown\":0,");
        files.Manifest["map"] = files.Save("map.json", Encoding.UTF8.GetBytes(json));
        files.Write();
        var refusal = Assert.Throws<CoverageBuildDataException>(() => CoverageBuildInput.Read(files.ManifestPath));
        Assert.Equal(CoverageBuildInputRefusal.AssetBinding, refusal.Reason);
        var inner = Assert.IsType<HostCoverageDataException>(refusal.InnerException);
        Assert.Equal(HostCoverageRefusal.UnknownMember, inner.Reason);
        Assert.Equal("$.unknown", inner.Path);
        files.Manifest["map"] = files.Save("map.json", original);
        files.Write();
        Assert.Equal(0, Run(files, "build"));
    }

    [Fact]
    public void Failed_output_staging_and_input_alias_preserve_accepted_files()
    {
        using var files = new CoverageBuildFiles();
        Assert.Equal(0, Run(files, "build"));
        byte[] table = File.ReadAllBytes(files.TablePath);
        byte[] evidence = File.ReadAllBytes(files.EvidencePath);
        string directory = Path.Combine(files.Folder, "directory");
        Directory.CreateDirectory(directory);
        Assert.Equal(1, Program.Main(["build", "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", directory]));
        Assert.Equal(table, File.ReadAllBytes(files.TablePath));
        Assert.Equal(evidence, File.ReadAllBytes(files.EvidencePath));
        string host = Path.Combine(files.Folder, "host.glb");
        byte[] hostBytes = File.ReadAllBytes(host);
        Assert.Equal(1, Program.Main(["build", "--manifest", files.ManifestPath, "--table", host, "--evidence", files.EvidencePath]));
        Assert.Equal(hostBytes, File.ReadAllBytes(host));
        Assert.Equal(0, Run(files, "build"));
    }

    [Fact]
    public void Greedy_cover_publishes_only_selected_rows_and_keeps_the_complete_candidate_matrix()
    {
        var input = CoverageBuildFixture.Input();
        var first = input.Samples[0];
        var transform = Matrix4x4.CreateScale(1, 1, 1.1f);
        var command = first.Commands[0] with { EquipmentWorld = transform, CoverageWorld = transform };
        var second = new CoverageSample("b", "clip", "frame-b", null, new(2), first.ActorModel, first.Palette, [command]);
        var overlapping = new CoverageBuildInput(input.Topology, input.InverseBind, input.Domains, [first, second], input.CandidateSampleIds, input.Identity, input.InputSha256);
        var result = CoverageTableBuilder.Build(overlapping);
        Assert.Equal(4, result.Evidence.PairResults.Count);
        Assert.Equal(1, result.Table.EntryCount);
        Assert.Equal(2, result.Evidence.Entries.Count);
        Assert.Single(result.Evidence.Entries, e => e.TableEntryIndex == -1);
        Assert.Empty(result.Evidence.Holes);
        Assert.True(CoverageSampleVerifier.Verify(overlapping, result.Table).Complete);
    }

    [Fact]
    public async Task Executable_build_and_verify_commands_publish_accepted_synthetic_evidence()
    {
        using var files = new CoverageBuildFiles();
        Assert.Equal(0, await Execute("build"));
        byte[] bytes = File.ReadAllBytes(files.TablePath);
        Assert.Equal(0, await Execute("verify-samples"));
        Assert.Equal(bytes, File.ReadAllBytes(files.TablePath));
        async Task<int> Execute(string command)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (string argument in new[] { typeof(Program).Assembly.Location, command, "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", files.EvidencePath }) start.ArgumentList.Add(argument);
            start.Environment["KE_GPU_TESTS"] = "0";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode;
        }
    }

    [Fact]
    public void Candidates_with_different_representative_vectors_are_refused()
    {
        var input = CoverageBuildFixture.Input();
        var sample = input.Samples[1];
        var inactive = new CoverageSample(sample.Id, sample.DomainId, sample.AuthoredFrameId, sample.BlendPathId,
            sample.StateKey, sample.ActorModel, sample.Palette, []);
        var inconsistent = new CoverageBuildInput(input.Topology, input.InverseBind, input.Domains,
            [input.Samples[0], inactive], input.CandidateSampleIds, input.Identity, input.InputSha256);
        var refusal = Assert.Throws<CoverageBuildValidationException>(() => CoverageTableBuilder.Build(inconsistent));
        Assert.Equal(CoverageBuildValidationRefusal.RepresentativeMismatch, refusal.Reason);
        Assert.Equal("b", refusal.SampleId);
        Assert.True(CoverageTableBuilder.Build(input).Evidence.Complete);
    }

    [Theory]
    [InlineData("build", "truncated", CoverageBuildInputRefusal.InvalidJson)]
    [InlineData("verify-samples", "truncated", CoverageBuildInputRefusal.InvalidJson)]
    [InlineData("build", "duplicate", CoverageBuildInputRefusal.DuplicateMember)]
    [InlineData("verify-samples", "duplicate", CoverageBuildInputRefusal.DuplicateMember)]
    [InlineData("build", "wrong-type", CoverageBuildInputRefusal.InvalidValue)]
    [InlineData("verify-samples", "wrong-type", CoverageBuildInputRefusal.InvalidValue)]
    public void Malformed_resumed_evidence_returns_exit_two_and_preserves_both_outputs(string command, string defect, CoverageBuildInputRefusal reason)
    {
        using var files = new CoverageBuildFiles();
        Assert.Equal(0, Run(files, "build"));
        byte[] table = File.ReadAllBytes(files.TablePath), accepted = File.ReadAllBytes(files.EvidencePath);
        string json = Encoding.UTF8.GetString(accepted);
        if (defect == "truncated") json = "{";
        else if (defect == "duplicate") json = json.Insert(1, "\"formatVersion\":1,");
        else
        {
            var root = JsonNode.Parse(json)!;
            root["tableSha256"] = 7;
            json = root.ToJsonString();
        }
        byte[] malformed = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(files.EvidencePath, malformed);
        Assert.Equal(2, Run(files, command));
        Assert.Equal(table, File.ReadAllBytes(files.TablePath));
        Assert.Equal(malformed, File.ReadAllBytes(files.EvidencePath));
        var evidence = CoverageTableBuilder.Build(CoverageBuildInput.Read(files.ManifestPath)).Evidence;
        Assert.Equal(reason, Assert.Throws<CoverageBuildDataException>(() => evidence.ValidateResume(malformed)).Reason);
        File.WriteAllBytes(files.EvidencePath, accepted);
        Assert.Equal(0, Run(files, command));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("verify-samples")]
    public void Evidence_byte_bound_is_shared_by_publication_and_resume_admission(string command)
    {
        using var files = new CoverageBuildFiles();
        Assert.Equal(0, Run(files, "build"));
        byte[] table = File.ReadAllBytes(files.TablePath), accepted = File.ReadAllBytes(files.EvidencePath);
        var input = CoverageBuildInput.Read(files.ManifestPath);
        var evidence = command == "build" ? CoverageTableBuilder.Build(input).Evidence
            : CoverageSampleVerifier.Verify(input, CoverageRecessionTableBinary.Read(table, "accepted"));
        byte[] candidate = evidence.Write();
        int limit = candidate.Length - 1;
        string[] args = [command, "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", files.EvidencePath];
        Assert.Equal(2, Program.Run(args, limit));
        Assert.Equal(table, File.ReadAllBytes(files.TablePath));
        Assert.Equal(accepted, File.ReadAllBytes(files.EvidencePath));
        var refusal = Assert.Throws<CoverageBuildDataException>(() => evidence.Write(limit));
        Assert.Equal(CoverageBuildInputRefusal.CapacityExceeded, refusal.Reason);
        Assert.Equal(limit, refusal.Limit);
        Assert.True(refusal.Count > limit);
        // The inclusive bound admits and republishes its own exact output without a large fixture.
        File.WriteAllBytes(files.EvidencePath, candidate);
        Assert.Equal(0, Program.Run(args, candidate.Length));
        Assert.Equal(0, Program.Run(args, candidate.Length));
        byte[] oversized = [.. candidate, (byte)' '];
        File.WriteAllBytes(files.EvidencePath, oversized);
        Assert.Equal(2, Program.Run(args, candidate.Length));
        Assert.Equal(table, File.ReadAllBytes(files.TablePath));
        Assert.Equal(oversized, File.ReadAllBytes(files.EvidencePath));
        var readRefusal = Assert.Throws<CoverageBuildDataException>(() => CoverageBuildInputJson.ReadBytes(files.EvidencePath, candidate.Length));
        Assert.Equal(CoverageBuildInputRefusal.CapacityExceeded, readRefusal.Reason);
        Assert.Equal(candidate.Length, readRefusal.Limit);
        Assert.Equal(oversized.Length, readRefusal.Count);
        File.WriteAllBytes(files.EvidencePath, candidate);
        Assert.Equal(0, Program.Run(args, candidate.Length));
    }

    [Theory]
    [InlineData(0x00800000u, false)]
    [InlineData(0x00800001u, true)]
    [InlineData(0x007fffffu, false)]
    public void Representative_activity_uses_the_retained_inclusive_flush_boundary(uint strengthWord, bool active)
    {
        var original = CoverageBuildFixture.Input();
        int[] collarGroups = original.Topology.Vertices.Where(v => v.Strength == .5f).Select(v => v.PositionGroup).Distinct().ToArray();
        Assert.NotEmpty(collarGroups);
        var input = CoverageBuildFixture.WithCollarStrength(strengthWord);
        int[] representatives = CoverageSampleVerifier.Representatives(input.Topology, input.Samples[0]);
        Assert.All(collarGroups, group => Assert.Equal(active, representatives[group] >= 0));
        var result = CoverageTableBuilder.Build(input);
        Assert.True(result.Evidence.Complete);
        Assert.Equal(representatives, result.Table.Representatives.ToArray());
    }

    static int Run(CoverageBuildFiles files, string command) => Program.Main([command, "--manifest", files.ManifestPath, "--table", files.TablePath, "--evidence", files.EvidencePath]);

}
