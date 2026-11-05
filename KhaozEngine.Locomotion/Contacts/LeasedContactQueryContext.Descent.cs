using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

internal sealed partial class LeasedContactQueryContext
{
    internal ContactDescentEvidence? DescentEvidence => _lease.DescentEvidence;
    internal MovementAvailability DescribeSelectedAnalytic(Vector3 feet, in SupportSample sample,
        out MovementSurfaceFeature feature) => _lease.DescribeSelectedAnalytic(feet, sample, _selection.Space, out feature);
}
