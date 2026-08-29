using KhaozEngine.Updates;

namespace KhaozEngine.Tests.Updates;

/// <summary>Mutable IUpdateStatus test double.</summary>
public sealed class FakeUpdateStatus : IUpdateStatus
{
    public UpdateState State { get; set; } = UpdateState.Idle;
    public string? RemoteVersion { get; set; }
    public int FilesDownloaded { get; set; }
    public int TotalFilesToDownload { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalDownloadBytes { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsRequired { get; set; }
    public bool ApplyAttemptsExhausted { get; set; }
}
