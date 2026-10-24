namespace KhaozEngine.Render3D;

/// <summary>How diffuse surfaces receive indirect light.</summary>
public enum IndirectDiffuseMode
{
    /// <summary>The same ambient irradiance in every direction. The default.</summary>
    Uniform,

    /// <summary>Directional irradiance evaluated from spherical harmonics.</summary>
    Directional,
}
