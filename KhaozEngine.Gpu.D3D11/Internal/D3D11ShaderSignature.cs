using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>One entry of a compiled shader's input signature: an HLSL semantic name and its index, which is
    /// how <c>TEXCOORD3</c> is spelled once FXC has reflected it apart.</summary>
    internal readonly struct D3D11ShaderInputSemantic : IEquatable<D3D11ShaderInputSemantic>
    {
        /// <summary>The semantic name, upper case as FXC reports it (<c>TEXCOORD</c>, <c>SV_VertexID</c>).
        /// </summary>
        internal string Name { get; }

        /// <summary>The semantic index within <see cref="Name"/>.</summary>
        internal uint Index { get; }

        internal D3D11ShaderInputSemantic(string name, uint index)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Index = index;
        }

        public bool Equals(D3D11ShaderInputSemantic other)
            => Index == other.Index && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object? obj) => obj is D3D11ShaderInputSemantic o && Equals(o);
        public override int GetHashCode()
            => HashCode.Combine(Name.ToUpperInvariant(), Index);
        public override string ToString() => Name + Index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// DECISION S5, THE ENFORCED HALF. A compiled vertex shader's <c>TEXCOORD</c> input indices must run
    /// contiguously from 0, and this is the pure rule that says so, with the two production incidents behind it.
    ///
    /// <para>
    /// WHAT GOES WRONG. SPIRV-Cross drops a vertex input the vertex stage does not READ, and it names each
    /// surviving input <c>TEXCOORD&lt;location&gt;</c> from the SPIR-V location decoration, so dropping the middle
    /// of a declared range leaves a hole in the emitted signature. FXC and WARP miscompile a holed input
    /// signature, and neither says so. It happened twice, both times on Direct3D 11 only, both times tolerated by
    /// Metal and Vulkan:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>ShaderSources.Shadow.cs</c>: the shadow depth vertex reads only Position and
    /// IModel0 to 3, so locations 1 to 4 and 9 to 11 were dropped, leaving <c>TEXCOORD0</c> then
    /// <c>TEXCOORD5</c> to <c>8</c>. Building that pipeline at scene construction corrupted WARP so the MAIN
    /// model and splat passes rendered no colour at all, with silhouette, normal and depth intact.</description>
    /// </item>
    /// <item><description><c>ShaderSources.Terrain.cs</c>: the interpolant twin. A fragment-unused interpolant
    /// sat below the live block, was dropped, and the highest live interpolant then read garbage, blowing the
    /// terrain to flat white.</description></item>
    /// </list>
    /// <para>
    /// BOTH WORKAROUNDS STAY. The native backend uses the SAME SPIRV-Cross and the SAME FXC, so it inherits the
    /// gap intolerance unchanged. The fix in a shader is always
    /// one of two shapes: a negligible-but-live SINK that reads every declared input so none is dropped (the
    /// shadow vertices), or ORDERING the interpolants so the used ones form a gap-free prefix (the terrain
    /// vertex). They come out with SPIRV-Cross, not before.
    /// </para>
    /// <para>
    /// WHY A PURE FUNCTION OVER A SEMANTIC LIST rather than something that reflects a shader itself. It runs at
    /// two sites: the shader path checks every module it compiles, and pipeline creation checks the vertex
    /// bytecode the input layout is validated against, so a shader set that came from the disk cache and never
    /// passed through a compiler in this process is checked too. Both sites are Windows-only and reflect through
    /// <see cref="D3D11Fxc"/>. Keeping the RULE separate from the reflection is what lets the rule be tested
    /// headlessly on every platform, which matters because the rule is the part with the interesting cases.
    /// </para>
    /// </summary>
    internal static class D3D11ShaderSignature
    {
        /// <summary>The semantic SPIRV-Cross emits for a user vertex input.</summary>
        internal const string UserSemantic = "TEXCOORD";

        /// <summary>
        /// Throw when <paramref name="signature"/> has a holed <see cref="UserSemantic"/> sequence, naming the
        /// shader through <paramref name="label"/> and printing the sequence it found next to the one it needed.
        /// A signature with no user inputs at all is legal and passes: the fullscreen passes declare none.
        /// </summary>
        /// <exception cref="ShaderValidationException">The indices are not exactly 0 to n-1.</exception>
        internal static void RequireContiguousUserSemantics(
            IReadOnlyList<D3D11ShaderInputSemantic> signature, string label)
        {
            ArgumentNullException.ThrowIfNull(signature);
            string? problem = DescribeHole(signature);
            if (problem is null) return;

            throw new ShaderValidationException(
                $"{label}: the compiled vertex input signature is {problem}. FXC and WARP miscompile a holed "
                + $"{UserSemantic} sequence SILENTLY, and it has cost this engine two production incidents (the "
                + "shadow depth pass corrupted WARP so the main passes rendered no colour, and the terrain blew "
                + "to flat white). SPIRV-Cross drops a vertex input the vertex stage never reads, so the fix is "
                + "in the GLSL: read every declared input with a negligible live weight (the sink in "
                + "ShaderSources.Shadow.cs), or order the interpolants so the used ones form a gap-free prefix "
                + "(the note in ShaderSources.Terrain.cs). Do not renumber the engine-side vertex layout to "
                + "match the hole: the hole is what miscompiles.");
        }

        /// <summary>
        /// What is wrong with <paramref name="signature"/>, as a phrase for a message, or null when it is clean.
        /// Separate from the throw so a test can assert the DIAGNOSIS rather than the fact that something threw.
        /// </summary>
        internal static string? DescribeHole(IReadOnlyList<D3D11ShaderInputSemantic> signature)
        {
            ArgumentNullException.ThrowIfNull(signature);

            var indices = new List<uint>();
            foreach (D3D11ShaderInputSemantic entry in signature)
            {
                // System-value inputs (SV_VertexID, SV_InstanceID) have their own space and never participate.
                if (string.Equals(entry.Name, UserSemantic, StringComparison.OrdinalIgnoreCase))
                    indices.Add(entry.Index);
            }
            if (indices.Count == 0) return null;

            indices.Sort();
            var duplicates = indices.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            if (duplicates.Length != 0)
            {
                return $"has a REPEATED {UserSemantic} index ({Join(duplicates)}), which Direct3D rejects as an "
                    + $"input signature, so the emitted HLSL is malformed rather than merely holed. Found "
                    + $"[{Join(indices)}]";
            }

            for (int i = 0; i < indices.Count; i++)
            {
                if (indices[i] == (uint)i) continue;
                return $"HOLED: it runs [{Join(indices)}] where a contiguous sequence from 0 would be "
                    + $"[{Join(Enumerable.Range(0, indices.Count).Select(n => (uint)n))}]. The first missing "
                    + $"index is {i.ToString(CultureInfo.InvariantCulture)}";
            }
            return null;
        }

        static string Join(IEnumerable<uint> indices)
            => string.Join(", ", indices.Select(i => UserSemantic + i.ToString(CultureInfo.InvariantCulture)));
    }
}
