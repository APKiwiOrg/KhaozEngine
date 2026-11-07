namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>An original protocol refusal and the comparison reason published for its side.</summary>
internal sealed record RendererPerfProtocolRefusal(RendererPerfProtocolException Error, string Reason);
