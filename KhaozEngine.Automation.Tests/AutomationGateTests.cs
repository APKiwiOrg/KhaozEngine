using System;
using System.IO;
using System.Reflection;
using KhaozEngine.Automation;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Gate 2: the host stays completely inert unless the head opted in AND <c>KE_AUTOMATION</c> reads <c>1</c>. Inert
/// means no thread, no socket and no handshake file, not a listener that refuses.
/// <para>
/// In the <c>AutomationEnvironment</c> collection because these write the process-global environment variable.
/// </para>
/// </summary>
[Collection("AutomationEnvironment")]
public class AutomationGateTests
{
    [Fact]
    public void WithTheEnvironmentVariableUnsetTheHostBindsNothingAndWritesNoFile()
    {
        using var temp = new TempDirectory();
        using var environment = new EnvironmentScope(AutomationHost.EnvironmentVariable, null);
        using var host = new AutomationHost(new AutomationOptions(Enabled: true, temp.Path));

        host.Start();

        AssertInert(host, temp);
    }

    [Fact]
    public void WithTheEnvironmentVariableSetButEnabledFalseTheHostStaysInert()
    {
        using var temp = new TempDirectory();
        using var environment = new EnvironmentScope(AutomationHost.EnvironmentVariable, "1");
        using var host = new AutomationHost(new AutomationOptions(Enabled: false, temp.Path));

        host.Start();

        AssertInert(host, temp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData(" 1")]
    public void OnlyTheExactValueOneArmsTheEnvironmentGate(string value)
    {
        using var temp = new TempDirectory();
        using var environment = new EnvironmentScope(AutomationHost.EnvironmentVariable, value);
        using var host = new AutomationHost(new AutomationOptions(Enabled: true, temp.Path));

        host.Start();

        AssertInert(host, temp);
    }

    [Fact]
    public void WithBothGatesArmedTheHostBindsLoopbackAndWritesTheHandshakeFile()
    {
        using var temp = new TempDirectory();
        using var environment = new EnvironmentScope(AutomationHost.EnvironmentVariable, "1");
        using var host = new AutomationHost(new AutomationOptions(Enabled: true, temp.Path));

        host.Start();

        Assert.True(host.IsRunning);
        Assert.True(host.Port > 0);
        Assert.NotNull(host.Token);
        Assert.True(File.Exists(host.HandshakeFilePath));

        System.Text.Json.JsonElement handshake = AutomationTestKit.Json(File.ReadAllText(host.HandshakeFilePath));
        Assert.Equal(host.Port, handshake.GetProperty("port").GetInt32());
        Assert.Equal(host.Token, handshake.GetProperty("token").GetString());
        Assert.Equal(AutomationHandshake.CurrentProcessId, handshake.GetProperty("pid").GetInt32());
        Assert.NotNull(handshake.GetProperty("startedAt").GetString());
    }

    [Fact]
    public void DisposeDeletesTheHandshakeFile()
    {
        using var temp = new TempDirectory();
        using var environment = new EnvironmentScope(AutomationHost.EnvironmentVariable, "1");
        var host = new AutomationHost(new AutomationOptions(Enabled: true, temp.Path));
        host.Start();
        string path = host.HandshakeFilePath;
        Assert.True(File.Exists(path));

        host.Dispose();

        Assert.False(File.Exists(path));
        Assert.False(host.IsRunning);
    }

    [Fact]
    public void TheHandshakeFileIsOwnerOnlyWhereThePlatformAllows()
    {
        if (System.OperatingSystem.IsWindows()) return;    // no one-call POSIX mode there, the directory ACL governs

        using var temp = new TempDirectory();
        using var environment = new EnvironmentScope(AutomationHost.EnvironmentVariable, "1");
        using var host = new AutomationHost(new AutomationOptions(Enabled: true, temp.Path));

        host.Start();

        UnixFileMode mode = File.GetUnixFileMode(host.HandshakeFilePath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void StartHooksTheProcessExitCleanupAndDisposeTakesItBackOff()
    {
        // The ordinary shutdown of a game is quit to AppWindow.Close to Run returning, and a head written from the
        // wiring example disposes nothing on that path, so without the hook every run left a file naming a dead
        // port. Asserted through the subscription and by invoking the handler, rather than by exiting the test run.
        using var temp = new TempDirectory();
        using var environment = new EnvironmentScope(AutomationHost.EnvironmentVariable, "1");
        var host = new AutomationHost(new AutomationOptions(Enabled: true, temp.Path));
        Assert.False(host.HasProcessExitHandler);

        host.Start();
        Assert.True(host.HasProcessExitHandler);

        string path = host.HandshakeFilePath;
        var handler = (EventHandler)typeof(AutomationHost)
            .GetField("_processExit", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host)!;
        handler(null, EventArgs.Empty);
        Assert.False(File.Exists(path));                   // what the hook is for: no stale file after a bare exit

        host.Dispose();
        Assert.False(host.HasProcessExitHandler);
    }

    [Fact]
    public void AStaleWorldReadableHandshakeFileDoesNotKeepItsMode()
    {
        if (System.OperatingSystem.IsWindows()) return;    // the directory ACL governs there, not a POSIX mode

        using var temp = new TempDirectory();
        string path = System.IO.Path.Combine(temp.Path, AutomationHost.HandshakeFileName);
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        AutomationHandshake.Write(path, 51234, "a-token", 4711, System.DateTimeOffset.UtcNow);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    static void AssertInert(AutomationHost host, TempDirectory temp)
    {
        Assert.False(host.IsRunning);
        Assert.Equal(0, host.Port);
        Assert.Null(host.Token);
        Assert.False(host.HasProcessExitHandler);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }
}
