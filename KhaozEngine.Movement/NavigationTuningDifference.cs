namespace KhaozEngine.Movement;

// The identity producer supplies the field and values alongside its unchanged prose projection.
internal readonly record struct NavigationTuningDifference(string FieldName, uint ExpectedBits, uint ActualBits, string Detail);
