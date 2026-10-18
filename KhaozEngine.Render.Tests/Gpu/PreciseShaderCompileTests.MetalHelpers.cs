using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed partial class PreciseShaderCompileTests
    {
        // A changed helper body must be refused rather than silently losing IEEE zero signs.
        [Theory]
        [MemberData(nameof(PinnedMultiplyHelpers))]
        public void MetalMultiplyHelpersAcceptOnlyPinnedOrCorrectedForms(string pinned)
        {
            string corrected = pinned.Replace("T(0)", "T(-0.0)", StringComparison.Ordinal)
                .Replace("(0)", "(T(-0.0))", StringComparison.Ordinal);
            Assert.NotEqual(pinned, corrected);
            Assert.Equal(corrected, MslPreciseMath.Rewrite(pinned));
            Assert.Equal(corrected, MslPreciseMath.Rewrite(corrected));
            string mixed = pinned.Replace("template<", "// preserved\r\ntemplate<", StringComparison.Ordinal);
            Assert.Equal(mixed.Replace("T(0)", "T(-0.0)", StringComparison.Ordinal)
                .Replace("(0)", "(T(-0.0))", StringComparison.Ordinal), MslPreciseMath.Rewrite(mixed));
            ShaderValidationException error = Assert.Throws<ShaderValidationException>(
                () => MslPreciseMath.Rewrite(pinned.Replace("(0)", "(1)", StringComparison.Ordinal)));
            Assert.Contains("spvFMul", error.Message);
            Assert.Contains("unrecognised", error.Message);
            Assert.Throws<ShaderValidationException>(() => MslPreciseMath.Rewrite(
                pinned.Replace(")\n{", ") noexcept\n{", StringComparison.Ordinal)));
        }

        [Fact]
        public void MetalMultiplyHelpersRejectUnknownDefinitions()
        {
            Assert.Throws<ShaderValidationException>(() => MslPreciseMath.Rewrite(
                "template<typename T>\nT spvFMul(T l, T r)\n{\n    return l * r;\n}"));
        }

        [Theory]
        [MemberData(nameof(PinnedMultiplyHelpers))]
        public void MetalMultiplyHelpersRejectUnrecognisedIdentifiers(string pinned)
        {
            int start = pinned.IndexOf("spvFMul", StringComparison.Ordinal);
            string name = pinned[start..pinned.IndexOf('(', start)];
            string[] unfamiliar =
            {
                pinned.Replace(name + "(", name + " /* toolchain comment */ (", StringComparison.Ordinal),
                pinned.Replace(name + "(", name + " // toolchain comment\n(", StringComparison.Ordinal),
                pinned.Replace(name + "(", name + " // toolchain comment\r\n(", StringComparison.Ordinal),
                pinned.Replace(name + "(", name + "<T>(", StringComparison.Ordinal),
                "T " + name + "(T l, T r);",
                "T (*" + name + ")(T, T);",
                "auto alias = " + name + ";",
                "auto value = " + name + "Unfamiliar(a, b);",
                "#define " + name + "(l, r) ((l) * (r))"
            };
            foreach (string unknown in unfamiliar)
            {
                // A known definition must not make an unfamiliar second declaration or use acceptable.
                foreach (string source in new[] { unknown, pinned + "\n" + unknown })
                {
                    ShaderValidationException error = Assert.Throws<ShaderValidationException>(() => MslPreciseMath.Rewrite(source));
                    Assert.Contains(name, error.Message);
                    Assert.Contains("unrecognised", error.Message);
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
        [MemberData(nameof(PinnedMultiplyHelpers))]
        public void MetalMultiplyHelpersPreserveLineEndings(string pinned)
        {
            foreach (string newline in new[] { "\n", "\r\n", "\r" })
            {
                string emitted = pinned.ReplaceLineEndings(newline);
                string corrected = emitted.Replace("T(0)", "T(-0.0)", StringComparison.Ordinal)
                    .Replace("(0)", "(T(-0.0))", StringComparison.Ordinal);
                Assert.Equal(corrected, MslPreciseMath.Rewrite(emitted));
                Assert.Equal(corrected, MslPreciseMath.Rewrite(corrected));
            }
        }

        static void DumpPreciseModule(GpuShaderStages stage, byte[] spirv)
        {
            string? directory = Environment.GetEnvironmentVariable("KE_SHADER_CORPUS_DUMP");
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, $"PreciseMath.{stage}.spirv"), spirv);
        }

        public static IEnumerable<object[]> PinnedMultiplyHelpers()
        {
            string[] helpers =
            {
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
