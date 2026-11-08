using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    internal ContactDescentEvidence? DescentEvidence
    {
        get => Workspace.DescentEvidence;
        set { AssertCurrent(); Workspace.DescentEvidence = value; }
    }
}
