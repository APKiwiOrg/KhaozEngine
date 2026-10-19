namespace KhaozEngine.Render3D;

/// <summary>Normal validation and the serial triangle-order orientation gate.</summary>
internal static class CoverageGpuNormalsGlsl
{
    internal const string ValidateVertices = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], v = gl_GlobalInvocationID.x;
            if (v >= CovFrameWords[f+6u] || covCompletedFailure(w)) return;
            uint before = covRow(w,4u)+v*16u;
            bool valid = covFinite3(covWork3(covRow(w,8u)+v*16u));
            [[dont_unroll]] for (uint field = 0u; field < 16u; field++)
                if (!covFinite(covRead(CovWorkWords[before+field]))) valid = false;
            covEvent(w,v,valid ? 0u : 4u);
        }
        """;

    internal const string ValidateFaces = """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], t = gl_GlobalInvocationID.x;
            if (t >= CovFrameWords[f+7u] || covCompletedFailure(w)) return;
            uint h = CovFrameWords[f], indices = covTable(h,20u)+t*3u, validation = covRow(w,22u)+t*2u;
            uint aIndex = CovStaticWords[indices], bIndex = CovStaticWords[indices+1u], cIndex = CovStaticWords[indices+2u];
            int region = int(CovStaticWords[covTable(h,14u)+t*3u+1u]);
            int slot = CovFrameWords[f+12u] == 0u ? -1 : covRegionSlot(f,region);
            CovWorkWords[validation] = uint(slot); CovWorkWords[validation+1u] = 0u;
            bool changed = covMoved(w,aIndex) || covMoved(w,bIndex) || covMoved(w,cIndex);
            uint code = 0u; int sign = 0;
            if (changed) {
                uint basis = covNormalWork(w,t), state = CovWorkWords[basis+45u];
                code = state == 3u ? CovWorkWords[basis+44u] : (state == 2u ? 0u : 4u);
            }
            int phase = 1;
            vec3 a = vec3(0.0), b = vec3(0.0), c = vec3(0.0), u = vec3(0.0), v = vec3(0.0);
            float area = 0.0, leftLength = 0.0;
            CovExactResult result = covExactResultZero();
            [[dont_unroll]] while (phase < 5 && code == 0u) {
                CovExactJob job = covScalarJob(0.0,1.0,false); bool request = false;
                if (phase == 1) {
                    if (slot < 0) { phase = 5; continue; }
                    mat4 frame = covFrameMatrix(covCommand(f,uint(slot))); uint after = covRow(w,8u);
                    a = covPoint(covWork3(after+aIndex*16u),frame); b = covPoint(covWork3(after+bIndex*16u),frame);
                    c = covPoint(covWork3(after+cIndex*16u),frame);
                    u = covSub3(b,a); v = covSub3(c,a);
                    area = covSub(covMul(u.x,v.y),covMul(u.y,v.x));
                    job = covScalarJob(covDot3(u,u),0.0,true); request = true; phase = 2;
                } else if (phase == 2) {
                    leftLength = result.predicate.value;
                    job = covScalarJob(covDot3(v,v),0.0,true); request = true; phase = 3;
                } else if (phase == 3) {
                    float scale = covMul(leftLength,result.predicate.value);
                    if (!covFinite3(a) || !covFinite3(b) || !covFinite3(c) || !covFinite(area) || !covFinite(scale)) code = 4u;
                    else if (!covGreater(covAbs(area),covMul(1e-9,scale))) code = 8u;
                    else { job = covEdgeJob(a,b,c); request = true; phase = 4; }
                } else {
                    if (!covFinite(result.predicate.value)) code = 4u;
                    else if (result.predicate.sign == 0) code = 8u;
                    else sign = result.predicate.sign;
                    phase = 5;
                }
                if (request) result = covExactWorker(job);
            }
            CovWorkWords[validation+1u] = uint(sign);
            covEvent(w,t,code);
        }
        """;

}
