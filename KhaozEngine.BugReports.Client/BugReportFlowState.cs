namespace KhaozEngine.BugReports.Client;

/// <summary>Where <see cref="BugReportFlow"/> stands between frames.</summary>
internal enum BugReportFlowState
{
    /// <summary>No report is open. The trigger or <see cref="BugReportFlow.Begin"/> starts one.</summary>
    Idle,

    /// <summary>
    /// The trigger frame captured the report. The screen opens once the screenshot is taken or failed, at most three
    /// frames later. Input stays held meanwhile.
    /// </summary>
    Captured,

    /// <summary>The confirmation screen is open until Send or Cancel.</summary>
    Open,
}
