using System;
using System.Threading;

namespace KhaozEngine.Diagnostics;

/// <summary>
/// One ambient slot shared by facade readers. Immutable nodes are inherited, while replacing the head only
/// changes the current execution context. Null values remain distinct from an absent override.
/// </summary>
internal sealed class AmbientScope<T>
{
    private readonly AsyncLocal<Node?> current = new();
    private bool used;

    /// <summary>
    /// Reads never consult the execution context until this slot has been used. Keep the flag monotonic,
    /// because other flows may still hold nodes after the calling flow has restored its fallback.
    /// The fallback is a value, not a delegate, so facade reads do not allocate closures.
    /// </summary>
    internal T GetValueOrDefault(T fallback)
    {
        if (!Volatile.Read(ref used)) return fallback;
        var node = current.Value;
        return node is null ? fallback : node.Value;
    }

    internal IDisposable Use(T value)
    {
        Volatile.Write(ref used, true);
        var node = new Node(this, current.Value, value);
        current.Value = node;
        return node;
    }

    private void Pop(Node node)
    {
        var head = current.Value;
        if (ReferenceEquals(head, node))
        {
            current.Value = node.Parent;
            return;
        }

        for (var ancestor = head; ancestor is not null; ancestor = ancestor.Parent)
            if (ReferenceEquals(ancestor, node))
                throw new InvalidOperationException("Dispose ambient scopes in reverse creation order.");

        // Already absent in this flow. A shared disposed flag would also poison inherited copies in other
        // execution contexts, preventing them from restoring their own parent.
    }

    private sealed class Node(AmbientScope<T> owner, Node? parent, T value) : IDisposable
    {
        private readonly AmbientScope<T> owner = owner;
        internal readonly Node? Parent = parent;
        internal readonly T Value = value;

        public void Dispose() => owner.Pop(this);
    }
}
