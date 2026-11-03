using System.Runtime.CompilerServices;

namespace KhaozEngine.Render3D;

/// <summary>Opaque reference identities for test observations, with no path back to the observed object.</summary>
internal static class TestObservationIdentity
{
    static readonly ConditionalWeakTable<object, object> Tokens = new();

    internal static object For(object value) => Tokens.GetValue(value, static _ => new object());
}
