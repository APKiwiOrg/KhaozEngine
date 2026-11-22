using System;
using System.Linq;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public sealed class RouteCalibrationWeightsTests
{
    [Fact]
    public void LargestWeightMatchesEveryRequestCharge()
    {
        int largest = Enum.GetValues<PathWorkKind>()
            .Where(kind => kind != PathWorkKind.Count)
            .Max(CalibrationWork.WeightOf);

        Assert.Equal(7, largest);
        Assert.Equal(PathWorkWeights.Largest, largest);
    }
}
