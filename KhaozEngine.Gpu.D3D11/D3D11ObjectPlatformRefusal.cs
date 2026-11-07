namespace KhaozEngine.Gpu.D3D11;

/// <summary>The rules of the D3D11ObjectPlatform refusal family.</summary>
public enum D3D11ObjectPlatformRefusal
{
    /// <summary>Refuses use of a Windows-only native D3D11 object on another operating system.</summary>
    WindowsObjectUsedOffWindows,
}
