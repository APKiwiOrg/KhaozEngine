using System;
using KhaozEngine.Movement;
using Xunit;

namespace KhaozEngine.Tests.Movement;

[Collection("AllocSensitive")]
public class TerrainIdentifierAllocationTests
{
    [Fact]
    public void RefusedIdentifierNeverAllocatesItsDecodedString()
    {
        // #1697 F1. A modest name makes an early string allocation observable without a giant payload.
        const int characters = 16 * 1024;
        var writer = new NavBakeWriter(); TerrainEvidenceBinary.Text(writer, new string('x', characters));
        byte[] bytes = writer.ToArray();
        PhysicsNavDescentEvidenceException? refusal = null;
        void Refuse()
        {
            try { var reader = new TerrainEvidenceReader(bytes); _ = reader.Text(new(0)); }
            catch (PhysicsNavDescentEvidenceException failure) { refusal = failure; }
        }
        Refuse();
        refusal = null;
        long allocated = AllocationMeasurement.Measure("Refused terrain identifier before UTF-16 allocation", Refuse);
        Assert.NotNull(refusal);
        Assert.Equal(PhysicsNavDescentEvidenceReason.IndexStorageExceeded, refusal.Reason);
        // The decoded UTF-16 string alone would exceed this bound. Exception storage remains permitted.
        Assert.InRange(allocated, 0, characters);
    }
}
