using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Pure classification of selected timed substeps. A speculative route never commits an owner.</summary>
internal abstract class ContactMediumPolicy
{
    internal abstract bool AllowsFooting(Vector3 feet, in SupportSample support);
    internal abstract bool ChangesOwner(Vector3 feet, float verticalVelocity, in SupportSample support, bool grounded);
}
