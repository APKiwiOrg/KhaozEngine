using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using KhaozEngine.Content;
using KhaozEngine.Diagnostics;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests.Logging;

/// <summary>
/// Verifies engine packages log under their own class-name category (via <c>Log.For&lt;T&gt;()</c>)
/// rather than hand-rolled message prefixes, and that the loggers fall back to the ambient
/// <see cref="Log"/> facade when none is injected. Touches static <see cref="Log"/> state, so it
/// runs in the serial logging collection.
/// </summary>
[Collection("LoggingSerial")]
public class PackageCategoryTests
{
    private static InMemorySink ConfigureCapture()
    {
        var sink = new InMemorySink();
        var options = new LoggerOptions { Synchronous = true, MinimumLevel = LogLevel.Trace, DefaultCategory = "App" };
        options.Sinks.Add(sink);
        Log.Configure(options);
        return sink;
    }

    [Fact]
    public void SaveEncoder_NullLogger_FallsBackToAmbient_UnderOwnCategory()
    {
        InMemorySink sink = ConfigureCapture();
        try
        {
            byte[] key = System.Text.Encoding.UTF8.GetBytes("k");
            var encoder = new SaveEncoder(key, "PFX1", logger: null);   // no logger -> ambient fallback

            // Corrupt the HMAC hex (first char after the "PFX1:v2:" prefix and version marker), leaving the
            // base64 payload intact so decoding hits the lenient HMAC-mismatch path (Warn), not a base64
            // FormatException.
            string encoded = encoder.Encode("{\"x\":1}");
            int hmacAt = "PFX1:v2:".Length;
            char flipped = encoded[hmacAt] == 'a' ? 'b' : 'a';
            encoder.Decode(encoded[..hmacAt] + flipped + encoded[(hmacAt + 1)..]);

            LogEntry e = Assert.Single(sink.Entries, x => x.Level == LogLevel.Warn);
            Assert.Equal(nameof(SaveEncoder), e.Category);
            Assert.DoesNotContain("[SaveEncoder]", e.Message);          // prefix lives in the category, not the message
            Assert.Contains("HMAC mismatch", e.Message);
        }
        finally { Log.Shutdown(); }
    }

    [Fact]
    public void ConfigLoader_LogsResolvedSource_UnderOwnCategory()
    {
        InMemorySink sink = ConfigureCapture();
        string tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, "{ \"name\": \"disk\", \"count\": 9 }");
        try
        {
            Assembly asm = typeof(PackageCategoryTests).Assembly;
            ConfigLoader.Load<SampleConfig>(asm, "KhaozEngine.Tests.Fixtures.sample.json", diskPath: tmp);

            LogEntry e = Assert.Single(sink.Entries, x => x.Category == "ConfigLoader");
            Assert.Contains(nameof(SampleConfig), e.Message);
            Assert.Contains("disk", e.Message);
        }
        finally { File.Delete(tmp); Log.Shutdown(); }
    }

    [Fact]
    public void LocalizationManager_SetCulture_LogsUnderOwnCategory()
    {
        InMemorySink sink = ConfigureCapture();
        CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
        CultureInfo originalUiCulture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            LocalizationManager.SetCulture("fr-FR");

            LogEntry e = Assert.Single(sink.Entries, x => x.Category == nameof(LocalizationManager));
            Assert.Contains("fr-FR", e.Message);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
            Thread.CurrentThread.CurrentUICulture = originalUiCulture;
            Log.Shutdown();
        }
    }
}
