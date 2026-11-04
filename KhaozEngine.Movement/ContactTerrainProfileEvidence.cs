using System;
using System.Collections.Generic;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

internal readonly record struct DescentAdmittedBinding(int Layer, int X, int Z, DescentRawBinding Raw);

/// <summary>Exact original raw addresses, never selected from resting heights.</summary>
internal sealed class ContactTerrainProfileEvidence
{
    readonly Dictionary<(int Layer, int X, int Z), DescentRawBinding> _cells = [];
    internal int Count => _cells.Count;
    internal ContactTerrainProfileEvidence(PhysicsNavDescentEvidence evidence, GroundNavigation navigation)
    {
        for (int layer = 0; layer < navigation.Space.Layers.Count; layer++)
        {
            NavGrid grid = navigation.Space.Layers[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    if (!navigation.Graph.Layers[layer].IsAccepted(x, z)) continue;
                    if (grid.SurfaceHeightAt(x, z) is not float raw || !evidence.TryRaw(x, z, BitConverter.SingleToInt32Bits(raw), out var address))
                        throw new PhysicsNavDescentEvidenceException(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous,
                            KhaozEngine.Physics.PhysicsEvidenceStatus.Unavailable);
                    _cells.Add((layer, x, z), address);
                }
        }
    }
    internal bool TryGet(int layer, int x, int z, out DescentRawBinding raw) => _cells.TryGetValue((layer, x, z), out raw);
    internal long PayloadBytes => Count * 24L;
}
