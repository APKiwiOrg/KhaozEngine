using System.IO;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfFixtureDirectory
{
    internal static void Delete(string path)
    {
        var directory = new DirectoryInfo(path);
        // Nonrecursive deletion unlinks junctions without the runtime's recursive mount-point removal.
        if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            directory.Attributes &= ~FileAttributes.ReadOnly;
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo child) Delete(child.FullName);
                else
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                    entry.Delete();
                }
            }
        }
        directory.Delete();
    }
}
