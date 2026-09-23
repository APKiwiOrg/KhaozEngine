using System.Runtime.CompilerServices;
using KhaozEngine.NetWorld;

// The ban seam physically lives in KhaozEngine.Netcode so the connect gate and the tile server can take it. The
// full type names are unchanged (namespace stays KhaozEngine.NetWorld, only the assembly differs), so
// TypeForwardedTo bridges it: an assembly built against an earlier KhaozEngine.NetWorld keeps binding all three,
// and source that names them through this namespace keeps compiling with no change.
[assembly: TypeForwardedTo(typeof(IBanStore))]
[assembly: TypeForwardedTo(typeof(BanRecord))]
[assembly: TypeForwardedTo(typeof(InMemoryBanStore))]
