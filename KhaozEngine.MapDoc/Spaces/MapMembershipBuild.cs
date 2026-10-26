using System;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Construction-only capacity attribution. Frozen queries never enter this context.</summary>
internal sealed class MapMembershipBuild : IDisposable
{
    [ThreadStatic] static MapMembershipBuild? t_current;
    readonly MapMembershipBuild? _previous;
    readonly MapSceneAdmission _admission;
    readonly MapPreparationScope _product;
    internal readonly MapRoofPreparationCounts Counts;
    internal static MapMembershipBuild? Current => t_current;
    internal MapMembershipBuild(MapSceneAdmission admission, MapPreparationScope product, MapRoofPreparationCounts counts)
    { _admission = admission; _product = product; Counts = counts; _previous = t_current; t_current = this; }
    internal static void Inventory(long bytes)
    {
        if (t_current is { } build) build._admission.BeforeGeometry(bytes, build._product.WorkspaceOwner);
    }
    internal static void ReleaseInventory(long bytes)
    {
        if (t_current is { } build && bytes != 0) build._product.WorkspaceOwner.Release(bytes);
    }
    internal static MapPreparationReservation? Kernel(long bytes = 32768)
    {
        if (t_current is not { } build) return null;
        var owner = build._admission.Workspace();
        try { build._admission.BeforeGeometry(bytes, owner); return owner; }
        catch { owner.Dispose(); throw; }
    }
    internal static void Coefficient(int bytes)
    {
        if (t_current is { } build)
        { build.Counts.Coefficients++; build.Counts.NormalizedCoefficientBytes += bytes; }
    }
    public void Dispose() => t_current = _previous;
}
