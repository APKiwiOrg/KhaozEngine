using System;
using System.IO;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class SaveBackupsTests
{
    [Fact]
    public void GenerationPath_ZeroIsPrimary_NIsBakN()
    {
        Assert.Equal("/x/save.json", SaveBackups.GenerationPath("/x/save.json", 0));
        Assert.Equal("/x/save.json.bak2", SaveBackups.GenerationPath("/x/save.json", 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rotate_ShiftsGenerationsAndReplacesTheOldest_PrimarySurvives(bool oldestExists)
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "s.json");
        File.WriteAllText(path, "newest");
        File.WriteAllText(path + ".bak1", "mid");
        if (oldestExists)
            File.WriteAllText(path + ".bak2", "oldest");

        SaveBackups.Rotate(path, 2);

        Assert.Equal("newest", File.ReadAllText(path));
        Assert.Equal("newest", File.ReadAllText(path + ".bak1"));
        Assert.Equal("mid", File.ReadAllText(path + ".bak2"));
    }

    [Fact]
    public void Rotate_ZeroGenerations_NoOp()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "s.json");
        File.WriteAllText(path, "new");

        SaveBackups.Rotate(path, 0);

        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak1"));
    }

    [Fact]
    public void Rotate_MissingPrimary_NoOp()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.Path, "s.json");

        SaveBackups.Rotate(path, 2);

        Assert.False(File.Exists(path + ".bak1"));
        Assert.False(File.Exists(path + ".bak2"));
    }

    sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("khaoz-backups-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
