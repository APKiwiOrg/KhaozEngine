namespace KhaozEngine.Physics;

internal enum SupportCurvedWorkKind { New, Sign, Split, Compile, RootCell, Refine, AdmissionCell }
internal enum SupportCurvedWorkFailure { None, WorkLimit, RootStorage, TerminalRoots, AdmissionStorage }

/// <summary>Cumulative certification events for one complete footprint request, independent of graph restores.</summary>
internal readonly record struct SupportCurvedWorkObservation(long Consumed, int Limit, long New, long Sign,
    long Split, long Compile, long RootCells, long Refine, long AdmissionCells, int RootStorageHighWater,
    int AdmissionStorageHighWater, SupportCurvedWorkFailure Failure, CapsuleFeatureStatus FootprintStatus);
