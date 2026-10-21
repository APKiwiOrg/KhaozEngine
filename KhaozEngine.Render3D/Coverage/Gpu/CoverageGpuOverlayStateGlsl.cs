using System.Text;

namespace KhaozEngine.Render3D;

/// <summary>Overlay child cursors retain only state that cannot be recovered from their parent.</summary>
internal static class CoverageGpuOverlayStateGlsl
{
    internal static readonly string Source = BuildSource();

    const string Template = """

        // Each lane owns one column. Only the unrounded side needed by the next crossing step is saved.
        // There is no communication between lanes, so divergent exits require no group barrier.
        shared uint CovOverlayExactWords[1216];
        CovExact covOverlayExactRead() {
            CovExact value; uint lane = gl_LocalInvocationID.x;
            /* READ */
            value.sign = int(CovOverlayExactWords[1152u+lane]); return value;
        }
        void covOverlayExactStore(CovExact value) {
            uint lane = gl_LocalInvocationID.x;
            /* STORE */
            CovOverlayExactWords[1152u+lane] = uint(value.sign);
        }
        struct CovOverlayCrossingGraph { int phase; int power; float x; };
        CovOverlayCrossingGraph covOverlayCrossingBegin() { return CovOverlayCrossingGraph(CovCrossBegin,0,0.0); }
        bool covOverlayCrossingStep(inout CovOverlayCrossingGraph cursor, vec3 s, vec3 t, vec3 a, vec3 b,
            CovExactResult result, out CovExactJob job, out float value) {
            CovCrossingGraph q = covCrossingBegin(s,t,a,b);
            q.phase = cursor.phase; q.power = cursor.power; q.x = cursor.x;
            if (q.phase == CovCrossB) q.ca = covOverlayExactRead();
            if (q.phase == CovCrossRoundA) q.cb = covOverlayExactRead();
            int previous = q.phase;
            bool pending = covCrossingStep(q,result,job);
            if (previous == CovCrossA) covOverlayExactStore(q.ca);
            if (previous == CovCrossB) covOverlayExactStore(q.cb);
            cursor.phase = q.phase; cursor.power = q.power; cursor.x = q.x; value = q.value;
            return pending;
        }
        struct CovOverlayHeightGraph { int phase; CovPredicate u; CovPredicate v; CovPredicate w; float az; float bz; bool edge; };
        CovOverlayHeightGraph covOverlayHeightBegin() {
            CovPredicate zero = CovPredicate(0,0.0);
            return CovOverlayHeightGraph(CovTHBegin,zero,zero,zero,0.0,0.0,false);
        }
        bool covOverlayHeightStep(inout CovOverlayHeightGraph cursor, CovTriangle triangle, vec2 point, CovPredicate area,
            CovExactResult result, out CovExactJob job, out float height) {
            CovTriangleHeightGraph q = covTriangleHeightBegin(triangle,point);
            q.phase = cursor.phase; q.u = cursor.u; q.v = cursor.v; q.w = cursor.w;
            q.area = area; q.cachedArea = true; q.edge = cursor.edge;
            // Endpoints are chosen in CovTHW. Only their z words are consumed on the resumed CovTHDiv step.
            q.a.z = cursor.az; q.b.z = cursor.bz;
            bool pending = covTriangleHeightStep(q,result,job);
            cursor.phase = q.phase; cursor.u = q.u; cursor.v = q.v; cursor.w = q.w;
            cursor.az = q.a.z; cursor.bz = q.b.z; cursor.edge = q.edge; height = q.height;
            return pending;
        }
        int covOverlaySide(uint sides, int corner, int side) { return int((sides >> uint((corner*3+side)*2)) & 3u)-1; }
        void covOverlaySidesStore(inout uint sides, int corner, uint value) {
            uint shift = uint(corner*6); sides = (sides & ~(63u << shift)) | (value << shift);
        }

        """;

    static string BuildSource()
    {
        var read = new StringBuilder();
        var store = new StringBuilder();
        for (int i = 0; i < 18; i++)
        {
            read.AppendLine($"value.limb[{i}] = CovOverlayExactWords[{i * 64}u+lane];");
            store.AppendLine($"CovOverlayExactWords[{i * 64}u+lane] = value.limb[{i}];");
        }
        return Template.Replace("/* READ */", read.ToString()).Replace("/* STORE */", store.ToString());
    }
}
