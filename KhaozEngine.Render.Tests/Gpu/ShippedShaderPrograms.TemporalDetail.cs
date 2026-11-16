using System.Collections.Generic;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

public static partial class ShippedShaderPrograms
{
    static IEnumerable<ShippedGraphicsProgram> TemporalDetailPrograms()
    {
        yield return new("TemporalDetailClassify", ShaderSources.FullscreenVert,
            ShaderSources.TemporalDetailClassifyFragment(TemporalResolvePrecision.Full));
        yield return new("TemporalDetailClassifyHalf", ShaderSources.FullscreenVert,
            ShaderSources.TemporalDetailClassifyFragment(TemporalResolvePrecision.Half));
        yield return new("TemporalDetailResolve", ShaderSources.FullscreenVert,
            ShaderSources.TemporalDetailResolveFragment(TemporalResolvePrecision.Full));
        yield return new("TemporalDetailResolveHalf", ShaderSources.FullscreenVert,
            ShaderSources.TemporalDetailResolveFragment(TemporalResolvePrecision.Half));
        yield return new("TemporalDetailUpdate", ShaderSources.FullscreenVert, ShaderSources.TemporalDetailUpdateFrag);
        yield return new("TemporalDetailDebug", ShaderSources.FullscreenVert,
            ShaderSources.TemporalDetailDebugFragment(TemporalResolvePrecision.Full));
        yield return new("TemporalDetailDebugHalf", ShaderSources.FullscreenVert,
            ShaderSources.TemporalDetailDebugFragment(TemporalResolvePrecision.Half));
        yield return new("TemporalDetailProbe", ShaderSources.FullscreenVert,
            ShaderSources.TemporalDetailProbeFragment(TemporalResolvePrecision.Full));
        yield return new("TemporalDetailProbeHalf", ShaderSources.FullscreenVert,
            ShaderSources.TemporalDetailProbeFragment(TemporalResolvePrecision.Half));
    }
}
