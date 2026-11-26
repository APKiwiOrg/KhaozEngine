using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

// Still-air drag on the exposed surface. The same material area contributes
// to drag and mass, leaving a normal decay rate of coefficient / density.
internal sealed class ClothAirDrag
{
    private readonly ClothRig _rig;
    private readonly NormalProjector[] _projectors;
    private readonly float[] _areas;
    private readonly double _decayRate;
    private float _normalLoss;

    internal ClothAirDrag(ClothRig rig)
    {
        _rig = rig;
        _projectors = new NormalProjector[rig.RestPositions.Length];
        _areas = new float[_projectors.Length];
        _decayRate = (double)rig.Material.NormalAirDrag / rig.Material.Density;

        foreach (ClothMembrane material in rig.Membranes)
        {
            var (a, b, c) = material.Face;
            _areas[a] += material.RestArea;
            _areas[b] += material.RestArea;
            _areas[c] += material.RestArea;
        }
    }

    internal void Begin(Vector3[] positions, float seconds)
    {
        _normalLoss = (float)(1 - Math.Exp(-_decayRate * seconds));
        Array.Clear(_projectors);
        foreach (ClothMembrane material in _rig.Membranes)
        {
            var (a, b, c) = material.Face;
            Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            float squared = normal.LengthSquared();
            if (squared <= 1e-20f || !float.IsFinite(squared)) continue;

            normal /= MathF.Sqrt(squared);
            var face = new NormalProjector(normal, material.RestArea);
            _projectors[a].Add(face);
            _projectors[b].Add(face);
            _projectors[c].Add(face);
        }
    }

    internal Vector3 Damp(int node, Vector3 velocity)
    {
        // P is a rest-area average of face normal projectors. Its eigenvalues
        // lie in [0, 1], so this update cannot add kinetic energy.
        Vector3 projected = _projectors[node].Apply(velocity) / _areas[node];
        return velocity - _normalLoss * projected;
    }

    private struct NormalProjector(Vector3 n, float area)
    {
        private Vector3 _diagonal = area * n * n;
        private Vector3 _offDiagonal = area * new Vector3(n.X * n.Y, n.X * n.Z, n.Y * n.Z);

        internal void Add(in NormalProjector other)
        {
            _diagonal += other._diagonal;
            _offDiagonal += other._offDiagonal;
        }

        internal readonly Vector3 Apply(Vector3 velocity)
        {
            var (xy, xz, yz) = (_offDiagonal.X, _offDiagonal.Y, _offDiagonal.Z);
            return _diagonal * velocity + new Vector3(
                xy * velocity.Y + xz * velocity.Z,
                xy * velocity.X + yz * velocity.Z,
                xz * velocity.X + yz * velocity.Y);
        }
    }
}
