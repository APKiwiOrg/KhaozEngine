using System;

namespace KhaozEngine.ShaderBuild;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length is not (2 or 4) || args[0] != "--pins" || (args.Length == 4 && args[2] != "--slangc"))
        {
            Console.Error.WriteLine("usage: KhaozEngine.ShaderBuild --pins <slangc.pins.json> [--slangc <path>]");
            return 2;
        }
        try
        {
            var pins = SlangcPins.Load(args[1]);
            var slangcOverride = args.Length == 4 ? args[3] : Environment.GetEnvironmentVariable(SlangcResolver.OverrideVariable);
            Console.WriteLine(SlangcResolver.Resolve(pins, slangcOverride, SlangcResolver.DefaultCacheRoot()));
            return 0;
        }
        catch (ShaderBuildException refusal)
        {
            Console.Error.WriteLine($"error {refusal.Failure}: {refusal.Message}");
            return 1;
        }
    }
}
