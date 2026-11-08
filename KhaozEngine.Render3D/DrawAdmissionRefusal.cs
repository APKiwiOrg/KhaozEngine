namespace KhaozEngine.Render3D;

/// <summary>The stable reasons DrawAdmission input or access is refused.</summary>
public enum DrawAdmissionRefusal
{
    /// <summary>Refuses a shadow-only draw that casts no shadows.</summary>
    ShadowOnlyWithoutCasting,
}
