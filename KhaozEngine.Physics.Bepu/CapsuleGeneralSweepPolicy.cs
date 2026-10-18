namespace KhaozEngine.Physics.Bepu;

/// <summary>General-family backend policy. This does not change wire or movement profile identity.</summary>
internal static class CapsuleGeneralSweepPolicy
{
    internal const uint Version = 1;
    internal const float MaximumErrorMetres = 0.001f;
    internal const float MaximumStoredCoordinate = 1_000_000f;
    internal const int MaximumNodes = 8192, MaximumLeaves = 4096, MaximumPending = 128;
    internal const int MaximumCallbacks = 4096, MaximumUnproved = 4096;
    internal const int MaximumCells = 256, MaximumDepth = 48;
    internal const int MaximumDescendants = 4096, MaximumFeatures = 4096, MaximumChildren = 64;
    internal const int MaximumFaces = 256, MaximumVertices = 508, MaximumFaceIndices = 1524;
    internal const int MaximumExactBits = 4096;
}

/// <summary>One whole-query account. Primitive cells and features reset only at primitive admission.</summary>
internal struct CapsuleSweepWork
{
    internal int Descendants, Cells, Features;
    internal bool Exhausted;

    internal bool Primitive()
    {
        Cells = Features = 0;
        if (Exhausted || Descendants >= CapsuleGeneralSweepPolicy.MaximumDescendants)
        { Exhausted = true; return false; }
        Descendants++;
        return true;
    }

    internal bool Cell()
    {
        if (Exhausted || Cells >= CapsuleGeneralSweepPolicy.MaximumCells)
        { Exhausted = true; return false; }
        Cells++;
        return true;
    }

    internal bool Feature()
    {
        if (Exhausted || Features >= CapsuleGeneralSweepPolicy.MaximumFeatures)
        { Exhausted = true; return false; }
        Features++;
        return true;
    }
}
