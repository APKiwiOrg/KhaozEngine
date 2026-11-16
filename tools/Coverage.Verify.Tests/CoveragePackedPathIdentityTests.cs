using System;
using System.Diagnostics;
using System.IO;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoveragePackedPathIdentityTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "khaozengine-coverage-packed-input", "identity-" + Guid.NewGuid().ToString("N"));

    public CoveragePackedPathIdentityTests()
    {
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void Hard_links_are_one_destination_and_byte_equal_copies_are_not()
    {
        string original = Path.Combine(folder, "manifest.json"), copy = Path.Combine(folder, "copy.json");
        File.WriteAllText(original, "input");
        File.Copy(original, copy);
        string link = CoveragePathSpelling.Create(folder, "manifest.json", "hard");

        Assert.True(CoveragePathIdentity.SameDestination(original, link));
        Assert.False(CoveragePathIdentity.SameDestination(original, copy));
    }

    // Each spelling reaches the folder through something only the OS resolves. A link whose target contains dot
    // segments names a directory on disk that a lexical resolver would place elsewhere.
    [DirectoryLinkTheory]
    [InlineData("linked")]
    [InlineData("dot-segment")]
    [InlineData("temp-alias")]
    public void Existing_and_absent_destinations_resolve_through_the_os(string spelling)
    {
        File.WriteAllText(Path.Combine(folder, "input.bin"), "input");
        string other = Path.Combine(folder, "elsewhere");
        Directory.CreateDirectory(other);

        Assert.True(CoveragePathIdentity.SameDestination(Path.Combine(folder, "input.bin"), CoveragePathSpelling.Create(folder, "input.bin", spelling)));
        Assert.True(CoveragePathIdentity.SameDestination(Path.Combine(folder, "absent.bin"), CoveragePathSpelling.Create(folder, "absent.bin", spelling)));
        // The same final name in a physically distinct directory stays a distinct destination.
        Assert.False(CoveragePathIdentity.SameDestination(Path.Combine(other, "absent.bin"), CoveragePathSpelling.Create(folder, "absent.bin", spelling)));
        Assert.False(CoveragePathIdentity.SameDestination(Path.Combine(folder, "input.bin"), Path.Combine(folder, "absent.bin")));
    }

    // A volume may fold case and Unicode composition, so absent names it could merge are one destination.
    [Theory]
    [InlineData("table.bin", "TABLE.BIN", true)]
    [InlineData("café.json", "café.json", true)]
    [InlineData("table.bin", "table.json", false)]
    public void Absent_names_a_folding_volume_could_merge_are_one_destination(string left, string right, bool same)
        => Assert.Equal(same, CoveragePathIdentity.SameDestination(Path.Combine(folder, left), Path.Combine(folder, right)));

    // The C library decides which stat entry point exists: stat on glibc 2.33 and later, musl and macOS, only
    // __xstat on older glibc. A library or entry point the runtime cannot bind is an unidentifiable destination,
    // never an untyped crash. Found stat is the passing control.
    [Theory]
    [InlineData("Stat", "Stat")]
    [InlineData("XStatOnly", "XStat")]
    [InlineData("Neither", null)]
    [InlineData("LoadFailed", null)]
    public void Stat_binding_follows_the_exports_and_refuses_when_none_binds(string lookup, string? binding)
    {
        var found = System.Enum.Parse<CoverageStatLookup>(lookup);
        if (binding != null)
        {
            Assert.Equal(System.Enum.Parse<CoverageStatBinding>(binding), CoveragePathIdentity.SelectStatBinding(found));
            return;
        }
        var error = Assert.Throws<CoverageBuildValidationException>(() => CoveragePathIdentity.SelectStatBinding(found));
        Assert.Equal(CoverageBuildValidationRefusal.OutputAlias, error.Reason);
    }

    [DirectoryLinkTheory]
    [InlineData("dangling", CoverageBuildValidationRefusal.OutputAlias)]
    [InlineData("loop", CoverageBuildValidationRefusal.OutputAlias)]
    [InlineData("missing-parent", CoverageBuildValidationRefusal.OutputIo)]
    public void Destinations_the_os_cannot_identify_are_refused(string defect, CoverageBuildValidationRefusal reason)
    {
        string control = Path.Combine(folder, "control.bin"), path = Path.Combine(folder, "entry.bin");
        Assert.True(CoveragePathIdentity.SameDestination(control, control));
        if (defect == "dangling") File.CreateSymbolicLink(path, "missing.bin");
        if (defect == "loop") { File.CreateSymbolicLink(path, "back.bin"); File.CreateSymbolicLink(Path.Combine(folder, "back.bin"), "entry.bin"); }
        if (defect == "missing-parent") path = Path.Combine(folder, "missing", "entry.bin");

        var error = Assert.Throws<CoverageBuildValidationException>(() => CoveragePathIdentity.SameDestination(control, path));
        Assert.Equal(reason, error.Reason);
    }
}

/// <summary>Spells an entry of a folder through a link, a link whose target contains dot segments, the system temporary
/// directory alias, or a hard link. Returns the new spelling.</summary>
internal static class CoveragePathSpelling
{
    internal static string Create(string folder, string name, string spelling)
    {
        string unique = Guid.NewGuid().ToString("N")[..8];
        switch (spelling)
        {
            case "linked":
                string link = Path.Combine(folder, "linked-" + unique);
                Directory.CreateSymbolicLink(link, folder);
                return Path.Combine(link, name);
            case "dot-segment":
                // On disk hop names real/inner, so the alias target hop/../.. names the folder. Lexically it
                // would name the folder's parent.
                Directory.CreateDirectory(Path.Combine(folder, "real-" + unique, "inner"));
                Directory.CreateSymbolicLink(Path.Combine(folder, "hop-" + unique), Path.Combine("real-" + unique, "inner"));
                string alias = Path.Combine(folder, "alias-" + unique);
                Directory.CreateSymbolicLink(alias, Path.Combine("hop-" + unique, "..", ".."));
                return Path.Combine(alias, name);
            case "temp-alias":
                // macOS spells /tmp and /var through /private. Elsewhere a linked spelling stands in.
                string full = Path.GetFullPath(folder);
                if (OperatingSystem.IsMacOS() && (full.StartsWith("/tmp/", StringComparison.Ordinal) || full.StartsWith("/var/", StringComparison.Ordinal)))
                    return Path.Combine("/private" + full, name);
                if (OperatingSystem.IsMacOS() && full.StartsWith("/private/", StringComparison.Ordinal))
                    return Path.Combine(full["/private".Length..], name);
                return Create(folder, name, "linked");
            case "hard":
                string hard = Path.Combine(folder, "hard-" + unique + "-" + name);
                HardLink(Path.Combine(folder, name), hard);
                return hard;
            default: throw new ArgumentOutOfRangeException(nameof(spelling));
        }
    }

    static void HardLink(string existing, string link)
    {
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe") : new ProcessStartInfo("ln");
        if (OperatingSystem.IsWindows()) foreach (string arg in new[] { "/c", "mklink", "/H", link, existing }) start.ArgumentList.Add(arg);
        else { start.ArgumentList.Add(existing); start.ArgumentList.Add(link); }
        start.UseShellExecute = false; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.Exists(link));
    }
}
