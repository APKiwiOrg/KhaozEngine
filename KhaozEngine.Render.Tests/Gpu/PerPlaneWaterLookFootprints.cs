using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The three captures <see cref="PerPlaneWaterLookGpuTests"/>' two tests each computed for themselves, computed
    /// ONCE for the class as an xUnit <c>IClassFixture</c>
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/639">#639</see>): the no-water render and the two
    /// single-plane renders the footprints are derived from. Both tests asked for the identical thing with the
    /// identical arguments, so the class captured eight pictures to look at five.
    ///
    /// <para>
    /// <b>THIS SHARES THE RESULT, NOT THE SCENE, AND THE DIFFERENCE IS THE WHOLE SAFETY ARGUMENT.</b>
    /// <see cref="OceanFocusScene"/> shares a live <see cref="Scene3D"/> between configurations, which is worth
    /// far more and costs an explicit determinism pin, because a producer carries state across a frame by design
    /// and a reused scene therefore has to PROVE it renders what a fresh one does. Nothing of the sort applies
    /// here. Every capture below still runs through the untouched public
    /// <see cref="Render3DSnapshot.Capture(int,int,System.Action{Scene3D},System.Action{Scene3D},int,ShadowSettings?)"/>
    /// path on its own device and its own scene, exactly as before. All that is removed is asking for the same
    /// picture twice, and the pictures are deterministic, which every golden in this assembly already rests on.
    /// </para>
    /// <para>
    /// <b>Why it was worth doing at all after
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/640">#640</see>.</b> A capture is no longer
    /// dominated by shader compilation, so the saving here is three renders rather than three scene constructions,
    /// and it is proportionally larger on the software legs where a render costs the most. It is also the only one
    /// of #639's rows that needed no judgement about producer state: two of the others are blocked by
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/645">#645</see> and the rest turned out not to be
    /// scene-bound.
    /// </para>
    /// <para>
    /// Lazy on first use, like <see cref="OceanFocusScene"/>, so a plain <c>dotnet test</c> that skips every
    /// <c>[GpuFact]</c> in the class never renders anything. Holds no process-global state and swaps no ambient
    /// static, so it needs no <c>DisableParallelization</c> collection, and xUnit runs one class's methods
    /// sequentially so the memo is only ever filled by one test at a time.
    /// </para>
    /// </summary>
    public sealed class PerPlaneWaterLookFootprints
    {
        byte[]? _dry;
        bool[]? _sea;
        bool[]? _lake;

        /// <summary>The scene with the seabed alone and no water queued. The control every footprint is measured
        /// against.</summary>
        public byte[] Dry => _dry ??= PerPlaneWaterLookGpuTests.Render(
            PerPlaneWaterLookGpuTests.W, PerPlaneWaterLookGpuTests.H, null, water: false);

        /// <summary>The eroded footprint of the scene's own sea plane.</summary>
        public bool[] SeaMask => _sea ??= Mask(only: 0);

        /// <summary>The eroded footprint of the overridable plane.</summary>
        public bool[] LakeMask => _lake ??= Mask(only: 1);

        bool[] Mask(int only) => PerPlaneWaterLookGpuTests.Footprint(
            PerPlaneWaterLookGpuTests.Render(
                PerPlaneWaterLookGpuTests.W, PerPlaneWaterLookGpuTests.H, null, only: only),
            Dry, PerPlaneWaterLookGpuTests.W, PerPlaneWaterLookGpuTests.H);
    }
}
