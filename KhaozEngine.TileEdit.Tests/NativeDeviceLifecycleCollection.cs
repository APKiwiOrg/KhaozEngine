using Xunit;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>Groups the tests in THIS assembly that build and tear down a whole GPU device, so no two of them are
/// ever doing it at once. A third copy of a definition <c>KhaozEngine.Render.Tests</c> and
/// <c>KhaozEngine.MapEditor.Tests</c> already carry, identical by name and by intent, because xUnit collection
/// definitions do not cross assemblies and this is now a third assembly that needs one.
///
/// <para>The cost is measured rather than suspected: on a software rasterizer (WARP, lavapipe) a device costs
/// real seconds and the suite's own primary device is busy throughout, so device-building tests running in
/// parallel with everything else contend for one driver. See the copy in <c>KhaozEngine.MapEditor.Tests</c> for
/// the run that made that case.</para>
///
/// <para>The device work here is NOT obvious from the call sites: every <c>RenderServiceTests</c> row calls a
/// <c>RenderService</c> render method, which reaches <c>TileWorldSnapshot</c> and then
/// <c>Render3DSnapshot.Capture</c>, and that creates and disposes a headless device per call. Grepping this
/// assembly for <c>CreateHeadless</c> finds nothing at all.</para></summary>
[CollectionDefinition("NativeDeviceLifecycle", DisableParallelization = true)]
public sealed class NativeDeviceLifecycleCollection { }
