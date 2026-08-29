using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Ecs;

public sealed partial class World
{
    private readonly Dictionary<int, List<Entity>> _children = new();   // parentId -> children
    private static readonly List<Entity> _noChildren = new();

    /// <summary>Makes <paramref name="child"/> a child of <paramref name="parent"/> (re-parenting if needed). Throws on self-parent, a dead parent, or a cycle.</summary>
    public void SetParent(Entity child, Entity parent)
    {
        if (!IsAlive(child)) throw new InvalidOperationException("Stale entity handle.");
        if (!IsAlive(parent)) throw new ArgumentException("Parent is not alive.", nameof(parent));
        if (child.Equals(parent)) throw new ArgumentException("An entity cannot be its own parent.");

        for (Entity? a = parent; a is Entity cur; a = GetParent(cur))
            if (cur.Equals(child))
                throw new InvalidOperationException("SetParent would create a cycle.");

        DetachFromParentIndex(child);                       // leave the old parent's list (if any)
        Set(child, new Parent { Value = parent });          // overwrite the link (change-tracked)
        AddToParentIndex(parent, child);
    }

    /// <summary>Detaches <paramref name="child"/> from its parent, making it a root. No-op if already a root.</summary>
    public void Detach(Entity child)
    {
        if (!Has<Parent>(child)) return;
        DetachFromParentIndex(child);
        Remove<Parent>(child);
    }

    /// <summary>The entity's parent, or null if it is a root.</summary>
    public Entity? GetParent(Entity child) => TryGet<Parent>(child, out Parent p) ? p.Value : null;

    /// <summary>The entity's children (empty if none). The returned list is a live, read-only view.</summary>
    public IReadOnlyList<Entity> Children(Entity parent) =>
        _children.TryGetValue(parent.Id, out List<Entity>? list) ? list : _noChildren;

    /// <summary>Despawns <paramref name="e"/> and all of its descendants (post-order).</summary>
    public void DespawnTree(Entity e)
    {
        if (!IsAlive(e)) return;
        var order = new List<Entity>();
        CollectPostOrder(e, order);
        foreach (Entity node in order) Despawn(node);
    }

    private void CollectPostOrder(Entity e, List<Entity> order)
    {
        if (_children.TryGetValue(e.Id, out List<Entity>? kids))
            foreach (Entity c in kids.ToArray())            // copy: Despawn mutates the index
                CollectPostOrder(c, order);
        order.Add(e);
    }

    private void AddToParentIndex(Entity parent, Entity child)
    {
        if (!_children.TryGetValue(parent.Id, out List<Entity>? list))
        {
            list = new List<Entity>();
            _children[parent.Id] = list;
        }
        list.Add(child);
    }

    private void DetachFromParentIndex(Entity child)
    {
        if (TryGet<Parent>(child, out Parent p) && _children.TryGetValue(p.Value.Id, out List<Entity>? list))
            list.Remove(child);
    }

    /// <summary>Called by <see cref="Despawn"/>: orphan e's children (clear their Parent) and unlink e from its own parent.</summary>
    internal void DetachHierarchyOnDespawn(Entity e)
    {
        DetachFromParentIndex(e);                            // remove e from its parent's children list
        if (_children.TryGetValue(e.Id, out List<Entity>? kids))
        {
            foreach (Entity c in kids.ToArray())
                if (Has<Parent>(c)) Remove<Parent>(c);       // orphan to root
            _children.Remove(e.Id);
        }
    }

    /// <summary>Rebuilds the children index from Parent components (after a load).</summary>
    /// <remarks>
    /// A child whose recorded parent is not alive is dropped to a root instead of being indexed. The index is keyed
    /// by the bare parent id, so filing a stale handle (save data that is hand-edited, corrupt, or points at a since
    /// recycled slot) would hang the child off whatever live entity now holds that id, and
    /// <see cref="DespawnTree"/> would then take it out along with a tree it never belonged to.
    /// </remarks>
    internal void RebuildHierarchyIndex()
    {
        _children.Clear();
        List<Entity>? orphans = null;                       // stays null on well-formed data: no allocation
        foreach (Entity child in Query().With<Parent>().Entities())
        {
            Entity parent = Get<Parent>(child).Value;
            if (IsAlive(parent)) AddToParentIndex(parent, child);
            else (orphans ??= new List<Entity>()).Add(child);
        }
        if (orphans is null) return;
        foreach (Entity child in orphans)                   // after the walk: Remove is a structural change
            Remove<Parent>(child);
    }
}
