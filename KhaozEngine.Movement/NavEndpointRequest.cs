using System.Numerics;

namespace KhaozEngine.Movement;

/// <summary>Resolves one endpoint's point footprint before visiting its candidate layers in order.</summary>
internal struct NavEndpointRequest(GroundNavigation navigation, Vector3 feet)
{
    NavFootprintRequest _footprint = new(navigation.Footprint, feet, feet);
    double _best = double.PositiveInfinity;
    int _layer;
    GroundNavigation.Node _node;

    internal NavSegmentOperation NextOperation { get; private set; } = NavSegmentOperation.EndpointStart;

    internal GroundNavigation.Node? Result { get; private set; }

    /// <summary>Runs the selected footprint or layer probe after the caller has admitted its charge.</summary>
    internal void Step()
    {
        if (NextOperation == NavSegmentOperation.EndpointLayer)
        {
            navigation.ProbeEndpointLayer(feet, _layer++, ref _best, ref _node);
            if (_layer == navigation.Space.Layers.Count)
            {
                if (navigation.AcceptsEndpoint(_best, _node)) Result = _node;
                NextOperation = NavSegmentOperation.Done;
            }
            return;
        }

        if (NextOperation == NavSegmentOperation.EndpointStart) _footprint.Start();
        else _footprint.Step();
        NextOperation = _footprint.NextOperation == NavSegmentOperation.Done && _footprint.Accepted
            ? NavSegmentOperation.EndpointLayer : _footprint.NextOperation;
    }
}
