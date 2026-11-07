using System.Collections;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class TopologyCountObservationTests
{
    [Theory]
    [InlineData("chain", MapDocumentRefusal.ValueOutsideRange, 1)]
    [InlineData("portal", MapDocumentRefusal.GeometryVertexSequenceInvalid, 1)]
    [InlineData("slots", MapDocumentRefusal.ValueOutsideRange, 0)]
    public void Count_diagnostic_uses_the_observation_that_failed(string subject, MapDocumentRefusal reason, long actual)
    {
        var world = TopologyRecordFixtures.WithEveryRecordKind();
        var records = world.Patches[0].Records;
        var chain = records.OfType<MapBoundaryChain>().Single(c => c.Id == "lower");
        var portal = records.OfType<MapCavePortal>().Single();
        var opening = records.OfType<MapHorizontalOpening>().Single();
        if (subject == "chain") TopologyRecordFixtures.Replace<MapBoundaryChain>(world, "lower", c => c with
        { Vertices = new ChangingCountList<MapChainVertex>(c.Vertices, 1, 2) });
        if (subject == "portal") TopologyRecordFixtures.Replace<MapCavePortal>(world, "portal", p => p with
        { Interval = new ChangingCountList<MapLatticeVertex>(p.Interval, 1, 2) });
        if (subject == "slots") TopologyRecordFixtures.Replace<MapHorizontalOpening>(world, "opening", o => o with
        { SlotCells = new ChangingCountList<int>(o.SlotCells, 0, 1) });
        var finding = Assert.Single(MapTopologyReferenceValidator.ValidateFindings(world.Surfaces, world.Patches));
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(actual, finding.ActualCount);
        Assert.Equal(subject == "chain" ? "lower" : subject == "portal" ? "portal" : "opening", finding.RecordId);
        if (subject == "chain") TopologyRecordFixtures.Replace<MapBoundaryChain>(world, "lower", _ => chain);
        if (subject == "portal") TopologyRecordFixtures.Replace<MapCavePortal>(world, "portal", _ => portal);
        if (subject == "slots") TopologyRecordFixtures.Replace<MapHorizontalOpening>(world, "opening", _ => opening);
        Assert.Empty(world.Validate());
    }

    sealed class ChangingCountList<T>(IReadOnlyList<T> values, int first, int subsequent) : IReadOnlyList<T>
    {
        bool _observed;
        public int Count
        {
            get
            {
                int count = _observed ? subsequent : first;
                _observed = true;
                return count;
            }
        }
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
