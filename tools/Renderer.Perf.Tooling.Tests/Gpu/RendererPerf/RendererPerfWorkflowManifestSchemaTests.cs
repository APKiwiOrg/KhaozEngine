using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("NativeDeviceLifecycle")]
public sealed class RendererPerfWorkflowManifestSchemaTests
{
    public static IEnumerable<object[]> Sections()
    {
        yield return ["", "baselineMainSha"];
        yield return ["recipes[0]", "assetSha256"];
        yield return ["blocks[0]", "sceneId"];
        yield return ["controls[0]", "workSha256"];
        yield return ["runtime", "tieredCompilation"];
        yield return ["execution", "backend"];
        yield return ["limits", "unitSeconds"];
        yield return ["budget", "maximumCostUsd"];
        yield return ["approval", "reference"];
    }

    [Theory]
    [InlineData("execution", "lockedGraphicsClockMHz", "LockedGraphicsClockMHz")]
    [InlineData("", "execution", "Execution")]
    public void Select_refuses_case_variants_beside_the_exact_field(string section, string field, string variant)
    {
        using var workspace = Workspace();
        JsonObject target = Section(workspace.Fixture.Manifest, section);
        target[variant] = section == "execution" ? JsonValue.Create(0) : target[field]!.DeepClone();
        RefuseBeforeAllocation(workspace, Path(section, variant));
    }

    [Theory]
    [MemberData(nameof(Sections))]
    public void Select_refuses_unknown_fields_in_every_object_type(string section, string field)
    {
        using var workspace = Workspace();
        JsonObject target = Section(workspace.Fixture.Manifest, section);
        target["extra"] = target[field]!.DeepClone();
        RefuseBeforeAllocation(workspace, Path(section, "extra"));
    }

    [Theory]
    [MemberData(nameof(Sections))]
    [InlineData("", "recipes")]
    [InlineData("execution", "lockedGraphicsClockMHz")]
    public void Select_refuses_missing_required_fields_in_every_object_type(string section, string field)
    {
        using var workspace = Workspace();
        Assert.True(Section(workspace.Fixture.Manifest, section).Remove(field));
        RefuseBeforeAllocation(workspace, Path(section, field));
    }

    [Theory]
    [MemberData(nameof(Sections))]
    [InlineData("", "recipes")]
    [InlineData("", "runtime")]
    [InlineData("", "conditioningFrames")]
    [InlineData("runtime", "environment")]
    public void Select_refuses_null_in_non_nullable_fields(string section, string field)
    {
        using var workspace = Workspace();
        Section(workspace.Fixture.Manifest, section)[field] = null;
        RefuseBeforeAllocation(workspace, Path(section, field));
    }

    [Theory]
    [InlineData("", "conditioningFrames", "\"1200\"")]
    [InlineData("", "conditioningFrames", "2147483648")]
    [InlineData("", "conditioningFrames", "-2147483649")]
    [InlineData("", "conditioningFrames", "1200.0")]
    [InlineData("", "baselineMainSha", "123")]
    [InlineData("runtime", "tieredCompilation", "\"true\"")]
    [InlineData("blocks[0]", "layout", "0")]
    [InlineData("", "runtime", "[]")]
    [InlineData("", "recipes", "{}")]
    [InlineData("runtime", "environment", "[]")]
    [InlineData("budget", "ratePerMinute", "\"0.052\"")]
    public void Select_refuses_wrong_field_kinds(string section, string field, string json)
    {
        using var workspace = Workspace();
        Section(workspace.Fixture.Manifest, section)[field] = JsonNode.Parse(json);
        RefuseBeforeAllocation(workspace, Path(section, field));
    }

    [Theory]
    [InlineData("recipes")]
    [InlineData("blocks")]
    [InlineData("controls")]
    public void Select_requires_objects_inside_record_arrays(string field)
    {
        using var workspace = Workspace();
        workspace.Fixture.Manifest[field]![0] = "not-an-object";
        RefuseBeforeAllocation(workspace, field + "[0]");
    }

    [Theory]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    public void Select_requires_non_null_strings_in_the_environment_map(string json)
    {
        using var workspace = Workspace();
        workspace.Fixture.Manifest["runtime"]!["environment"]!["CUSTOM"] = JsonNode.Parse(json);
        RefuseBeforeAllocation(workspace, "runtime.environment.CUSTOM");
    }

    [Fact]
    public void Select_accepts_free_ordinal_environment_keys_with_string_values()
    {
        using var workspace = Workspace();
        JsonNode environment = workspace.Fixture.Manifest["runtime"]!["environment"]!;
        environment["Custom"] = "one";
        environment["custom"] = "two";
        environment["keys"] = "three";
        RendererPerfRehearsalManifestTests.Parse(workspace.Fixture.Manifest);
        workspace.Fixture.SaveManifest();
        var result = workspace.Select();
        Assert.True(result.Code == 0, result.Output);
        Assert.Contains("matrix=", File.ReadAllText(workspace.Output), StringComparison.Ordinal);
    }

    static RendererPerfWorkflowTrustTests.AdmissionWorkspace Workspace()
    {
        var workspace = new RendererPerfWorkflowTrustTests.AdmissionWorkspace();
        var fixture = workspace.Fixture;
        fixture.Manifest = RendererPerfColdLimitPolicyTests.ManifestJson("water-optics-v2", "linux-vulkan");
        fixture.Manifest["workloadSha"] = fixture.Workload;
        RendererPerfRehearsalManifestTests.Parse(fixture.Manifest);
        return workspace;
    }

    static JsonObject Section(JsonObject manifest, string section)
        => section.Length == 0 ? manifest : section.EndsWith("[0]", StringComparison.Ordinal)
            ? manifest[section[..^3]]![0]!.AsObject() : manifest[section]!.AsObject();

    static string Path(string section, string field) => section.Length == 0 ? field : section + "." + field;

    static void RefuseBeforeAllocation(RendererPerfWorkflowTrustTests.AdmissionWorkspace workspace, string path)
    {
        string text = workspace.Fixture.Manifest.ToJsonString();
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(text)));
        workspace.Fixture.SaveManifest();
        var result = workspace.Select();
        Assert.True(result.Code != 0, result.Output);
        Assert.Contains($"Manifest field {path} ", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("matrix=", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(workspace.Output) && File.ReadAllText(workspace.Output).Contains("matrix=", StringComparison.Ordinal));
    }
}
