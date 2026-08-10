using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The internal, Veldrid-free SPIRV cross-compile helper in <c>KhaozEngine.Gpu</c> (decision P2, section 3 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>): the single seat the native Direct3D 11
    /// backend reaches SPIRV-Cross through, so the backend itself carries no Veldrid edge.
    /// <para>
    /// Two properties are worth separating. That it WORKS (GLSL in, HLSL plus usable reflection out) is the
    /// ordinary half. That its contract mentions no Veldrid type is the half the whole layering decision rests
    /// on, and it is the one nothing else would catch: the backend can see these members across
    /// <c>InternalsVisibleTo</c>, and internal API is exactly what a public-surface scan does not check.
    /// </para>
    /// <para>
    /// Device-free and CPU-only, so this runs on every leg. Only <c>HLSL</c> is emitted here, unlike
    /// <c>ShaderValidation</c>, which cross-compiles to all four languages: this helper exists for the Direct3D
    /// path, and every other backend still goes through Veldrid.
    /// </para>
    /// </summary>
    public sealed class SpirvCrossCompileTests
    {
        // A pair with something in every reflection bucket the register scheme has to number: two vertex inputs
        // (both READ, since SPIRV-Cross drops unread ones), a uniform buffer at set 0, and a texture plus a
        // sampler at set 1. The set numbers are deliberately NOT in binding-kind order, because the flattening
        // rule is pipeline-array order per kind and never the GLSL set number.
        const string VertexGlsl = @"#version 450
layout(location = 0) in vec3 Position;
layout(location = 1) in vec2 TexCoord;
layout(set = 0, binding = 0) uniform Transform { mat4 Mvp; };
layout(location = 0) out vec2 fsUv;
void main()
{
    fsUv = TexCoord;
    gl_Position = Mvp * vec4(Position, 1);
}";

        const string FragmentGlsl = @"#version 450
layout(location = 0) in vec2 fsUv;
layout(location = 0) out vec4 fsColor;
layout(set = 1, binding = 0) uniform texture2D Surface;
layout(set = 1, binding = 1) uniform sampler SurfaceSampler;
void main()
{
    fsColor = texture(sampler2D(Surface, SurfaceSampler), fsUv);
}";

        const string ComputeGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 1, local_size_z = 1) in;
