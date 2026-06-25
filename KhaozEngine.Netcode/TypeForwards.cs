using System.Runtime.CompilerServices;
using KhaozEngine.Netcode;

// The channel-split contract physically lives in the zero-dependency KhaozEngine.Netcode.Abstractions
// assembly so transport-agnostic DTO projects can implement it. The full type names are unchanged
// (namespace stays KhaozEngine.Netcode, only the assembly differs), so TypeForwardedTo bridges it:
// anyone referencing KhaozEngine.Netcode keeps compiling and binding both types with no source change.
[assembly: TypeForwardedTo(typeof(IChannelSplittable<>))]
[assembly: TypeForwardedTo(typeof(NetChannelReliability))]
