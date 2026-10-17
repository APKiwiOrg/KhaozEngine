using System.Linq;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void CurvedCollectionHasItsOwnPhase()
    {
        SupportQueryDiagnostics.Enable(true);
        var recorder = new SupportCostAttributionTests.PhaseRecorder(false);
        ContactAllocationDiagnostics.SetObserver(recorder.Observe);
        SupportCostSample sample;
        try
        {
            sample = new SupportCostSampler(false) { BeforeWarm = recorder.Reset, IsolateAllocations = true }
                .Measure(CostRows().Single(row => row.Name == "sphere"), "Find")[0];
        }
        finally
        {
            ContactAllocationDiagnostics.SetObserver(null);
            SupportQueryDiagnostics.Enable(false);
        }
        Assert.True(sample.GeometryMatchesOracle);
        Assert.Equal(0, recorder.Depth);
        Assert.Equal(0, sample.WarmBytes);
        Assert.Equal(sample.WarmBytes, recorder.Bytes.Sum());
        // The sphere's contact kernel runs once per contact proposal, nested inside its curved collection.
        Assert.Contains(recorder.Rows(), row => row.Phase == "SupportCurved" && row.Calls > 0);
        Assert.Contains(recorder.Rows(), row => row.Phase == "SupportCurvedContact" && row.Calls > 0);
    }
}
