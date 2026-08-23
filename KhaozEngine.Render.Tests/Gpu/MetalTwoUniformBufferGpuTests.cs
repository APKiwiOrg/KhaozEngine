using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// MM6'S MEASUREMENT: DOES A PIPELINE THAT READS TWO UNIFORM BUFFERS BIND THEM CORRECTLY ON THE NATIVE METAL
    /// BACKEND? Row 17 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/583).
    ///
    /// <para><b>THE QUESTION IS FOUR SESSIONS OLD AND HAS ONE SHIPPED CONSEQUENCE.</b>
    /// <c>docs/DEPENDENCY-SEAMS.md</c> carried the one-uniform-buffer-per-pipeline invariant as a fact about
    /// METAL until this row ran: any pipeline reading more than one uniform buffer mis-binds, a second buffer
    /// read only by the fragment stage reads all zero whether it sits at a second binding or in a separate set,
    /// and the read is SILENT. (It carries the measured, narrower form now, with the same practical rule on top
    /// of it.) The splat terrain and the skinned model both carry a bespoke combined UBO because of it, and the
    /// engine-wide rule for any new render path is that a pipeline reads exactly one uniform buffer at set 0
    /// binding 0. Section 2.3 rules that this is a HYPOTHESIS about the incumbent's numbering rather than a
    /// measured property of Metal, and MM6 is the measurement that settles it.</para>
    ///
    /// <para><b>IT IS ONLY READABLE BECAUSE 2.2b TOOK THE ID JOIN.</b> The native backend binds at the index the
    /// compiler put each argument at, read out of each stage's own SPIR-V module, rather than at an index counted
    /// over the declared layout array the way the incumbent does. Under the fallback that was briefly taken
    /// (#586) this backend would have used the incumbent's numbering, both hypotheses would have predicted the
    /// same answer, and these two rows could not have separated them.</para>
    ///
    /// <para><b>EVERY ROW ASSERTS A PIXEL, WHICH IS THE WHOLE POINT.</b> Section 18's row 17 records that a
    /// <c>[GpuFact]</c> asserting only no-throw is how the all-black splat terrain shipped, and this claim is
    /// specifically about WHICH BYTES A SHADER READ. Nothing but a value read back can answer it, so both rows
    /// draw a full-screen triangle whose colour is composed from both buffers and read a texel back through the
    /// seam's own <c>CopyTexture</c> and <c>Map</c>.</para>
    ///
    /// <para><b>AND THE COMPOSITION IS BUILT SO THAT EVERY FAILURE IS A DIFFERENT COLOUR.</b> The first buffer
    /// holds <c>(1, 0, 0, 1)</c> and the second <c>(0, 1, 0, 1)</c>, and the shader writes
    /// <c>(A.r, B.g, A.g + B.r)</c>. Both landing is YELLOW. The second reading all zero, which is the recorded
    /// constraint, is RED. The second aliasing the first is MAGENTA, the first aliasing the second is CYAN, and
    /// nothing bound at all is BLACK. A probe whose only failure mode was black could not tell a mis-bind from a
    /// pipeline that never bound, which is the same trap row 14's ABI probe was built around.</para>
    ///
    /// <para><b>THE INCUMBENT IS MEASURED BESIDE IT AND ASSERTED ON.</b> A pass on the native backend alone would
    /// not say the constraint moved: it would also be the reading if the constraint had quietly stopped
    /// reproducing anywhere, in which case the answer is about a toolchain version rather than about this
    /// backend. So each row runs the SAME shape through <see cref="GpuBackendKind.Metal"/> as a control and writes
    /// what it read into the test output. The control is RECORDED rather than asserted, deliberately: asserting
    /// that the incumbent still mis-binds would be a test that goes red the day somebody else fixes it, which is a
    /// failing suite reporting good news.</para>
    ///
    /// <para><b>NO SHADER CHANGES ON THE STRENGTH OF THIS (M-B4).</b> The invariant stays in force whatever these
    /// two rows read. A pass authorises FILING its removal as work with its own gates on all three backends, and
    /// nothing else, because the Veldrid Metal leg still ships and still numbers its buffers by declaration
    /// order.</para>
    ///
    /// <para><b>DORMANT OFF macOS RATHER THAN SKIPPED</b>, which is phase 3's row-19 lesson: under
    /// <c>KE_GPU_TESTS=1</c> the Vulkan and Direct3D 11 legs run this assembly in strict mode where a skip is a
    /// failure, so each row returns early with the reason recorded instead.</para>
    ///
    /// <para><b>IT SITS IN <c>NativeDeviceLifecycle</c></b> because it builds a whole <c>MTLDevice</c> and queue
    /// beside the suite's own, and because it stands the incumbent up next to it.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalTwoUniformBufferGpuTests
    {
        const uint Size = 4;

        // THE TWO VALUES, CHOSEN SO EVERY MIS-BIND IS A DISTINCT COLOUR. Only 0 and 1 appear, so the readback
        // compares exactly against an 8-bit target with no rounding to reason about.
        static readonly Vector4 FirstValue = new(1f, 0f, 0f, 1f);
        static readonly Vector4 SecondValue = new(0f, 1f, 0f, 1f);

        readonly ITestOutputHelper _out;

        public MetalTwoUniformBufferGpuTests(ITestOutputHelper output) => _out = output;

        /// <summary>
        /// MM6, FIRST HALF: A PIPELINE WHOSE VERTEX STAGE READS TWO UNIFORM BUFFERS, one at set 0 and one at set
        /// 1, with both values reaching the pixel.
        ///
        /// <para><b>THIS IS THE SHAPE <c>GpuSkinningReproGpuTests</c> VARIANT 3 REPRODUCED THE CORRUPTION IN.</b>
        /// That row records a vertex stage reading a frame UBO at set 0 and the bone palette at set 1 losing every
        /// bone past the first, offscreen, on the incumbent, and its fix was to fold the matrix into the bone
        /// buffer so the vertex stage reads exactly ONE resource buffer. What is measured here is the same two-set
        /// vertex-stage read with the values arranged so the answer is a colour rather than an occupancy
        /// count.</para>
        /// </summary>
        [GpuFact]
        public void TheVertexStageReadsBothOfItsUniformBuffers()
        {
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;

            Color native = MeasureOnNative(TwoInVertexVert, TwoInVertexFrag, GpuShaderStages.Vertex,
                GpuShaderStages.Vertex);

            RecordIncumbent(TwoInVertexVert, TwoInVertexFrag, GpuShaderStages.Vertex, GpuShaderStages.Vertex);

            Assert.Equal(Yellow, native);
        }

        /// <summary>
        /// MM6, SECOND HALF: A FRAGMENT-ONLY SECOND UNIFORM BUFFER AT SET 1, which is the half the seam doc says
        /// turned the constraint from a statement about the vertex stage into a statement about the whole
        /// PIPELINE. The first buffer is read by both stages, which is the shipped frame-UBO shape, and the second
        /// is referenced by the fragment function alone.
        ///
        /// <para><b>THE VERTEX STAGE READS THE FIRST BUFFER FOR ITS <c>w</c>,</b> so that read is real rather
        /// than something a compiler may drop: if the vertex stage's own view of set 0 came back zero the triangle
        /// would collapse and the row would read BLACK, which is a different answer from the fragment-side ones
        /// and is named as such in the diagnosis below.</para>
        /// </summary>
        [GpuFact]
        public void AFragmentOnlySecondUniformBufferAtSetOneIsRead()
        {
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;

            Color native = MeasureOnNative(FragmentOnlyVert, FragmentOnlyFrag,
                GpuShaderStages.Vertex | GpuShaderStages.Fragment, GpuShaderStages.Fragment);

            RecordIncumbent(FragmentOnlyVert, FragmentOnlyFrag,
                GpuShaderStages.Vertex | GpuShaderStages.Fragment, GpuShaderStages.Fragment);

            Assert.Equal(Yellow, native);
        }

        /// <summary>
        /// THE SHAPE THE RECORD ACTUALLY FAILED IN, and the row that discriminates when the two above do not: the
        /// vertex function reads set 0 and NOTHING ELSE, and the fragment function reads set 1 and nothing else.
        ///
        /// <para><b>IT IS HERE BECAUSE THE FIRST TWO ROWS CAME BACK YELLOW ON BOTH BACKENDS.</b> That is a real
        /// answer rather than a bad one, and it says the recorded constraint is narrower than
        /// <c>docs/DEPENDENCY-SEAMS.md</c> states it: a pipeline reading two uniform buffers does not mis-bind
        /// per se, on either backend. What the incumbent's numbering can disagree with the emission about is a
        /// stage that references FEWER buffers than the pipeline declares before them, because the incumbent
        /// counts every element declared in the preceding sets while the cross-compiler numbers only the
        /// arguments the stage it is emitting actually references.</para>
        ///
        /// <para><b>AND THAT IS PRECISELY THE SHIPPED SHAPE THE SEAM DOC RECORDS AS BLACK.</b> Its last paragraph
        /// names an earlier skinned pipeline that kept the frame UBO fragment-only in a second set and rendered
        /// every skinned mesh black, with the vertex stage reading the combined bone buffer alone. So this row is
        /// that pipeline in miniature, with the values arranged so the answer is a colour.</para>
        ///
        /// <para><b>THE FIRST BUFFER REACHES THE PIXEL AS AN INTERPOLANT,</b> because the fragment function may
        /// not reference it without destroying the shape being measured. A vertex-side mis-bind is therefore
        /// visible here too, as a missing red rather than as a missing green.</para>
        /// </summary>
        [GpuFact]
        public void ASecondUniformBufferReadByTheFragmentStageAloneIsRead()
        {
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;

            Color native = MeasureOnNative(SplitStageVert, SplitStageFrag, GpuShaderStages.Vertex,
                GpuShaderStages.Fragment);

            // THIS ROW'S CONTROL IS THE ONE THAT PROVOKES METAL'S API VALIDATION, and it does so by
            // construction: the whole point of the split-stage shape is that the incumbent writes the fragment's
            // buffer at index 1 while the emitted function reads index 0, so the draw really does leave a
            // declared fragment buffer unbound. The layer is entitled to object, and its default error mode
            // aborts the host, which on the metal-native leg would take the other six thousand rows down with
            // it. The NATIVE measurement above is untouched and still runs under validation, which is the half
            // that asserts anything.
            //
            // The layer independently confirming MM6's mechanism is worth recording rather than only working
            // around: "Fragment Function(main0): missing Buffer binding at index 0" is the incumbent's
            // mis-binding, seen from outside this repository's own reasoning about it (2.3a of the design).
            if (!MetalValidationDormancy.StandDown(_out,
                "reproduces the incumbent's mis-binding on purpose, which the layer sees as a draw with an "
                + "unbound fragment buffer"))
            {
                RecordIncumbent(SplitStageVert, SplitStageFrag, GpuShaderStages.Vertex,
                    GpuShaderStages.Fragment);
            }

            Assert.Equal(Yellow, native);
        }

        /// <summary>
        /// WHY THE THIRD SHAPE IS THE ONE THAT SEPARATES THEM, PINNED DEVICE-FREE. The pixel rows above say WHAT
        /// each backend read. This one says WHY, and it runs on every <c>dotnet test</c> on the free Linux leg,
        /// so the two halves the explanation rests on, the EMISSION and this engine's reproduction of the
        /// incumbent's arithmetic, stay checked on the days nobody has a Mac in front of them.
        ///
        /// <para><b>WHAT IT DOES NOT PIN IS THE INCUMBENT ITSELF.</b> Veldrid's live binding is recorded by the
        /// rows above and asserted by nothing, which is the deliberate trade in the control's own header. So the
        /// day Veldrid numbers its buffers the way the emission does, nothing here goes red: the control quietly
        /// reads yellow, and what goes stale is the recorded table in 2.3a and the measurement in the body of
        /// #604. A changed control reading is the signal to refresh both, and it arrives in this file's test
        /// output rather than as a failure.</para>
        ///
        /// <para><b>18.0.0 TURNED THIS ROW OVER, AND THAT IS THE ROW'S CONTENT NOW.</b> Until row 10 (#693) the
        /// index was SPIRV-Cross's to choose, and this program was the constructed counter-example: a fragment
        /// function referencing only the set-1 buffer was emitted at <c>buffer(0)</c> while a count over the
        /// declared array put it at <c>buffer(1)</c>, because set 0 declares a buffer the fragment never
        /// mentions. The engine authors the index now, walking the reflected layouts in ascending
        /// <c>(set, binding)</c> with a counter per argument table, which is the SAME walk the per-kind
        /// declaration-order arithmetic does. So the emission and the count agree here, on the one shape that
        /// used to separate them.</para>
        ///
        /// <para><b>WHICH IS THE ONE-UBO CONSTRAINT'S MECHANISM, MEASURED GONE.</b> The constraint existed
        /// because the writer's index and the reader's index were computed by two different rules that happened
        /// to agree on the shipped set. There is one rule now, so a second uniform buffer per pipeline is a
        /// numbering question with an answer rather than a hazard.
        /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/604">#604</see> is the change that RETIRES
        /// the shipped validation still enforcing the old constraint, and row 10 deliberately did not, because
        /// that validation and its negative tests invert together or new shaders fail before reaching a
        /// device.</para>
        /// </summary>
        [Fact]
        public void TheSplitStageProgramNowAgreesWithTheCount_BecauseTheIndexIsAuthored()
        {
            MetalShaderIndexTable table = MetalShaderBuild.Pair(SplitStageVert, SplitStageFrag, null,
                "MM6SplitStage").Table;

            Assert.True(table.TryGetIndex(1, 0, MetalShaderStage.Fragment, out MetalIndexTableEntry emitted),
                "the fragment stage has no entry for set 1 binding 0, so this program is not the shape MM6's "
                + "third row measures any more.");
            Assert.Equal(MetalIndexSpace.Buffer, emitted.Space);

            // THE INCUMBENT'S ARITHMETIC, READ OFF M-T3'S STANDING COPY rather than written a third time. That
            // copy is the one the shipped-corpus assertion runs on, so a correction there reaches this row too,
            // and 2.2b's rule survives the sharing: the per-kind count is a COMPARISON here and a binding path
            // nowhere.
            int counted = MetalShaderIndexTableTests.IncumbentIndices(table.Layouts)[(1, 0)].Buffer;
            _out.WriteLine($"set 1 binding 0, fragment stage: the emission put it at buffer({emitted.Index}), a "
                + $"per-kind declaration-order count puts it at buffer({counted}).");

            Assert.Equal(1, counted);
            Assert.Equal(counted, emitted.Index);
        }

        // ---- The measurement ---------------------------------------------------------------------------------

        /// <summary>Both buffers read: the answer MM6 bets on.</summary>
        static Color Yellow => new(1f, 1f, 0f, 1f);

        // THE NATIVE BACKEND, NAMED RATHER THAN RESOLVED, and asserted to be what came back. A selection that fell
        // back to the incumbent would measure Veldrid and report it as the native backend, which is the one
        // failure this row could not see from its own output.
        Color MeasureOnNative(string vertex, string fragment, GpuShaderStages first, GpuShaderStages second)
        {
            using GpuDeviceContext native = GpuDeviceContext.CreateHeadless(GpuBackendKind.MetalNative);
            Assert.Equal(GpuBackendKind.MetalNative, native.Backend);

            Color measured = Measure(native.GpuDevice, vertex, fragment, first, second);
            _out.WriteLine($"native Metal ({native.Capabilities.DeviceName}): {Describe(measured)}");
            return measured;
        }

        // THE CONTROL. Recorded, never asserted: what makes MM6 readable is that the two backends can be compared
        // on one machine in one process, and what would make this suite fragile is a row that fails when the
        // incumbent's own behaviour improves. A control that cannot be taken at all is recorded as that.
        void RecordIncumbent(string vertex, string fragment, GpuShaderStages first, GpuShaderStages second)
        {
            // NO VALIDATION STAND-DOWN HERE, and that is a scoping decision rather than an omission. ONE of the
            // three rows provokes the layer, the split-stage one, and it stands down at its own call site. The
            // other two were measured under the armed layer and record BOTH buffers read cleanly, with no
            // objection, because the incumbent's count and the emission agree on those two shapes. Standing all
            // three down would have cost two clean control readings on the one leg that runs the layer, which is
            // the leg whose output is the reason to take a control at all.
            try
            {
                using GpuDeviceContext incumbent = GpuDeviceContext.CreateHeadless(GpuBackendKind.Metal);
                if (incumbent.Backend != GpuBackendKind.Metal)
                {
                    _out.WriteLine($"control not taken: the incumbent came up on {incumbent.Backend}.");
                    return;
                }

                Color measured = Measure(incumbent.GpuDevice, vertex, fragment, first, second);
                _out.WriteLine($"incumbent Metal via Veldrid ({incumbent.Capabilities.DeviceName}): "
                    + Describe(measured));
            }
            catch (Exception ex)
            {
                // The control is diagnostic. A machine that cannot stand the incumbent up beside the native
                // device still has a native measurement worth taking, and losing the row to the control's failure
                // would trade the measurement for its footnote.
                _out.WriteLine("control not taken: the incumbent Metal device threw (" + ex.GetType().Name + ": "
                    + ex.Message + ").");
            }
        }

        /// <summary>
        /// ONE DRAW, TWO UNIFORM BUFFERS, ONE TEXEL BACK, through the seam alone so the same body runs on both
        /// backends. Everything that differs between them is behind <see cref="IGpuDevice"/>, which is what makes
        /// the A and the B of this measurement comparable at all.
        /// </summary>
        static Color Measure(IGpuDevice gd, string vertexGlsl, string fragmentGlsl, GpuShaderStages firstStages,
            GpuShaderStages secondStages)
        {
            IGpuResourceFactory f = gd.Factory;

            using IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(
                Size, Size, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, target);

            // TWO SETS RATHER THAN TWO BINDINGS IN ONE SET, which is the arrangement both halves of the recorded
            // constraint were measured in: the skinning repro's frame UBO at set 0 with bones at set 1, and the
            // fragment-only frame block that rendered every skinned mesh black.
            using IGpuBuffer first = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
            using IGpuBuffer second = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
            using IGpuBuffer vertices = f.CreateBuffer(
                new GpuBufferDescription(6 * sizeof(float), GpuBufferUsage.VertexBuffer));

            using IGpuResourceLayout firstLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("First", GpuResourceKind.UniformBuffer, firstStages)));
            using IGpuResourceLayout secondLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Second", GpuResourceKind.UniformBuffer, secondStages)));

            using IGpuResourceSet firstSet = f.CreateResourceSet(
                new GpuResourceSetDescription(firstLayout, first));
            using IGpuResourceSet secondSet = f.CreateResourceSet(
                new GpuResourceSetDescription(secondLayout, second));

            using IGpuShaderSet shaders = f.CreateShadersFromSpirv(vertexGlsl, fragmentGlsl);
            using IGpuPipeline pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                    GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = [firstLayout, secondLayout],
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>
                {
                    new(new GpuVertexElement("Pos", GpuVertexElementFormat.Float2)),
                },
                Outputs = fb.Outputs,
            });

            Vector4 firstValue = FirstValue;
            Vector4 secondValue = SecondValue;
            gd.UpdateBuffer(first, 0, in firstValue);
            gd.UpdateBuffer(second, 0, in secondValue);
            gd.UpdateBuffer(vertices, 0, FullScreenTriangle);

            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.SetFramebuffer(fb);

                // BLACK, so "nothing was drawn" and "nothing was bound" are the same reading rather than two
                // different ones, and neither can be mistaken for a partial success.
                cl.ClearColorTarget(0, Color.Black);
                cl.SetPipeline(pipeline);
                cl.SetGraphicsResourceSet(0, firstSet);
                cl.SetGraphicsResourceSet(1, secondSet);
                cl.SetVertexBuffer(0, vertices);
                cl.Draw(3);
                cl.End();
                gd.Submit(cl);
            }

            gd.WaitForIdle();

            // A DEVICE THAT LOST ITSELF WOULD READ BLACK AND MEAN SOMETHING ELSE ENTIRELY, so the latch is checked
            // before the pixel is believed. The incumbent answers null here as well, through the same member.
            Assert.Null(gd.Diagnostics.DeviceLossReason);

            byte[] pixels = GpuReadback.ToRgba(gd, target, (int)Size, (int)Size);
            int at = (((int)Size / 2) * (int)Size + ((int)Size / 2)) * 4;
            return new Color(pixels[at] / 255f, pixels[at + 1] / 255f, pixels[at + 2] / 255f,
                pixels[at + 3] / 255f);
        }

        // WHAT A COLOUR MEANS, so a failure reports a diagnosis rather than three floats. Every arm names a
        // distinct binding outcome, which is the property that makes this a measurement.
        static string Describe(Color c)
        {
            string what = c switch
            {
                { R: 1f, G: 1f, B: 0f } => "BOTH uniform buffers read (the second is not mis-binding)",
                { R: 1f, G: 0f, B: 0f } => "the SECOND uniform buffer read all zero, which is the recorded "
                    + "one-UBO constraint reproducing",
                { R: 1f, G: 0f, B: 1f } => "the second binding ALIASED the first buffer",
                { R: 0f, G: 1f, B: 1f } => "the first binding ALIASED the second buffer",
                { R: 0f, G: 0f, B: 0f } => "nothing reached the target: either the draw did not rasterize or "
                    + "neither buffer was bound",
                _ => "an outcome none of the five predicted shapes covers",
            };

            return $"({c.R}, {c.G}, {c.B}, {c.A}) - {what}.";
        }

        // The full-screen triangle every fullscreen pass in this engine uses, in clip space, so one texel answers
        // for the whole draw.
        static ReadOnlySpan<float> FullScreenTriangle => [-1f, -1f, 3f, -1f, -1f, 3f];

        // ---- The two programs --------------------------------------------------------------------------------

        // MM6's first shape: BOTH uniform buffers referenced by the VERTEX function, at two sets. The fragment
        // function reads no resource at all, so the vertex stage is the only place a mis-bind can come from.
        const string TwoInVertexVert = @"#version 450
