using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Source-declared candidates only. Unknown sources retain the exact all-pairs path.</summary>
internal sealed class DescentAdjacencyIndex
{
    readonly Dictionary<MovementSurfaceFeatureKey, int> _keys;
    readonly int[] _seen, _candidates, _duplicates;
    int _stamp, _candidateCount;
    readonly DescentFace[] _faces;
    readonly IMovementAnalyticSupportSource _source;
    readonly DescentCaptureIndexBudget _budget;
    readonly MovementAnalyticSupportCandidateVisitor _visit;
    internal DescentAdjacencyIndex(DescentFace[] faces, IMovementAnalyticSupportSource source, DescentCaptureIndexBudget budget)
    {
        _faces = faces; _source = source; _budget = budget; _visit = Visit;
        // Reserve dictionary bookkeeping and fixed scratch before construction. Stamps avoid clearing a world-sized set per face.
        budget.Reserve(faces.Length, 128 + 4 + 4 + 4);
        _keys = new(faces.Length); _seen = new int[faces.Length]; _candidates = new int[faces.Length]; _duplicates = new int[faces.Length];
        Array.Fill(_duplicates, -1);
        for (int i = 0; i < faces.Length; i++)
        {
            if (!faces[i].Analytic) continue;
            if (_keys.TryGetValue(faces[i].Key, out int previous)) _duplicates[i] = previous;
            _keys[faces[i].Key] = i;
        }
    }
    bool Visit(MovementSurfaceFeatureKey key)
    {
        if (_keys.TryGetValue(key, out int index))
            for (; index >= 0; index = _duplicates[index])
                if (_seen[index] != _stamp) { _seen[index] = _stamp; _candidates[_candidateCount++] = index; }
        return true;
    }
    internal int[][] Build(bool reference)
    {
        _budget.Reserve(_faces.Length, 8);
        var joins = new int[_faces.Length][];
        for (int i = 0; i < _faces.Length; i++)
        {
            if (!_faces[i].Analytic) { joins[i] = []; continue; }
            _stamp = i + 1; _candidateCount = 0;
            bool narrowed = !reference && _source is IMovementAnalyticSupportCandidates topology &&
                topology.VisitAdjacentCandidates(_faces[i].Key, _visit) == MovementAvailability.Known;
            if (!narrowed)
            {
                _candidateCount = 0;
                for (int j = 0; j < _faces.Length; j++) if (_faces[j].Analytic) _candidates[_candidateCount++] = j;
            }
            Array.Sort(_candidates, 0, _candidateCount);
            int accepted = 0;
            for (int next = 0; next < _candidateCount; next++)
            {
                int j = _candidates[next];
                if (i != j && _source.AreAdjacent(_faces[i].Key, _faces[j].Key))
                    _candidates[accepted++] = j;
            }
            _budget.Reserve(accepted, 4);
            joins[i] = new int[accepted];
            Array.Copy(_candidates, joins[i], accepted);
            if ((i & 1023) == 1023) ContactTerrainCaptureHooks.Current?.Progress?.Invoke(ContactTerrainCaptureStage.Adjacency, i + 1, _faces.Length);
        }
        return joins;
    }
}
