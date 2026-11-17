namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// The half of the test-owned native scope that changes rounding and flush controls. Only test assemblies whose
// tests change controls compile it. KhaozEngine.TileWorld.Physics.Tests does not, so it cannot set one.
internal sealed partial class WaterFloatingPointEnvironment
{
    internal bool Set(string mode)
    {
        if (_windows)
            return Apply(windows: mode switch
            {
                "up" => 0x200u,
                "down" => 0x100u,
                "zero" => 0x300u,
                "flush-output" => 0x03000000u,
                "flush-input" => 0x02000000u,
                _ => 0u,
            }, arm: 0, mxcsr: 0, x87: 0);
        // ARM's FZ controls both operands and results. Independent FIZ is optional on newer
        // cores, so the input-only case is exercised on x64 where DAZ is independent of FTZ.
        if (_arm && mode == "flush-input") return false;
        return Apply(windows: 0, arm: mode switch
        {
            "up" => 0x400000ul,
            "down" => 0x800000ul,
            "zero" => 0xc00000ul,
            "flush-output" => 0x1000000ul,
            _ => 0ul,
        }, mxcsr: mode switch
        {
            "up" => 0x4000u,
            "down" => 0x2000u,
            "zero" => 0x6000u,
            "flush-output" => 0x8000u,
            "flush-input" => 0x40u,
            _ => 0u,
        }, x87: mode switch
        {
            "up" => 0x800u,
            "down" => 0x400u,
            "zero" => 0xc00u,
            _ => 0u,
        });
    }

    /// <summary>The control change a shared test body receives in a floating point test assembly: any mode.</summary>
    internal static bool AnyMode(WaterFloatingPointEnvironment environment, string mode) => environment.Set(mode);
}
