using System;
using System.Collections.Generic;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// THE REFLECTION PASS, AND IT IS ENGINE-OWNED CODE RATHER THAN SOMETHING THE CROSS-COMPILER HANDS BACK.
    /// Until 18.0.0 <c>Veldrid.SPIRV</c> shipped its own pass over SPIRV-Cross and returned a filled
    /// <c>SpirvReflection</c>, which is why the swap in row 8 of the Veldrid removal
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/691">#691</see>) had to write one: bare
    /// SPIRV-Cross gives you a resource list per module and nothing that resembles a pipeline's layout.
    ///
    /// <para>
    /// IT SORTS, EXPLICITLY, AND THAT IS THE WHOLE POINT OF THE FILE. SPIRV-Cross enumerates resources in
    /// NEITHER declaration nor binding order. Section 2.3 result 2 of
    /// <c>docs/design/VELDRID-REMOVAL-DESIGN-2026-08-22.md</c> measured a probe getting <c>PerObject</c> before
    /// <c>Camera</c>, and <c>Uv, Color, Normal, Position</c> for inputs declared
    /// <c>Position, Normal, Uv, Color</c>. A port that trusts that order produces silently permuted resource
    /// layouts, which is risk R5 and which is the defect class this area has already shipped three times
    /// (7.25.0's albedo swap, 7.51.2's normal-and-depth swap, the splat terrain's second UBO). Resources sort by
    /// SET then BINDING, vertex inputs by LOCATION, and <c>SpirvCrossReflectOrderTests</c> pins both against a
    /// source whose declaration order matches neither.
    /// </para>
    /// <para>
    /// A PAIR IS REFLECTED AS ONE PROGRAM. The two stage modules are read separately, because each is its own
    /// SPIR-V module with its own ids, and merged on (set, binding): a resource both stages reference is ONE
    /// element carrying both stage flags. That is the shape the backends bind against, and it is why the
    /// emitters compile a pair together rather than stage by stage.
    /// </para>
    /// <para>
    /// TRAILING EMPTY SETS ARE NOT PRODUCED, which is
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/599">#599</see> landing with the rewrite. The
    /// layout array is sized to the highest set any resource declares, so a shader referencing no resource at all
    /// reflects ZERO layouts rather than one empty one, and a pipeline created with <c>ResourceLayouts = []</c>
    /// against it is legal. The incumbent reflected one empty set there, and the native Metal backend's shape
    /// check refused the empty declared array that honestly described it. A GAP between sets is still a set: a
    /// shader using sets 0 and 2 reflects three layouts, the middle one empty, because both the Direct3D 11
    /// register scheme and the Vulkan descriptor policy index the array POSITIONALLY.
    /// </para>
    /// </summary>
    internal static class SpirvCrossReflect
    {
        // The six resource types the engine's GpuResourceKind mirror can express, plus the stage inputs. Every
        // other list SPIRV-Cross can hand back is either unreachable from the engine's GLSL or unmodelled, and
        // Refuse below turns the reachable-but-unmodelled ones into the named failure rather than silence.
        static readonly ResourceType[] Modelled =
        {
            ResourceType.UniformBuffer,
            ResourceType.StorageBuffer,
            ResourceType.SeparateImage,
            ResourceType.StorageImage,
            ResourceType.SeparateSamplers,
        };

        /// <summary>Reflect a vertex and fragment pair as one program.</summary>
        internal static unsafe ShaderReflection ForPair(Cross cross, Context* context, Compiler* vertex,
            Compiler* fragment, string tag)
        {
            var elements = new Dictionary<(uint Set, uint Binding), Element>();
            GpuVertexElement[] inputs = ReadStage(cross, context, vertex, GpuShaderStages.Vertex, elements, tag, true);
            ReadStage(cross, context, fragment, GpuShaderStages.Fragment, elements, tag, false);
            return new ShaderReflection(inputs, Layouts(elements));
        }

        /// <summary>Reflect a single compute module. It has no vertex inputs by construction.</summary>
        internal static unsafe ShaderReflection ForCompute(Cross cross, Context* context, Compiler* compute,
            string tag)
        {
            var elements = new Dictionary<(uint Set, uint Binding), Element>();
            ReadStage(cross, context, compute, GpuShaderStages.Compute, elements, tag, false);
            return new ShaderReflection(Array.Empty<GpuVertexElement>(), Layouts(elements));
        }

        /// <summary>
        /// Every resource one module declares, keyed on <c>(set, binding)</c>, merged into
        /// <paramref name="into"/>. The kind-only half of <see cref="ReadStage"/>, for
        /// <see cref="HlslRegisterRemap"/>: it needs the raw binding numbers, which the layout array folds away
        /// when it turns a set's elements into dense positions, and it needs them BEFORE the emission rather
        /// than beside it.
        /// <para>
        /// CALLED ONCE PER STAGE and merged, for the same reason the layouts are: a resource both stages declare
        /// is one resource, at one register, and a per-stage numbering would give the two stages different ones.
        /// </para>
        /// </summary>
        internal static unsafe void ReadResourceKinds(Cross cross, Context* context, Compiler* compiler,
            Dictionary<(uint Set, uint Binding), GpuResourceKind> into, string tag)
        {
            Resources* resources;
            SpirvCrossCompile.Check(context, cross.CompilerCreateShaderResources(compiler, &resources), tag,
                "reflect the module's resources");

            foreach (ResourceType type in Modelled)
            {
                foreach (ReflectedResource resource in List(cross, resources, type))
                {
                    uint set = cross.CompilerGetDecoration(compiler, resource.Id, Decoration.DescriptorSet);
                    uint binding = cross.CompilerGetDecoration(compiler, resource.Id, Decoration.Binding);
                    into[(set, binding)] = Kind(cross, compiler, type, resource.Id);
                }
            }
        }

        // One stage's contribution, merged into the shared table. Returns that stage's vertex inputs when asked
        // for them, which only the vertex stage of a pair is.
        static unsafe GpuVertexElement[] ReadStage(Cross cross, Context* context, Compiler* compiler,
            GpuShaderStages stage, Dictionary<(uint, uint), Element> elements, string tag, bool wantInputs)
        {
            Resources* resources;
            SpirvCrossCompile.Check(context, cross.CompilerCreateShaderResources(compiler, &resources), tag,
                "reflect the module's resources");

            Refuse(cross, resources, ResourceType.SampledImage, tag,
                "a combined image sampler (GLSL 'uniform sampler2D')");
            Refuse(cross, resources, ResourceType.PushConstant, tag, "a push-constant block");

            foreach (ResourceType type in Modelled)
            {
                foreach (ReflectedResource resource in List(cross, resources, type))
                {
                    uint set = cross.CompilerGetDecoration(compiler, resource.Id, Decoration.DescriptorSet);
                    uint binding = cross.CompilerGetDecoration(compiler, resource.Id, Decoration.Binding);
                    GpuResourceKind kind = Kind(cross, compiler, type, resource.Id);

                    if (elements.TryGetValue((set, binding), out Element existing))
                        elements[(set, binding)] = existing with { Stages = existing.Stages | stage };
                    else
                        elements[(set, binding)] = new Element(kind, Name(cross, compiler, resource), stage);
                }
            }

            if (!wantInputs) return Array.Empty<GpuVertexElement>();

            var inputs = new List<(uint Location, GpuVertexElement Element)>();
            foreach (ReflectedResource resource in List(cross, resources, ResourceType.StageInput))
            {
                uint location = cross.CompilerGetDecoration(compiler, resource.Id, Decoration.Location);
                inputs.Add((location,
                    new GpuVertexElement(Name(cross, compiler, resource), Format(cross, compiler, resource, tag))));
            }

            // BY LOCATION, and never by the order the list came back in. Risk R5.
            inputs.Sort((a, b) => a.Location.CompareTo(b.Location));
            var ordered = new GpuVertexElement[inputs.Count];
            for (int i = 0; i < ordered.Length; i++) ordered[i] = inputs[i].Element;
            return ordered;
        }

        // The merged table as the layout array the backends index positionally. Sized to the highest set that
        // carries anything, which is what makes a resource-free module reflect no layouts at all (#599).
        static GpuResourceLayoutDescription[] Layouts(Dictionary<(uint Set, uint Binding), Element> elements)
        {
            if (elements.Count == 0) return Array.Empty<GpuResourceLayoutDescription>();

            uint highest = 0;
            foreach ((uint set, uint _) in elements.Keys) highest = Math.Max(highest, set);

            var layouts = new GpuResourceLayoutDescription[highest + 1];
            for (uint set = 0; set <= highest; set++)
            {
                var inSet = new List<(uint Binding, Element Element)>();
                foreach (KeyValuePair<(uint Set, uint Binding), Element> entry in elements)
                    if (entry.Key.Set == set) inSet.Add((entry.Key.Binding, entry.Value));

                // BY BINDING, and never by the order the list came back in. Risk R5.
                inSet.Sort((a, b) => a.Binding.CompareTo(b.Binding));

                var built = new GpuResourceLayoutElement[inSet.Count];
                for (int i = 0; i < built.Length; i++)
                {
                    // dynamic: false always. A dynamic binding is a property of how the ENGINE declares a layout
                    // for a per-draw rebase, not something a SPIR-V module can express, so reflection can never
                    // report one and inventing a value here would be a guess that reads as a fact.
                    Element element = inSet[i].Element;
                    built[i] = new GpuResourceLayoutElement(element.Name, element.Kind, element.Stages);
                }
                layouts[set] = new GpuResourceLayoutDescription(built);
            }
            return layouts;
        }

        readonly record struct Element(GpuResourceKind Kind, string Name, GpuShaderStages Stages);

        static unsafe ReadOnlySpan<ReflectedResource> List(Cross cross, Resources* resources, ResourceType type)
        {
            ReflectedResource* list;
            nuint count;
            return cross.ResourcesGetResourceListForType(resources, type, &list, &count) != Result.Success
                ? ReadOnlySpan<ReflectedResource>.Empty
                : new ReadOnlySpan<ReflectedResource>(list, (int)count);
        }

        static unsafe void Refuse(Cross cross, Resources* resources, ResourceType type, string tag, string what)
        {
            if (List(cross, resources, type).Length == 0) return;
            throw new ShaderValidationException(
                $"{tag}: the module declares a resource of kind {type}, which the engine's GpuResourceKind mirror "
                + "does not model. Add the kind to the mirror and to both directions of the map, or the register "
                + $"assignment will be counted against a shape the binder cannot express. The source declares {what}.");
        }

        static unsafe GpuResourceKind Kind(Cross cross, Compiler* compiler, ResourceType type, uint id) => type switch
        {
            ResourceType.UniformBuffer => GpuResourceKind.UniformBuffer,
            ResourceType.SeparateImage => GpuResourceKind.TextureReadOnly,
            ResourceType.StorageImage => GpuResourceKind.TextureReadWrite,
            ResourceType.SeparateSamplers => GpuResourceKind.Sampler,
            // A storage block's writability is a decoration on the BLOCK rather than on the variable, so it is
            // read through the buffer-block list. readonly in GLSL puts NonWritable there, and that is the only
            // thing separating the engine's two structured-buffer kinds.
            _ => Writable(cross, compiler, id)
                ? GpuResourceKind.StructuredBufferReadWrite
                : GpuResourceKind.StructuredBufferReadOnly,
        };

        static unsafe bool Writable(Cross cross, Compiler* compiler, uint id)
        {
            Decoration* decorations;
            nuint count;
            if (cross.CompilerGetBufferBlockDecorations(compiler, id, &decorations, &count) != Result.Success)
                return true;

            for (nuint i = 0; i < count; i++)
                if (decorations[i] == Decoration.NonWritable) return false;
            return true;
        }

        // The reflected name, which nothing BINDS on and which is kept because a diff of two reflections is far
        // more readable with it than without. #586 measured that 83 of 141 elements reflect with no name at all,
        // every one of them a texture or a sampler, so an empty string here is the normal case and not a fault.
        static unsafe string Name(Cross cross, Compiler* compiler, ReflectedResource resource)
        {
            string? instance = cross.CompilerGetNameS(compiler, resource.Id);
            if (!string.IsNullOrEmpty(instance)) return instance;
            return cross.CompilerGetNameS(compiler, resource.BaseTypeId) ?? string.Empty;
        }

        // Named separately from the kind map so the failure can carry the shader label: a format the mirror does
        // not model is a real stop, and "which shader" is the first thing the reader asks.
        static unsafe GpuVertexElementFormat Format(Cross cross, Compiler* compiler, ReflectedResource resource,
            string tag)
        {
            CrossType* type = cross.CompilerGetTypeHandle(compiler, resource.BaseTypeId);
            Basetype basetype = cross.TypeGetBasetype(type);
            uint components = cross.TypeGetVectorSize(type);

            if (basetype == Basetype.FP32)
            {
                switch (components)
                {
                    case 1: return GpuVertexElementFormat.Float1;
                    case 2: return GpuVertexElementFormat.Float2;
                    case 3: return GpuVertexElementFormat.Float3;
                    case 4: return GpuVertexElementFormat.Float4;
                    default: break;
                }
            }

            throw new ShaderValidationException(
                $"{tag}: the module declares a vertex input of format {basetype} x{components}, which the engine's "
                + "GpuVertexElementFormat mirror does not model (it covers Float1 to Float4, the set the "
                + "renderers declare). Add the format to the mirror and to both directions of the map.");
        }
    }
}
