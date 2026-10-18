using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>Guarded signed-zero backport for the pinned SPIRV-Cross MSL multiplication helpers.</summary>
    internal static class MslPreciseMath
    {
        // Remove with the toolchain upgrade tracked at https://github.com/APKiwiOrg/KhaozEngine/issues/1495.
        // C4a deliberately preserves the pinned matrix dimensions. Only zero seeds change.
        internal static string Rewrite(string source)
        {
            var helpers = new List<string>();
            foreach (Match definition in Regex.Matches(source, @"\b(spvFMul\w*)\s*\([^;{}]*\)[^;{}]*\{"))
            {
                int start = source.LastIndexOf("template<", definition.Index, StringComparison.Ordinal);
                int end = definition.Index + definition.Length;
                int depth = 1;
                while (end < source.Length && depth != 0)
                {
                    char c = source[end++];
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }
                string? pinned = null;
                string helper = start >= 0 ? source[start..end] : string.Empty;
                if (start >= 0 && depth == 0)
                {
                    string canonical = helper.Replace("\r\n", "\n", StringComparison.Ordinal);
                    foreach (string candidate in Pinned)
                        if (canonical == candidate || canonical == Correct(candidate)) { pinned = candidate; break; }
                }
                if (pinned is null)
                    throw new ShaderValidationException($"MSL precise multiplication helper {definition.Groups[1].Value} has an unrecognised form. "
                        + "Expected the pinned SPIRV-Cross 2.23.0 helper or its signed-zero correction. Review issue 1495 before upgrading the toolchain.");
                helpers.Add(helper);
            }
            foreach (string helper in helpers)
                source = source.Replace(helper, Correct(helper), StringComparison.Ordinal);
            return source;
        }

        static string Correct(string helper) => helper.Replace("T(0)", "T(-0.0)", StringComparison.Ordinal)
            .Replace("(0)", "(T(-0.0))", StringComparison.Ordinal);

        static readonly string[] Pinned =
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
    }
}
