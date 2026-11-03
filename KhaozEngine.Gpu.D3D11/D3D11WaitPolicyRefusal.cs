namespace KhaozEngine.Gpu.D3D11;

/// <summary>Rules that refuse D3D11WaitPolicy operations.</summary>
public enum D3D11WaitPolicyRefusal
{
    /// <summary>Refuses a D3D11 drain or ring rotation while the caller holds the submit lock.</summary>
    SubmitLockHeld,
}
