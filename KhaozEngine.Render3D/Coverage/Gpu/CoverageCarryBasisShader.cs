namespace KhaozEngine.Render3D;

/// <summary>One face owns its complete native direction basis.</summary>
internal static class CoverageCarryBasisShader
{
    internal static string Source => CoverageCarryNormalShader.Source + "\n" + """
        void main() {
            uint f = covFrame(), w = CovFrameWords[f+1u], triangle = carryIndex();
            if (triangle >= CovFrameWords[f+7u]) return;
            uint p = covNormalWork(w,triangle);
            CovWorkWords[p+44u] = 0u; CovWorkWords[p+45u] = 0u;
            uint h = CovFrameWords[f], indices = covTable(h,20u)+triangle*3u;
            uint ai = CovStaticWords[indices], bi = CovStaticWords[indices+1u], ci = CovStaticWords[indices+2u];
            if (!covMoved(w,ai) && !covMoved(w,bi) && !covMoved(w,ci)) return;
            uint before = covRow(w,4u), after = covRow(w,8u), a = ai*16u, b = bi*16u, c = ci*16u;
            vec3 u = covDirectionSub(covWork3(before+b),covWork3(before+a)), v = covDirectionSub(covWork3(before+c),covWork3(before+a));
            vec3 s = covDirectionSub(covWork3(after+b),covWork3(after+a)), t = covDirectionSub(covWork3(after+c),covWork3(after+a));
            vec3 normal = covDirectionCross(u,v), finalNormal = covDirectionCross(s,t), n, m;
            uint code = covDirectionUnit(normal,n);
            if (code == 0u) code = covDirectionUnit(finalNormal,m);
            if (code == 0u) {
                float alignment = covDirectionDot3(normal,finalNormal);
                float beforeDet = covDirectionDot3(u,covDirectionCross(v,n)), afterDet = covDirectionDot3(s,covDirectionCross(t,m));
                precise float beforeScale = sqrt(covDirectionDot3(u,u)*covDirectionDot3(v,v)), afterScale = sqrt(covDirectionDot3(s,s)*covDirectionDot3(t,t));
                precise float beforeThreshold = beforeScale*1e-12, afterThreshold = afterScale*1e-12;
                if (!covDirectionFinite(alignment) || !covDirectionFinite(beforeDet) || !covDirectionFinite(afterDet)
                    || !covDirectionFinite(beforeScale) || !covDirectionFinite(afterScale)) code = 4u;
                else if (!(alignment > 0.0) || !(beforeDet > 0.0) || !(afterDet > 0.0)
                    || beforeDet <= beforeThreshold || afterDet <= afterThreshold) code = 8u;
                if (code == 0u) {
                    precise vec3 bu = covDirectionCross(v,n)/beforeDet, bv = covDirectionCross(n,u)/beforeDet, bn = normal/beforeDet;
                    precise vec3 au = covDirectionCross(t,m)/afterDet, av = covDirectionCross(m,s)/afterDet, an = finalNormal/afterDet;
                    if (!covDirectionFinite3(bu) || !covDirectionFinite3(bv) || !covDirectionFinite3(bn)
                        || !covDirectionFinite3(au) || !covDirectionFinite3(av) || !covDirectionFinite3(an)) code = 4u;
                    covDirectionWrite3(p+18u,bu); covDirectionWrite3(p+21u,bv); covDirectionWrite3(p+24u,bn);
                    covDirectionWrite3(p+27u,au); covDirectionWrite3(p+30u,av); covDirectionWrite3(p+33u,an);
                }
            }
            if (code == 0u) {
                covDirectionWrite3(p,u); covDirectionWrite3(p+3u,v); covDirectionWrite3(p+6u,n);
                covDirectionWrite3(p+9u,s); covDirectionWrite3(p+12u,t); covDirectionWrite3(p+15u,m);
            }
            CovWorkWords[p+44u] = code; CovWorkWords[p+45u] = code == 0u ? 2u : 3u;
        }
        """;
}
