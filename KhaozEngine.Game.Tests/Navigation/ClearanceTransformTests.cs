using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class ClearanceTransformTests
{
    [Fact]
    public void BlockedCellsAreZero_AdjacentCellsAreTwo()
    {
        // 5x5, single blocked cell in the middle
        var blocked = new bool[25];
        blocked[2 * 5 + 2] = true;
        byte[] c = ClearanceTransform.Compute(blocked, 5, 5);
        Assert.Equal(0, c[2 * 5 + 2]);
        Assert.Equal(2, c[2 * 5 + 1]);
        Assert.Equal(2, c[1 * 5 + 2]);
        Assert.Equal(3, c[1 * 5 + 1]);
    }

    [Fact]
    public void BordersActAsBlocked()
    {
        // fully open grid: border cells read distance-to-outside
        var blocked = new bool[25];
        byte[] c = ClearanceTransform.Compute(blocked, 5, 5);
        Assert.Equal(2, c[0]);
        Assert.Equal(2, c[4]);
        Assert.Equal(4, c[1 * 5 + 1]);
        Assert.Equal(6, c[2 * 5 + 2]);
    }

    [Fact]
    public void MatchesBruteForceChamfer_OnRandomFixedMask()
    {
        // fixed pseudo-random mask (deterministic seed), compare against a brute force
        // Dijkstra-style propagation that uses the same 2-3 step costs
        const int w = 16, h = 12;
        var blocked = new bool[w * h];
        uint s = 12345;
        for (int i = 0; i < blocked.Length; i++)
        {
            s = s * 1664525u + 1013904223u;
            blocked[i] = (s >> 24) < 40;
        }
        byte[] got = ClearanceTransform.Compute(blocked, w, h);
        byte[] want = BruteForce(blocked, w, h);
        Assert.Equal(want, got);
    }

    static byte[] BruteForce(bool[] blocked, int w, int h)
    {
        // exhaustive relaxation until fixpoint, seeded exactly like Compute
        var d = new int[w * h];
        for (int z = 0; z < h; z++)
        for (int x = 0; x < w; x++)
        {
            int i = z * w + x;
            int edge = System.Math.Min(System.Math.Min(x, z), System.Math.Min(w - 1 - x, h - 1 - z)) + 1;
            d[i] = blocked[i] ? 0 : System.Math.Min(255, edge * 2);
        }
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
            {
                int i = z * w + x;
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= w || nz >= h) continue;
                    int cost = (dx == 0 || dz == 0) ? 2 : 3;
                    int cand = d[nz * w + nx] + cost;
                    if (cand < d[i]) { d[i] = cand; changed = true; }
                }
            }
        }
        var result = new byte[w * h];
        for (int i = 0; i < d.Length; i++) result[i] = (byte)System.Math.Min(255, d[i]);
        return result;
    }
}