layout(set = 0, binding = 0) uniform First { vec4 A; };
layout(set = 1, binding = 0) uniform Second { vec4 B; };
layout(location = 0) in vec2 Pos;
layout(location = 0) out vec4 vTint;
void main()
{
    gl_Position = vec4(Pos, 0.0, 1.0);
    vTint = vec4(A.r, B.g, A.g + B.r, 1.0);
}
";

        const string TwoInVertexFrag = @"#version 450
layout(location = 0) in vec4 vTint;
layout(location = 0) out vec4 oColour;
void main() { oColour = vTint; }
";

        // MM6's second shape: set 0 read by BOTH stages, set 1 referenced ONLY by the fragment function. The
        // vertex stage reads its buffer for the clip w, which is a use no compiler can drop and which fails
        // visibly (a collapsed triangle) rather than silently.
        const string FragmentOnlyVert = @"#version 450
layout(set = 0, binding = 0) uniform First { vec4 A; };
layout(location = 0) in vec2 Pos;
void main() { gl_Position = vec4(Pos, 0.0, A.a); }
";

        const string FragmentOnlyFrag = @"#version 450
layout(set = 0, binding = 0) uniform First { vec4 A; };
layout(set = 1, binding = 0) uniform Second { vec4 B; };
layout(location = 0) out vec4 oColour;
void main() { oColour = vec4(A.r, B.g, A.g + B.r, 1.0); }
";

        // MM6's third shape, the one the record failed in: each stage references exactly ONE of the two buffers,
        // so the fragment stage's own numbering starts at the set-1 buffer while a count over the declared array
        // does not. The first buffer travels to the fragment as an interpolant because the fragment function may
        // not reference it without dissolving the case.
        const string SplitStageVert = @"#version 450
layout(set = 0, binding = 0) uniform First { vec4 A; };
layout(location = 0) in vec2 Pos;
layout(location = 0) out vec4 vFirst;
void main()
{
    gl_Position = vec4(Pos, 0.0, 1.0);
    vFirst = A;
}
";

        const string SplitStageFrag = @"#version 450
layout(set = 1, binding = 0) uniform Second { vec4 B; };
layout(location = 0) in vec4 vFirst;
layout(location = 0) out vec4 oColour;
void main() { oColour = vec4(vFirst.r, B.g, vFirst.g + B.r, 1.0); }
";
    }
}
