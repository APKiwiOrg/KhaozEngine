namespace KhaozEngine.Render3D;

/// <summary>Invocation-owned continuation records, allocated with checked word and byte offsets.</summary>
internal static class CoverageGpuQueryWorkLayout
{
    // Query XYZ, height, spatial weight, state. States are idle 0, boundary 1, finish 2.
    // ConformGroups initializes all six words, including idle and failed records, before either consumer.
    internal const int ConformWords = 6;
    // Nominal triangle XYZ per corner, discrepancy, maximum, state. States are idle 0, overlay 1, finish 2.
    // CorrectFaces always initializes state. Idle payload words are unspecified and must not be read.
    // Nonzero state publishes the full payload. Each consumer checks its required state before loading it.
    internal const int FaceWords = 12;

    internal const string Source = """
        struct CovConformWork { vec3 query; float height; float spatial; uint state; };
        CovConformWork covConformWorkRead(uint p) {
            return CovConformWork(covWork3(p),covRead(CovWorkWords[p+3u]),covRead(CovWorkWords[p+4u]),CovWorkWords[p+5u]);
        }
        void covConformWorkStore(uint p, CovConformWork q) {
            covWrite3(p,q.query); CovWorkWords[p+3u] = floatBitsToUint(q.height);
            CovWorkWords[p+4u] = floatBitsToUint(q.spatial); CovWorkWords[p+5u] = q.state;
        }
        struct CovFaceWork { CovTriangle host; float discrepancy; float maximum; uint state; };
        // The caller must check the initialized state word at p+11u before reading this payload.
        CovFaceWork covFaceWorkRead(uint p) {
            return CovFaceWork(CovTriangle(covWork3(p),covWork3(p+3u),covWork3(p+6u)),
                covRead(CovWorkWords[p+9u]),covRead(CovWorkWords[p+10u]),CovWorkWords[p+11u]);
        }
        void covFaceWorkStore(uint p, CovFaceWork q) {
            covWrite3(p,q.host.a); covWrite3(p+3u,q.host.b); covWrite3(p+6u,q.host.c);
            CovWorkWords[p+9u] = floatBitsToUint(q.discrepancy); CovWorkWords[p+10u] = floatBitsToUint(q.maximum);
            CovWorkWords[p+11u] = q.state;
        }
        """;
}
