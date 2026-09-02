using System.IO;
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

    static void AssertInert(AutomationHost host, TempDirectory temp)
    {
        Assert.False(host.IsRunning);
        Assert.Equal(0, host.Port);
        Assert.Null(host.Token);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }
}
