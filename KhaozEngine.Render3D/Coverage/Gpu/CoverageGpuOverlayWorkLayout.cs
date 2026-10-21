namespace KhaozEngine.Render3D;

/// <summary>Face-owned ordered candidate tapes and independent host guards for four support stripes.</summary>
internal static class CoverageGpuOverlayWorkLayout
{
    internal const int Stripes = 4, Pair = 44, Guards = 46, Capacity = 48, PairWords = 17;

    // Fifteen sample words, presence mask, completed pair. A failed pair publishes only its accepted prefix.
    // Every active stripe initializes its guard and pair headers. Inactive face payloads remain unspecified.
    internal const string Source = """
        uint covOverlayPairWork(uint w, uint face, uint pair) {
            return covRow(w,44u)+(face*CovWorkWords[w+48u]+pair)*17u;
        }
        // Quotient/remainder form avoids overflowing stripe*count for checked large reservations.
        uint covOverlayStripeStart(uint stripe, uint count) {
            return (count/4u)*stripe+((count%4u)*stripe)/4u;
        }
        """;
}
