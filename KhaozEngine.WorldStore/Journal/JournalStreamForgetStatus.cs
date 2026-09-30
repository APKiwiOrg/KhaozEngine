namespace KhaozEngine.WorldStore.Journal;

/// <summary>The result of requesting removal of one stream's admitted view.</summary>
public enum JournalStreamForgetStatus
{
    /// <summary>The executor holds no admitted view for the stream.</summary>
    Unknown,

    /// <summary>The empty admitted view was removed immediately.</summary>
    Forgotten,

    /// <summary>The view will be removed after its final acknowledgement unless the stream is seeded again.</summary>
    Deferred,
}
