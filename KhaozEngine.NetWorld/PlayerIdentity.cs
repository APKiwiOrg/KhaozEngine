using KhaozEngine.Ecs;

namespace KhaozEngine.NetWorld;

/// <summary>
/// A player entity's replicated human display name (e.g. "Daniel") - the cosmetic label a client renders above the
/// avatar. Deliberately SEPARATE from the account identity (<see cref="WorldServer.TryGetAccountId"/> / the verified
/// token subject used for persistence): the display name is additive, client-cosmetic, and never assumed to equal the
/// account id. Registered in <see cref="MoveProtocol.CreateRegistry"/> as type id
/// <see cref="MoveProtocol.IdentityTypeId"/> with a length-prefixed UTF-8 encoding capped at
/// <see cref="MoveProtocol.MaxDisplayNameBytes"/> bytes and NOT interpolated (a string does not blend). Set it
/// server-side via <see cref="WorldServer.SetPlayerDisplayName"/> / <see cref="ShardedWorldServer.SetPlayerDisplayName"/>
/// (or carry it on a <see cref="KhaozEngine.Netcode.SignedToken"/> display-name claim, auto-applied on join); read it
/// client-side off <see cref="EntityRenderState.DisplayName"/>.
/// </summary>
public struct PlayerIdentity : IComponent
{
    /// <summary>The human display name. Empty/null renders no label.</summary>
    public string DisplayName;
}
