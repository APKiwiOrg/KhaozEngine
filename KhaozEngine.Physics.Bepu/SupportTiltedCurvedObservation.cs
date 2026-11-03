namespace KhaozEngine.Physics.Bepu;

/// <summary>Copied tilted proof work counters and refusal flags, independent of retained mutable proof storage.</summary>
internal readonly record struct SupportTiltedCurvedObservation(long RootCellCount, int RootLiveHighWater,
    int RootTerminalHighWater, bool RootCapacityRefused, long AdmissionTotalCells, int AdmissionFrontierHighWater,
    bool AdmissionCapacityRefused);
