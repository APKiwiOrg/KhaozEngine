using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>What a contact means to the body. <see cref="Support"/> and <see cref="SteepSupport"/> come from foot
/// support. The rest come from the shell. <see cref="RisingSupport"/> is a walkable face met above the step
/// height.</summary>
internal enum ContactClass : byte { Support, SteepSupport, RisingSupport, Wall, Ceiling }

/// <summary>Maps support results and shell contact normals to contact classes.</summary>
internal static class ContactClassifier
{
    /// <summary>The class of a foot support result, or null when there is no trusted support.</summary>
    internal static ContactClass? ClassifySupport(SupportStatus status) => status switch
    {
        SupportStatus.Walkable => ContactClass.Support,
        SupportStatus.Steep => ContactClass.SteepSupport,
        _ => null,
    };

    /// <summary>The class of a shell contact by its normal. A normal at or above the slope gate is walkable, the
    /// same gate foot support uses. A downward normal is a ceiling. Anything between is a wall.</summary>
    internal static ContactClass ClassifyShell(Vector3 contactNormal, float cosMaxSlope)
    {
        if (contactNormal.Y >= cosMaxSlope)
            return ContactClass.RisingSupport;
        return contactNormal.Y < 0f ? ContactClass.Ceiling : ContactClass.Wall;
    }
}
