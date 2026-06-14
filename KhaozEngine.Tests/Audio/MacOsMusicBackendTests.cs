using System.IO;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Headless coverage for <see cref="MacOsMusicBackend"/> track loading. These touch the filesystem
/// only (the native AVAudioPlayer bridge is created lazily at first playback), so they run on any OS.
/// </summary>
public sealed class MacOsMusicBackendTests
{
    // Regression: the DesktopGL content pipeline emits .ogg (plus a tiny .xnb header), not raw .mp3.
    // The backend must locate the built .ogg, or every track is dropped and music never plays.
    [Fact]
    public void TryLoadTrack_FindsBuiltOgg()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("ke-music-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "Music"));
            File.WriteAllBytes(Path.Combine(dir.FullName, "Music", "Theme.ogg"), [1]);

            var backend = new MacOsMusicBackend();
            bool loaded = backend.TryLoadTrack(content: null!, dir.FullName, "Music/Theme");

            Assert.True(loaded);
            Assert.Equal(1, backend.TrackCount);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoadTrack_FindsRawMp3Fallback()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("ke-music-");
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "Theme.mp3"), [1]);

            var backend = new MacOsMusicBackend();
            bool loaded = backend.TryLoadTrack(content: null!, dir.FullName, "Theme");

            Assert.True(loaded);
            Assert.Equal(1, backend.TrackCount);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryLoadTrack_ReturnsFalseWhenNoAudioFilePresent()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("ke-music-");
        try
        {
            var backend = new MacOsMusicBackend();
            bool loaded = backend.TryLoadTrack(content: null!, dir.FullName, "Music/Missing");

            Assert.False(loaded);
            Assert.Equal(0, backend.TrackCount);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