layout(set = 0, binding = 0) uniform Params { uint Count; };
layout(set = 0, binding = 1) buffer Values { float Data[]; };
void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i < Count) Data[i] = Data[i] * 2.0;
}";

        [Fact]
        public void AGlslPair_CrossCompilesToHlsl()
        {
            CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(VertexGlsl, FragmentGlsl, "test pair");

            Assert.False(string.IsNullOrWhiteSpace(pair.VertexSource));
            Assert.False(string.IsNullOrWhiteSpace(pair.FragmentSource));
            // Emitted HLSL, not the GLSL it came from. A cross-compiler that silently passed the source through
            // would satisfy every other assertion here.
            Assert.DoesNotContain("#version 450", pair.VertexSource, StringComparison.Ordinal);
            Assert.Contains("cbuffer", pair.VertexSource, StringComparison.Ordinal);
        }

        /// <summary>
        /// The vertex input signature, in location order. Order is load-bearing: the input layout is built by
        /// counting this array, so a reordering compiles cleanly and reads every attribute from the wrong slot.
        /// </summary>
        [Fact]
        public void TheReflectedVertexInputs_KeepTheirLocationOrderAndFormats()
        {
            CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(VertexGlsl, FragmentGlsl, "test pair");

            GpuVertexElement[] elements = pair.Reflection.VertexElements;
            Assert.Equal(2, elements.Length);
            Assert.Equal(GpuVertexElementFormat.Float3, elements[0].Format);
            Assert.Equal(GpuVertexElementFormat.Float2, elements[1].Format);
            Assert.All(elements, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
        }

        /// <summary>
        /// The resource layouts, in set order, with the kinds and stages the register assignment counts against.
        /// A texture and a sampler SHARE a set here on purpose: they take separate register counters
        /// (<c>t</c> and <c>s</c>), so a reflection that collapsed or reordered them would renumber both.
        /// </summary>
        [Fact]
        public void TheReflectedLayouts_CarryKindAndStageInSetOrder()
        {
            CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(VertexGlsl, FragmentGlsl, "test pair");

            GpuResourceLayoutDescription[] layouts = pair.Reflection.ResourceLayouts;
            Assert.Equal(2, layouts.Length);

            GpuResourceLayoutElement uniform = Assert.Single(layouts[0].Elements);
            Assert.Equal(GpuResourceKind.UniformBuffer, uniform.Kind);
            Assert.True((uniform.Stages & GpuShaderStages.Vertex) != 0);
            // Reflection can never report a dynamic binding: a per-draw rebase is how the ENGINE declares a
            // layout, not something a SPIR-V module can express, so inventing one here would read as a fact.
            Assert.False(uniform.Dynamic);

            Assert.Equal(2, layouts[1].Elements.Length);
            Assert.Equal(GpuResourceKind.TextureReadOnly, layouts[1].Elements[0].Kind);
            Assert.Equal(GpuResourceKind.Sampler, layouts[1].Elements[1].Kind);
            Assert.All(layouts[1].Elements, e => Assert.True((e.Stages & GpuShaderStages.Fragment) != 0));
        }

        [Fact]
        public void AComputeModule_CrossCompilesWithItsLayouts()
        {
            CrossCompiledCompute compute = SpirvCrossCompile.GlslComputeToHlsl(ComputeGlsl, "test compute");

            Assert.False(string.IsNullOrWhiteSpace(compute.ComputeSource));
            Assert.Empty(compute.Reflection.VertexElements);

            GpuResourceLayoutDescription layout = Assert.Single(compute.Reflection.ResourceLayouts);
            Assert.Equal(GpuResourceKind.UniformBuffer, layout.Elements[0].Kind);
            Assert.Equal(GpuResourceKind.StructuredBufferReadWrite, layout.Elements[1].Kind);
            Assert.All(layout.Elements, e => Assert.True((e.Stages & GpuShaderStages.Compute) != 0));
        }

        /// <summary>
        /// A bad source stops with the engine's own shader exception naming the label and the stage, rather than
        /// with whatever the native cross-compiler threw. The label is the whole value: the engine has roughly
        /// fifty GLSL sources, and a raw compiler message names none of them.
        /// </summary>
        [Fact]
        public void ABrokenSource_ThrowsNamingTheShaderAndTheStage()
        {
            ShaderValidationException ex = Assert.Throws<ShaderValidationException>(
                () => SpirvFrontEnd.ToSpirv("#version 450\nvoid main() { this is not glsl }",
                    GpuShaderStages.Fragment, "broken pass"));

            Assert.Contains("broken pass", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Fragment", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE LAYERING ASSERTION. Every member of the helper and of its result types that the backend can see
        /// (public or internal, which is everything not private) must be expressed in engine types. A Veldrid
        /// type anywhere in this contract would compile fine, would put a Veldrid assembly reference in
        /// <c>KhaozEngine.Gpu.D3D11</c>'s IL the moment the backend called it, and would defeat decision P2 through
        /// an API surface no public-surface scan looks at.
        /// </summary>
        [Fact]
        public void TheHelpersContract_NamesNoVeldridType()
        {
            Type[] contract =
            {
                typeof(SpirvFrontEnd), typeof(SpirvFrontEndPin), typeof(SpirvCrossCompile),
                typeof(CrossCompiledPair), typeof(CrossCompiledCompute), typeof(ShaderReflection),
            };

            List<string> leaks = contract.SelectMany(NonPrivateVeldridTypes).ToList();

            bool clean = leaks.Count == 0;
            Assert.True(clean,
                "The internal cross-compile contract names a Veldrid type, which is exactly what decision P2 " +
                "keeps out of KhaozEngine.Gpu.D3D11. Convert it at the boundary inside SpirvCrossCompile and " +
                "carry an engine mirror across instead:\n" + string.Join("\n", leaks));
        }

        // Every non-private method, constructor, property and field of a type, reported wherever a type from a
        // Veldrid assembly appears. Private members are deliberately out of scope: they are the implementation,
        // and naming Veldrid there is the entire reason this helper exists.
        static IEnumerable<string> NonPrivateVeldridTypes(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            string owner = type.Name;

            foreach (MethodInfo m in type.GetMethods(flags).Where(m => !m.IsPrivate))
            {
                if (IsVeldrid(m.ReturnType)) yield return $"{owner}.{m.Name} returns {m.ReturnType.FullName}";
                foreach (ParameterInfo p in m.GetParameters().Where(p => IsVeldrid(p.ParameterType)))
                    yield return $"{owner}.{m.Name} takes {p.ParameterType.FullName} as {p.Name}";
            }
            foreach (ConstructorInfo c in type.GetConstructors(flags).Where(c => !c.IsPrivate))
                foreach (ParameterInfo p in c.GetParameters().Where(p => IsVeldrid(p.ParameterType)))
                    yield return $"{owner}.ctor takes {p.ParameterType.FullName} as {p.Name}";
            foreach (PropertyInfo pr in type.GetProperties(flags).Where(pr => IsVeldrid(pr.PropertyType)))
                yield return $"{owner}.{pr.Name} is {pr.PropertyType.FullName}";
            foreach (FieldInfo f in type.GetFields(flags).Where(f => !f.IsPrivate && IsVeldrid(f.FieldType)))
                yield return $"{owner}.{f.Name} is {f.FieldType.FullName}";
        }

        static bool IsVeldrid(Type t)
        {
            Type leaf = t.HasElementType ? t.GetElementType()! : t;
            return (leaf.Assembly.GetName().Name ?? "").StartsWith("Veldrid", StringComparison.Ordinal);
        }
    }
}
