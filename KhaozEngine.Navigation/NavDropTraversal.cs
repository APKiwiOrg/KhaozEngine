using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>A directed, certified unpowered fall. Layer and cell identities distinguish stacked support.
/// The host aligns on the physical source, supplies ordinary movement without jump input and acknowledges
/// completion only after its controller proves supported, non-swimming arrival at the named landing.</summary>
/// <param name="Link">The baked directed Drop, including source and landing layer and cell identity.</param>
/// <param name="StartFeet">The admitted source's physical resting feet in world metres.</param>
/// <param name="LandingFeet">The admitted landing's physical resting feet in world metres.</param>
public readonly record struct NavDropTraversal(NavLink Link, Vector3 StartFeet, Vector3 LandingFeet);
