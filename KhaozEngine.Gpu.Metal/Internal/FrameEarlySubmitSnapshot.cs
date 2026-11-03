using System.Linq;

namespace KhaozEngine.Gpu.Metal.Internal;

// Observations of the released split, with no switch or schedule to control it.
internal sealed record FrameEarlySubmitSnapshot(int[] PrefixCommitsPerCallback, bool[] AppliedPerCallback)
{
    public int AppliedCallbacks => AppliedPerCallback.Count(value => value);
}
