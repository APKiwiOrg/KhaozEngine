using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("NativeDeviceLifecycle")]
public sealed class RendererPerfColdLimitWorkflowTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("fractional")]
    [InlineData("null")]
    public void Trusted_selector_refuses_a_mistyped_cold_limit(string fault)
    {
        using var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        fixture.Manifest["limits"]!["coldFrameMilliseconds"] = fault switch
        {
            "text" => System.Text.Json.Nodes.JsonValue.Create("60000"),
            "fractional" => System.Text.Json.Nodes.JsonValue.Create(60000.5),
            _ => null
        };
        fixture.SaveManifest();
        Assert.NotEqual(0, fixture.Run("Select").Code);
    }

    [Theory]
    [MemberData(nameof(RendererPerfColdLimitPolicyTests.Classes), MemberType = typeof(RendererPerfColdLimitPolicyTests))]
    public void Trusted_selector_requires_the_cold_limit_for_every_class(string preset, string leg)
    {
        using var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        fixture.Manifest = RendererPerfColdLimitPolicyTests.ManifestJson(preset, leg);
        fixture.Manifest["workloadSha"] = fixture.Workload;
        fixture.Environment["LEGS"] = leg;
        fixture.Manifest["limits"]!["coldFrameMilliseconds"] = 60000;
        fixture.SaveManifest();
        fixture.Accept("Select", "jobMinutes=");
        fixture.Manifest["limits"]!["coldFrameMilliseconds"] = 60001;
        fixture.SaveManifest();
        Assert.NotEqual(0, fixture.Run("Select").Code);
        fixture.Manifest["limits"]!.AsObject().Remove("coldFrameMilliseconds");
        fixture.SaveManifest();
        Assert.NotEqual(0, fixture.Run("Select").Code);
    }
}
