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

    [Fact]
    public void Rotate_ShiftsGenerations_PrimarySurvives()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(dir, "s.json");
        File.WriteAllText(path, "new");
        File.WriteAllText(path + ".bak1", "old");

        SaveBackups.Rotate(path, 2);

        Assert.Equal("new", File.ReadAllText(path));          // primary untouched (copied, not moved)
        Assert.Equal("new", File.ReadAllText(path + ".bak1"));
        Assert.Equal("old", File.ReadAllText(path + ".bak2"));
    }

    [Fact]
    public void Rotate_ZeroGenerations_NoOp()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(dir, "s.json");
        File.WriteAllText(path, "new");

        SaveBackups.Rotate(path, 0);

        Assert.False(File.Exists(path + ".bak1"));
    }

    [Fact]
    public void Rotate_MissingPrimary_NoOp()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(dir, "s.json");

        SaveBackups.Rotate(path, 2);

        Assert.False(File.Exists(path + ".bak1"));
        Assert.False(File.Exists(path + ".bak2"));
    }

    [Fact]
    public void Rotate_DropsOldestBeyondLimit()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(dir, "s.json");
        File.WriteAllText(path, "newest");
        File.WriteAllText(path + ".bak1", "mid");
        File.WriteAllText(path + ".bak2", "oldest");

        SaveBackups.Rotate(path, 2);

        Assert.Equal("newest", File.ReadAllText(path));
        Assert.Equal("newest", File.ReadAllText(path + ".bak1"));
        Assert.Equal("mid", File.ReadAllText(path + ".bak2"));
    }
}
