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
    /// The internal, toolchain-free SPIRV cross-compile helper in <c>KhaozEngine.Gpu</c> (decision P2, section 3
    /// of <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>): the single seat the native Direct3D 11
    /// backend reaches SPIRV-Cross through, so the backend itself declares no toolchain package. The decision
    /// was written as "Veldrid-free" and the package left in 18.0.0, taking the wording with it and not the
    /// rule.
    /// <para>
    /// Two properties are worth separating. That it WORKS (GLSL in, HLSL plus usable reflection out) is the
    /// ordinary half. That its contract mentions no Veldrid type is the half the whole layering decision rests
    /// on, and it is the one nothing else would catch: the backend can see these members across
    /// <c>InternalsVisibleTo</c>, and internal API is exactly what a public-surface scan does not check.
    /// </para>
    /// <para>
    /// Device-free and CPU-only, so this runs on every leg. Only <c>HLSL</c> is emitted here, unlike
    /// <c>ShaderValidation</c>, which cross-compiles to all four languages: this helper exists for the Direct3D
    /// path, and the same helper's MSL members sit under their own pin and are covered by the Metal tests. The
    /// Vulkan backend consumes SPIR-V and cross-compiles nothing at all.
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
            // NOT that the name is non-empty, which is what this asserted until 18.0.0 and which was an
            // assertion about the OUTGOING toolchain rather than about the module. Veldrid.SPIRV reported
            // SPIRV-Cross's FALLBACK name for anything the module does not name, the SPIR-V id rendered as
            // "_25"; shaderc reports the empty string, because with debug info off there is no OpName to
            // report. Nothing binds on either (#586), the ids move whenever the compiler version does, and
            // KhaozEngine.Render.Tests/Gpu/shader-corpus/README.md has the measurement. Empty, never null, is
            // the contract that is left.
            Assert.All(elements, e => Assert.NotNull(e.Name));
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

        // THE PAIR THAT TELLS AN EXPLICIT SORT APART FROM A LUCKY ONE. Three orders are deliberately different
        // here. GLSL DECLARATION order is Surface, SurfaceSampler, Tint. SPIRV-Cross enumerates by resource TYPE,
        // so its list order is Tint, Surface, SurfaceSampler. BINDING order, which is the only one the backends
        // may see, is SurfaceSampler at 0, Tint at 1, Surface at 2. A reflection that trusted either of the first
        // two would compile, bind every resource to the wrong register and produce a picture. The vertex inputs
        // do the same in miniature: declared 2, 0, 1 and reflected 0, 1, 2.
        const string ShuffledVertexGlsl = @"#version 450
layout(location = 2) in vec4 Weights;
layout(location = 0) in vec3 Position;
layout(location = 1) in vec2 TexCoord;
layout(set = 0, binding = 0) uniform Transform { mat4 Mvp; };
layout(location = 0) out vec2 fsUv;
void main()
{
    fsUv = TexCoord + Weights.xy;
    gl_Position = Mvp * vec4(Position, 1);
}";

        const string ShuffledFragmentGlsl = @"#version 450
layout(location = 0) in vec2 fsUv;
layout(location = 0) out vec4 fsColor;
layout(set = 1, binding = 2) uniform texture2D Surface;
layout(set = 1, binding = 0) uniform sampler SurfaceSampler;
layout(set = 1, binding = 1) uniform Tint { vec4 Colour; };
void main()
{
    fsColor = texture(sampler2D(Surface, SurfaceSampler), fsUv) * Colour;
}";

        /// <summary>
        /// RISK R5, AS A TEST RATHER THAN A COMMENT. SPIRV-Cross hands its resources back in neither declaration
        /// nor binding order, and both reflected arrays are indexed POSITIONALLY by the backends, so an
        /// enumeration order trusted as-is is a silent rebinding rather than a failure. The sort in
        /// <c>SpirvCrossReflect</c> is what makes the order a property of the module instead of a property of the
        /// library version, which matters most in the release that changed the library version.
        /// </summary>
        [Fact]
        public void AShuffledSource_ReflectsInBindingOrderAndNotInTheOrderItWasHandedBack()
        {
            CrossCompiledPair pair =
                SpirvCrossCompile.GlslPairToHlsl(ShuffledVertexGlsl, ShuffledFragmentGlsl, "shuffled pair");

            GpuVertexElement[] inputs = pair.Reflection.VertexElements;
            Assert.Equal(
                new[] { GpuVertexElementFormat.Float3, GpuVertexElementFormat.Float2, GpuVertexElementFormat.Float4 },
                inputs.Select(e => e.Format));

            GpuResourceLayoutElement[] set1 = pair.Reflection.ResourceLayouts[1].Elements;
            Assert.Equal(
                new[] { GpuResourceKind.Sampler, GpuResourceKind.UniformBuffer, GpuResourceKind.TextureReadOnly },
                set1.Select(e => e.Kind));
        }

        // A pair that binds nothing at all. Legal GLSL, and the shape #599 was filed about.
        const string BareVertexGlsl = @"#version 450
layout(location = 0) in vec3 Position;
void main() { gl_Position = vec4(Position, 1); }";

        const string BareFragmentGlsl = @"#version 450
layout(location = 0) out vec4 fsColor;
void main() { fsColor = vec4(1, 0, 0, 1); }";

        /// <summary>
        /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/599">#599</see>. A module that declares no
        /// resource reflects NO sets, not one empty set. The array is what a pipeline's resource layouts are
        /// built from set by set, so a phantom set 0 is a layout the backend creates, binds and validates against
        /// for a shader that never asked for one.
        /// </summary>
        [Fact]
        public void AResourceFreeSource_ReflectsNoSetsAtAll()
        {
            CrossCompiledPair pair =
                SpirvCrossCompile.GlslPairToHlsl(BareVertexGlsl, BareFragmentGlsl, "bare pair");

            Assert.Empty(pair.Reflection.ResourceLayouts);
            // The vertex side is untouched by the trim: an input signature is not a resource set.
            Assert.Single(pair.Reflection.VertexElements);
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
        /// (public or internal, which is everything not private) must be expressed in engine types. A toolchain
        /// type anywhere in this contract would compile fine, would put that assembly reference in
        /// <c>KhaozEngine.Gpu.D3D11</c>'s IL the moment the backend called it, and would defeat decision P2 through
        /// an API surface no public-surface scan looks at.
        /// <para>
        /// The walk below looks for a <c>Veldrid</c> assembly, which 18.0.0 left nothing of, so it cannot fail
        /// any more. It is kept for the same reason <c>GpuPublicApiTests</c> keeps its rows: the walk is the
        /// pattern, not the vendor. <c>ArchitectureTests.NoTwoShaderToolchains</c> is the half that binds.
        /// </para>
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
