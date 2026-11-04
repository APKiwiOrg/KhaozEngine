using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfD3D11DiagnosticsTests
{
    [Theory]
    [InlineData(D3D11CompilerEvidenceRefusal.ReadProfileMismatch)]
    [InlineData(D3D11CompilerEvidenceRefusal.PipelineUnavailable)]
    [InlineData(D3D11CompilerEvidenceRefusal.ConstructionIdentityInvalid)]
    [InlineData(D3D11CompilerEvidenceRefusal.FailureOriginMissing)]
    public void CompilerReadsKeepDistinctValidationFailures(D3D11CompilerEvidenceRefusal reason)
    {
        var evidence = D3D11ShaderEvidence.Capture("compute", new byte[] { 1, 2, 3 }, "compute", 0,
            new(true, D3D11RecordMode.Deferred, false, false), new ReflectionApi());
        var control = new D3D11PipelineDiagnostics([evidence], new('a', 64), new('b', 64));
        Assert.True(control.Read(false, D3D11PipelineDiagnostics.Request, _ => { }, default).Complete);
        if (reason == D3D11CompilerEvidenceRefusal.FailureOriginMissing)
            evidence = evidence with { Status = GpuDiagnosticStatus.Invalid, Reason = "missing origin", Failures = [] };
        var diagnostics = new D3D11PipelineDiagnostics([evidence],
            reason == D3D11CompilerEvidenceRefusal.ConstructionIdentityInvalid ? "invalid" : new('a', 64), new('b', 64));
        var request = reason == D3D11CompilerEvidenceRefusal.ReadProfileMismatch ? D3D11PipelineDiagnostics.Request with { MaximumExecutables = 1 } : D3D11PipelineDiagnostics.Request;
        var result = diagnostics.Read(reason == D3D11CompilerEvidenceRefusal.PipelineUnavailable, request, _ => { }, default);
        Assert.False(result.Complete);
        var failure = Assert.Single(result.Failures);
        if (reason == D3D11CompilerEvidenceRefusal.PipelineUnavailable)
        {
            var error = Assert.IsType<D3D11CompilerEvidenceDisposedException>(failure.Error);
            Assert.IsAssignableFrom<ObjectDisposedException>(error);
            Assert.Equal(reason, error.Reason);
            Assert.Equal("D3D11 pipeline", error.ObjectName);
        }
        else
        {
            var error = Assert.IsType<D3D11CompilerEvidenceStateException>(failure.Error);
            Assert.IsAssignableFrom<InvalidOperationException>(error);
            Assert.Equal(reason, error.Reason);
            if (reason == D3D11CompilerEvidenceRefusal.ReadProfileMismatch)
            {
                Assert.Equal(request, error.RequestedProfile);
                Assert.Equal(D3D11PipelineDiagnostics.Request, error.RequiredProfile);
            }
        }
    }
}
