using System.Runtime.CompilerServices;
using KhaozEngine.NetWorld;

// The ban seam and the admin seam physically live in KhaozEngine.Netcode, so the connect gate and the tile server can
// take the first and the tile server can implement the second without referencing this assembly. The full type names
// are unchanged (namespace stays KhaozEngine.NetWorld, only the assembly differs), so TypeForwardedTo bridges them: an
// assembly built against an earlier KhaozEngine.NetWorld keeps binding all of them, and source that names them through
// this namespace keeps compiling with no change.
[assembly: TypeForwardedTo(typeof(IBanStore))]
[assembly: TypeForwardedTo(typeof(BanRecord))]
[assembly: TypeForwardedTo(typeof(InMemoryBanStore))]
[assembly: TypeForwardedTo(typeof(IAdminControllable))]
[assembly: TypeForwardedTo(typeof(PlayerRef))]
[assembly: TypeForwardedTo(typeof(OnlinePlayer))]
[assembly: TypeForwardedTo(typeof(MovementCommitmentRequest))]
