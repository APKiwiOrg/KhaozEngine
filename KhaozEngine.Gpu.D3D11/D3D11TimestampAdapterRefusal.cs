namespace KhaozEngine.Gpu.D3D11;

/// <summary>Rules that refuse D3D11TimestampAdapter operations.</summary>
public enum D3D11TimestampAdapterRefusal
{
    /// <summary>Refuses a timestamp query read that returns a failed HRESULT.</summary>
    QueryReadFailed,
}
