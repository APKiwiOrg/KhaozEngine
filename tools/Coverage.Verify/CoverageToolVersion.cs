using System;
using System.Text.Json;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

internal static class CoverageToolVersion
{
    internal static int Write()
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            engineVersion = typeof(Program).Assembly.GetName().Version!.ToString(3),
            carryArithmeticContractVersion = CoverageArithmeticContract.Version,
        }));
        return 0;
    }
}
