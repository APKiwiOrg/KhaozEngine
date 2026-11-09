using System.Runtime.CompilerServices;

namespace KhaozEngine.MapDoc;

/// <summary>Opaque test observation tokens with no path back to the captured owner.</summary>
internal static class MapTestObservationIdentity
{
    static readonly ConditionalWeakTable<object, object> Tokens = new();
    internal static object For(object owner) => Tokens.GetValue(owner, static _ => new object());
}
