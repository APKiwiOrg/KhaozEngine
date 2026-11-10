using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed partial class PreciseShaderCompileTests
    {
        public static IEnumerable<object[]> CoverageGraphics() => CoverageGraphicsProgramTests.Programs();
        public static IEnumerable<object[]> CarryKernels() => CoverageCarryKernelTests.Stages();

        [Fact]
        public void CoverageHelperGuardCoversEveryProgramAndKernel()
        {
            Assert.Equal(new[] { "cascade", "cascade-complement", "cascade-dissolve", "dissolve", "main", "motion",
                "motion-dissolve", "outline-full", "outline-visible", "point", "point-complement", "point-dissolve" },
                CoverageGraphics().Select(p => (string)p[0]).Order());
            Assert.Equal(new[] { "CoverageCarrySkin", "CoverageCarryTransport", "CoverageCarryBasis", "CoverageCarryGather", "CoverageCarryPublish" },
                CoverageCarryShaders.Kernels.Select(k => k.Name));
            Assert.Equal(Enumerable.Range(0, CoverageCarryShaders.Kernels.Count), CarryKernels().Select(p => (int)p[0]));
        }

        [Theory]
        [MemberData(nameof(CoverageGraphics))]
        public void CoverageVerticesShipInlinablePreciseMetalHelpers(string name, string vertex, string fragment)
        {
            byte[] v = SpirvFrontEnd.ToSpirv(vertex, GpuShaderStages.Vertex, name, Precise);
            byte[] f = SpirvFrontEnd.ToSpirv(fragment, GpuShaderStages.Fragment, name);
            var emitted = SpirvCrossCompile.VertexFragmentToMsl(v, f, vertexOptions: Precise);
            var built = MetalShaderBuild.Pair(vertex, fragment, null, options: Precise);
            Assert.Equal(emitted.VertexSource, built.StageOf(MetalShaderStage.Vertex).Msl);
            Assert.Equal(emitted.FragmentSource, built.StageOf(MetalShaderStage.Fragment).Msl);
            AssertInlinableHelpers(emitted.VertexSource);
        }

        [Theory]
        [MemberData(nameof(CarryKernels))]
        public void CarryKernelsShipInlinablePreciseMetalHelpers(int stage)
        {
            var kernel = CoverageCarryShaders.Kernels[stage];
            byte[] spirv = SpirvFrontEnd.ToSpirv(kernel.Source, GpuShaderStages.Compute, kernel.Name, Precise);
            string emitted = SpirvCrossCompile.ComputeToMsl(spirv, options: Precise).ComputeSource;
            var built = MetalShaderBuild.Compute(kernel.Source, null, options: Precise);
            Assert.Equal(emitted, built.Program.StageOf(MetalShaderStage.Compute).Msl);
            Assert.Equal(SpirvLocalSize.Parse(spirv, kernel.Name), (built.X, built.Y, built.Z));
            // Publication copies float words and reduces integer bounds, so it emits no arithmetic helpers.
            AssertInlinableHelpers(emitted, expectHelpers: kernel.Name != "CoverageCarryPublish");
        }

        void AssertInlinableHelpers(string msl, bool expectHelpers = true)
        {
            _output.WriteLine(msl);
            if (expectHelpers) Assert.Contains("[[clang::always_inline]]", msl);
            else Assert.DoesNotContain("[[clang::always_inline]]", msl);
            Assert.DoesNotContain("[[clang::optnone]]", msl);
        }

        // A changed helper body must be refused rather than silently losing IEEE zero signs.
        [Theory]
        [MemberData(nameof(PinnedPreciseHelpers))]
        public void MetalPreciseHelpersAcceptOnlyPinnedOrCorrectedForms(string pinned)
        {
            string corrected = ExpectedPreciseHelper(pinned);
            Assert.NotEqual(pinned, corrected);
            Assert.Equal(corrected, MslPreciseMath.Rewrite(pinned));
            Assert.Equal(corrected, MslPreciseMath.Rewrite(corrected));
            Assert.Equal(corrected, MslPreciseMath.Rewrite(corrected.Replace("[[clang::always_inline]]",
                "[[clang::optnone]]", StringComparison.Ordinal)));
            string mixed = pinned.Replace("template<", "// preserved\r\ntemplate<", StringComparison.Ordinal);
            Assert.Equal(ExpectedPreciseHelper(mixed), MslPreciseMath.Rewrite(mixed));
            ShaderValidationException error = Assert.Throws<ShaderValidationException>(
                () => MslPreciseMath.Rewrite(pinned.Replace("return ", "return /* changed */ ", StringComparison.Ordinal)));
            Assert.Equal(Regex.Match(pinned, @"spvF\w*").Value, error.Operation);
            Assert.Equal(ShaderValidationRefusal.PreciseMslHelperUnsupported, error.Reason);
            Assert.Throws<ShaderValidationException>(() => MslPreciseMath.Rewrite(
                pinned.Replace(")\n{", ") noexcept\n{", StringComparison.Ordinal)));
        }

        [Fact]
        public void MetalPreciseHelpersRejectUnknownDefinitions()
        {
            Assert.Throws<ShaderValidationException>(() => MslPreciseMath.Rewrite(
                "template<typename T>\nT spvFMul(T l, T r)\n{\n    return l * r;\n}"));
        }

        [Theory]
        [MemberData(nameof(PinnedPreciseHelpers))]
        public void MetalPreciseHelpersRejectUnrecognisedIdentifiers(string pinned)
        {
            int start = pinned.IndexOf("spvF", StringComparison.Ordinal);
            string name = pinned[start..pinned.IndexOf('(', start)];
            string[] unfamiliar =
            {
                pinned.Replace(name + "(", name + " /* toolchain comment */ (", StringComparison.Ordinal),
                pinned.Replace(name + "(", name + " // toolchain comment\n(", StringComparison.Ordinal),
                pinned.Replace(name + "(", name + " // toolchain comment\r\n(", StringComparison.Ordinal),
                pinned.Replace(name + "(", name + "<T>(", StringComparison.Ordinal),
                "T " + name + "(T l, T r);",
                "T (*" + name + ")(T, T);",
                "T* " + name + "(T l, T r) { return nullptr; }",
                "auto alias = " + name + ";",
                "auto value = " + name + "Unfamiliar(a, b);",
                "#define " + name + "(l, r) ((l) * (r))",
                "# " + name + "(a, b)"
            };
            foreach (string unknown in unfamiliar)
            {
                // A known definition must not make an unfamiliar second declaration or use acceptable.
                foreach (string source in new[] { unknown, pinned + "\n" + unknown })
                {
                    ShaderValidationException error = Assert.Throws<ShaderValidationException>(() => MslPreciseMath.Rewrite(source));
                    Assert.Equal(Regex.Match(unknown, @"spvF\w*").Value, error.Operation);
                    Assert.Equal(ShaderValidationRefusal.PreciseMslHelperUnsupported, error.Reason);
                }
            }
            string call = "\nfloat result = " + name + "(a, b);\n";
            Assert.Equal(MslPreciseMath.Rewrite(pinned) + call, MslPreciseMath.Rewrite(pinned + call));
            string returning = "\nfloat caller(float a, float b) { return " + name + "(a, b); }\n";
            Assert.Equal(MslPreciseMath.Rewrite(pinned) + returning, MslPreciseMath.Rewrite(pinned + returning));
            string comments = "\n// " + name + " is a helper\nconst char* label = \"" + name + "\";\n";
            Assert.Equal(MslPreciseMath.Rewrite(pinned) + comments, MslPreciseMath.Rewrite(pinned + comments));
        }

        [Theory]
        [MemberData(nameof(PinnedPreciseHelpers))]
        public void MetalPreciseHelpersPreserveLineEndings(string pinned)
        {
            foreach (string newline in new[] { "\n", "\r\n", "\r" })
            {
                string emitted = pinned.ReplaceLineEndings(newline);
                string corrected = ExpectedPreciseHelper(emitted);
                Assert.Equal(corrected, MslPreciseMath.Rewrite(emitted));
                Assert.Equal(corrected, MslPreciseMath.Rewrite(corrected));
            }
        }

        [Theory]
        [MemberData(nameof(PinnedPreciseHelpers))]
        public void MetalPreciseHelpersAcceptCallsInExpressions(string pinned)
        {
            int start = pinned.IndexOf("spvF", StringComparison.Ordinal);
            string name = pinned[start..pinned.IndexOf('(', start)];
            string corrected = ExpectedPreciseHelper(pinned);
            string[] expressions =
            {
                $"{name}({name}(a, b), c)",
                $"consume({name}(a, b), {name}(b, c))",
                $"c + {name}(a, b)",
                $"c - {name}(a, b)",
                $"{name}(a, b) + c",
                $"{name}(a, b) - c",
                $"choose ? {name}(a, b) : {name}(b, c)",
                $"(({name}(a, b)))"
            };
            foreach (string expression in expressions)
            {
                string call = "\nfloat result = " + expression + ";\n";
                Assert.Equal(corrected + call, MslPreciseMath.Rewrite(pinned + call));
                Assert.Equal(corrected + call, MslPreciseMath.Rewrite(corrected + call));
            }
        }

        [Theory]
        [InlineData("d.a * d.b * d.c", "spvFMul(spvFMul(d.a, d.b), d.c)")]
        [InlineData("abs(d.a * d.b)", "abs(spvFMul(d.a, d.b))")]
        [InlineData("d.c + d.a * d.b", "spvFAdd(d.c, spvFMul(d.a, d.b))")]
        [InlineData("d.c - d.a * d.b", "spvFSub(d.c, spvFMul(d.a, d.b))")]
        [InlineData("(d.a * d.b) * (d.b * d.c)", "spvFMul(spvFMul(d.a, d.b), spvFMul(d.b, d.c))")]
        [InlineData("d.a > 0.0 ? d.a * d.b : d.b * d.c", "spvFMul(d.a, d.b)")]
        public void MetalPreciseExpressionsCompileThroughPinnedToolchain(string expression, string emittedExpression)
        {
            string source = $$"""
                #version 450
                layout(local_size_x = 1) in;
                layout(set = 0, binding = 0, std430) buffer Data { float a; float b; float c; float outputValue; } d;
                void main()
                {
                    precise float result = {{expression}};
                    d.outputValue = result;
                }
                """;
            byte[] spirv = SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Compute, "precise expression", Precise);
            string msl = SpirvCrossCompile.ComputeToMsl(spirv, options: Precise).ComputeSource;
            Assert.Contains("return fma(l, r, T(-0.0));", msl);
            Assert.DoesNotContain("return fma(l, r, T(0));", msl);
            Assert.Contains(emittedExpression, msl);
            _output.WriteLine(msl);
        }

        static void DumpPreciseModule(GpuShaderStages stage, byte[] spirv)
        {
            string? directory = Environment.GetEnvironmentVariable("KE_SHADER_CORPUS_DUMP");
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, $"PreciseMath.{stage}.spirv"), spirv);
        }

        static string ExpectedPreciseHelper(string pinned) => pinned.Replace("T(0)", "T(-0.0)", StringComparison.Ordinal)
            .Replace("(0)", "(T(-0.0))", StringComparison.Ordinal)
            .Replace("[[clang::optnone]]", "[[clang::always_inline]]", StringComparison.Ordinal);

        public static IEnumerable<object[]> PinnedPreciseHelpers()
        {
            string[] helpers =
            {
                """
                template<typename T>
                [[clang::optnone]] T spvFAdd(T l, T r)
                {
                    return fma(T(1), l, r);
                }
                """,
                """
                template<typename T>
                [[clang::optnone]] T spvFSub(T l, T r)
                {
                    return fma(T(-1), r, l);
                }
                """,
                """
                template<typename T>
                [[clang::optnone]] T spvFMul(T l, T r)
                {
                    return fma(l, r, T(0));
                }
                """,
                """
                template<typename T, int Cols, int Rows>
                [[clang::optnone]] vec<T, Cols> spvFMulVectorMatrix(vec<T, Rows> v, matrix<T, Cols, Rows> m)
                {
                    vec<T, Cols> res = vec<T, Cols>(0);
                    for (uint i = Rows; i > 0; --i)
                    {
                        vec<T, Cols> tmp(0);
                        for (uint j = 0; j < Cols; ++j)
                        {
                            tmp[j] = m[j][i - 1];
                        }
                        res = fma(tmp, vec<T, Cols>(v[i - 1]), res);
                    }
                    return res;
                }
                """,
                """
                template<typename T, int Cols, int Rows>
                [[clang::optnone]] vec<T, Rows> spvFMulMatrixVector(matrix<T, Cols, Rows> m, vec<T, Cols> v)
                {
                    vec<T, Rows> res = vec<T, Rows>(0);
                    for (uint i = Cols; i > 0; --i)
                    {
                        res = fma(m[i - 1], vec<T, Rows>(v[i - 1]), res);
                    }
                    return res;
                }
                """,
                """
                template<typename T, int LCols, int LRows, int RCols, int RRows>
                [[clang::optnone]] matrix<T, RCols, LRows> spvFMulMatrixMatrix(matrix<T, LCols, LRows> l, matrix<T, RCols, RRows> r)
                {
                    matrix<T, RCols, LRows> res;
                    for (uint i = 0; i < RCols; i++)
                    {
                        vec<T, RCols> tmp(0);
                        for (uint j = 0; j < LCols; j++)
                        {
                            tmp = fma(vec<T, RCols>(r[i][j]), l[j], tmp);
                        }
                        res[i] = tmp;
                    }
                    return res;
                }
                """
            };
            foreach (string helper in helpers) yield return new object[] { helper.ReplaceLineEndings("\n") };
        }
    }
}
