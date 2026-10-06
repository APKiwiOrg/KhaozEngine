using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuUtilities;
using BepuUtilities.Memory;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Accumulates source convex leaves without final nonconvex manifold reduction.</summary>
internal sealed class CapsuleContactCollector
{
    const int MaximumChildren = 4096;
    const int MaximumContacts = 16384;
    readonly List<Pair> _pairs = new();
    readonly List<CapsuleContact> _contacts = new();
    readonly List<(Pair Owner, int ChildIndex)> _leaves = new();

    internal sealed class Pair
    {
        public TypedIndex Shape;
        public RigidPose Pose;
        public int Handle;
        public bool Dynamic;
        public readonly List<Child> Children = new();
        public readonly List<int> MeshTriangles = new();
        public BoundingBox MeshBounds;
    }

    internal readonly record struct Child(int Index, ConvexContactManifold Manifold);

    public int PairCount { get; private set; }
    public int ContactCount => _contacts.Count;
    public bool Valid { get; private set; }
    public float Margin { get; set; }
    public Pair PairAt(int index) => _pairs[index];

    public void Reset()
    {
        PairCount = 0;
        _leaves.Clear();
        _contacts.Clear();
        Valid = true;
    }

    public void AddPair(TypedIndex shape, RigidPose pose, int handle, bool dynamic)
    {
        if (_pairs.Count == PairCount) _pairs.Add(new Pair());
        Pair pair = _pairs[PairCount++];
        pair.Shape = shape;
        pair.Pose = pose;
        pair.Handle = handle;
        pair.Dynamic = dynamic;
        pair.Children.Clear();
        pair.MeshTriangles.Clear();
    }

    public int AddLeaf(Pair owner, int childIndex)
    {
        if (_leaves.Count == MaximumChildren)
        {
            Valid = false;
            return -1;
        }
        int id = _leaves.Count;
        _leaves.Add((owner, childIndex));
        return id;
    }

    void AddManifold<TManifold>(Pair pair, int childIndex, ref TManifold manifold)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        for (int i = 0; i < manifold.Count && Valid; i++)
        {
            manifold.GetContact(i, out _, out Vector3 normal, out float depth, out int feature);
            float separation = -depth;
            if (!float.IsFinite(separation) || !float.IsFinite(normal.LengthSquared()) ||
                MathF.Abs(normal.LengthSquared() - 1f) > 0.0001f)
            {
                Valid = false;
                return;
            }
            if (separation > Margin) continue;
            if (_contacts.Count == MaximumContacts)
            {
                Valid = false;
                return;
            }
            _contacts.Add(new CapsuleContact(-normal, separation, pair.Dynamic, pair.Handle, childIndex, feature));
        }
    }

    public void FinishChildren(Shapes shapes, BufferPool pool)
    {
        for (int i = 0; i < PairCount && Valid; i++)
        {
            Pair pair = _pairs[i];
            if (pair.Shape.Type == default(Mesh).TypeId)
            {
                if (!CapsuleMeshContacts.Smooth(shapes, pool, pair))
                {
                    Valid = false;
                    return;
                }
            }
            foreach (Child child in pair.Children)
            {
                ConvexContactManifold manifold = child.Manifold;
                AddManifold(pair, child.Index, ref manifold);
            }
        }
    }

    public void SortAndDeduplicate()
    {
        _contacts.Sort(ContactOrder.Instance);
        int written = 0;
        for (int i = 0; i < _contacts.Count; i++)
        {
            CapsuleContact contact = _contacts[i];
            if (written > 0)
            {
                CapsuleContact previous = _contacts[written - 1];
                if ((previous with { FeatureId = contact.FeatureId }) == contact) continue;
            }
            _contacts[written++] = contact;
        }
        if (written < _contacts.Count) _contacts.RemoveRange(written, _contacts.Count - written);
    }

    public void CopyTo(Span<CapsuleContact> destination)
    {
        for (int i = 0; i < _contacts.Count; i++) destination[i] = _contacts[i];
    }

    sealed class ContactOrder : IComparer<CapsuleContact>
    {
        public static readonly ContactOrder Instance = new();

        public int Compare(CapsuleContact x, CapsuleContact y)
        {
            int c = x.Normal.X.CompareTo(y.Normal.X);
            if (c == 0) c = x.Normal.Y.CompareTo(y.Normal.Y);
            if (c == 0) c = x.Normal.Z.CompareTo(y.Normal.Z);
            if (c == 0) c = x.Separation.CompareTo(y.Separation);
            if (c == 0) c = x.Dynamic.CompareTo(y.Dynamic);
            if (c == 0) c = x.BodyHandle.CompareTo(y.BodyHandle);
            if (c == 0) c = x.ChildIndex.CompareTo(y.ChildIndex);
            return c != 0 ? c : x.FeatureId.CompareTo(y.FeatureId);
        }
    }

    public readonly struct Callbacks(CapsuleContactCollector owner) : ICollisionCallbacks
    {
        // All submitted pairs are convex leaves. Receiving a nested child would break provenance and
        // completeness, so fail closed rather than accepting an unaccounted reduction path.
        public bool AllowCollisionTesting(int pairId, int childA, int childB)
        {
            owner.Valid = false;
            return false;
        }

        public void OnChildPairCompleted(int pairId, int childA, int childB, ref ConvexContactManifold manifold)
            => owner.Valid = false;

        public void OnPairCompleted<TManifold>(int pairId, ref TManifold manifold)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            var leaf = owner._leaves[pairId];
            if (leaf.Owner.Shape.Type == default(Mesh).TypeId)
            {
                if (typeof(TManifold) != typeof(ConvexContactManifold))
                {
                    owner.Valid = false;
                    return;
                }
                leaf.Owner.Children.Add(new Child(leaf.ChildIndex,
                    Unsafe.As<TManifold, ConvexContactManifold>(ref manifold)));
            }
            else owner.AddManifold(leaf.Owner, leaf.ChildIndex, ref manifold);
        }
    }
}
