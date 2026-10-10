using System.Runtime.CompilerServices;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>The bounded set of statics and contact classes met by one shell sweep. A lifted route may only
/// shorten a move on obstructions also met by the standing route.</summary>
internal struct ShellObstructions
{
    // One initial sweep plus four slides. Kept inline so contact retries do not allocate.
    [InlineArray(5)]
    struct Buffer
    {
        Obstruction _first;
    }

    readonly record struct Obstruction(StaticHandle? Static, ContactClass Class);
    Buffer _items;
    int _count;

    internal void Add(StaticHandle? target, ContactClass contact) => _items[_count++] = new(target, contact);

    internal readonly bool Includes(in ShellObstructions other)
    {
        for (int i = 0; i < other._count; i++)
        {
            bool found = false;
            for (int j = 0; j < _count; j++)
                if (_items[j] == other._items[i]) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }
}
