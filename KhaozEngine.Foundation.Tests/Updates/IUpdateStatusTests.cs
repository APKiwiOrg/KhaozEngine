using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

public sealed class IUpdateStatusTests
{
    [Fact]
    public void UpdateService_implements_IUpdateStatus() =>
        Assert.True(typeof(IUpdateStatus).IsAssignableFrom(typeof(UpdateService)));

    [Fact]
    public void Fake_double_carries_progress()
    {
        IUpdateStatus s = new FakeUpdateStatus { State = UpdateState.Downloading, FilesDownloaded = 2 };
        Assert.Equal(UpdateState.Downloading, s.State);
        Assert.Equal(2, s.FilesDownloaded);
    }
}
