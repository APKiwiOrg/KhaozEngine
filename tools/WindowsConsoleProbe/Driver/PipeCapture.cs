using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowsConsoleValidation;

internal sealed class PipeCapture : IDisposable
{
    private readonly SafeFileHandle write;
    private readonly Task<string> content;
    internal long Handle => write.DangerousGetHandle().ToInt64();

    internal PipeCapture()
    {
        var pipe = NativeStreams.Pipe();
        SafeFileHandle read = pipe.Read;
        write = pipe.Write;
        content = Task.Run(() =>
        {
            using var stream = new FileStream(read, FileAccess.Read);
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[512];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                bytes.Write(buffer, 0, count);
                if (bytes.Length > 4096) throw new InvalidOperationException("Pipe exceeded finite marker capacity.");
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        });
    }

    internal string Complete()
    {
        write.Dispose();
        if (!content.Wait(5000)) throw new TimeoutException("Pipe capture did not reach EOF within five seconds.");
        return content.GetAwaiter().GetResult();
    }

    public void Dispose() => Complete();
}
