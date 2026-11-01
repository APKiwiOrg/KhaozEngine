using System.IO;

namespace KhaozEngine.Tests.Benchmarks;

/// <summary>Bounded cleanup for disposed Items benchmark fixtures, without rerunning measurements.</summary>
internal static class ItemsBenchmarkFixtureDirectory
{
    internal static void Delete(string directory)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
    }
}
