using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Groups the tests in THIS assembly that build and tear down a whole GPU device, so no two of them are ever
/// doing it at once. The second copy of the definition <c>KhaozEngine.Render.Tests</c> carries, identical by
/// name and by intent, because xUnit collection definitions do not cross assemblies and this one now has two
/// assemblies that need it. <c>AllocSensitiveCollection</c> is the same shape for the same reason.
///
/// <para><b>THE COST IS MEASURED, NOT SUSPECTED.</b> The first WARP run of the <c>direct3d11-native</c> leg took
/// 49 minutes where that leg normally takes 17 (run 30955744945), with device-building tests creating their
/// devices in parallel with everything else. A software rasterizer pays for a device in real seconds, and the
/// suite's primary device is busy rendering throughout, so the two contend for the same driver. The
/// <c>vulkan-native</c> leg (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/529">#529</see>) meets
/// that contention on lavapipe, where the full suite is ALREADY serialised at roughly twenty-odd minutes, which
/// is why the copy lands with the leg rather than after someone reads a first scheduled run as a hang.</para>
///
/// <para><b>FOUR DEVICES IN THIS ASSEMBLY, ACROSS TWO CLASSES, AND THEY ARE NOT OBVIOUS FROM THE CALL SITES.</b>
/// <c>ViewportWorldDisposeOrderGpuTests</c> calls <c>GpuDeviceContext.CreateHeadless</c> outright, so that one
/// reads as what it is. The three <c>RenderServiceTests</c> rows do not: each calls a <c>RenderService</c> render
/// method, which reaches <c>Render3DSnapshot.Capture</c>, which creates and disposes a headless device of its
/// own per call. A reader looking for device work by grepping this assembly for <c>CreateHeadless</c> finds one
/// hit and misses three.</para>
/// </summary>
[CollectionDefinition("NativeDeviceLifecycle", DisableParallelization = true)]
public sealed class NativeDeviceLifecycleCollection { }
