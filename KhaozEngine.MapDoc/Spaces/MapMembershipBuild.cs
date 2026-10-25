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
    internal static MapPreparationReservation? Kernel(long bytes = 32768)
    {
        var owner = t_current?._admission.Workspace();
        owner?.Reserve(bytes);
        return owner;
    }
    internal static void Coefficient(int bytes)
    {
        Inventory(checked(48L + bytes));
        if (t_current is { } build) build.Counts.NormalizedCoefficientBytes += bytes;
    }
    public void Dispose() => t_current = _previous;
}
