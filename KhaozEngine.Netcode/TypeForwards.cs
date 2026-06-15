using System.Runtime.CompilerServices;
using KhaozEngine.Netcode;

// The channel-split contract moved to the zero-dependency KhaozEngine.Netcode.Abstractions assembly
// in 4.9.0 so MonoGame-free, transport-agnostic DTO projects can implement it. The full type names
// are unchanged (namespace stays KhaozEngine.Netcode, only the assembly moved), so TypeForwardedTo
// bridges it: anyone referencing KhaozEngine.Netcode keeps compiling and binding both types with no
// source change. (Unlike the 4.8.0 namespace move, which forwards could not bridge.)
[assembly: TypeForwardedTo(typeof(IChannelSplittable<>))]
[assembly: TypeForwardedTo(typeof(NetChannelReliability))]
