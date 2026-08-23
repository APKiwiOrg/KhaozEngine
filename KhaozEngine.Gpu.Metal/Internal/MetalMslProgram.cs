using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// One stage of an emitted program, as the DEVICE half needs it: the MSL text to compile into an
    /// <c>MTLLibrary</c>, and the entry-point name to ask that library for.
    /// <para>
    /// THE SPIR-V IS DELIBERATELY NOT HERE. Nothing downstream reads it, so carrying it on the program would be
    /// a lie the day the emission arrives from a cache instead of from the front end. Until 18.0.0 the index
    /// table's id join needed each stage's own module and a second record carried it for exactly as long as the
    /// join lasted. The authored indices need no module at all.
    /// </para>
    /// </summary>
    /// <param name="Stage">Which stage this is.</param>
    /// <param name="EntryPointName">The function's name AS EMITTED, read rather than assumed to be
    /// <c>main0</c> (M-S5).</param>
    /// <param name="Msl">The emitted MSL source for this stage.</param>
    internal readonly record struct MetalMslStage(MetalShaderStage Stage, string EntryPointName, string Msl);

    /// <summary>
    /// A WHOLE PROGRAM'S EMISSION, DEVICE-FREE: every stage's MSL and entry-point name, plus the binding table
    /// read off that emission. This is what <see cref="MetalShaderBuild"/> produces and what the device half turns
    /// into libraries and functions.
    ///
    /// <para>
    /// NOTHING IN HERE TOUCHES METAL, which is what makes the whole shader path except the final
    /// <c>newLibraryWithSource:</c> testable on the free Linux leg, over every shipped program, on every
    /// <c>dotnet test</c>. Section 2.2b rests on that: the failure mode this area has is "everything compiles and
    /// every pixel is wrong", and the answer is a device-free assertion taken before the first golden run.
    /// </para>
    /// <para>
    /// THE TABLE AND THE ENTRY-POINT NAMES TRAVEL TOGETHER, and any cache in front of this has to carry BOTH
    /// (2.2b, pin 6). A cache hit skips the emission, and neither the names nor the per-stage use the table is
    /// keyed on can be re-derived from anything the hit hands back. The INDICES can, since 18.0.0, and the
    /// payload deliberately does not carry them: see <see cref="MetalShaderIndexTable.FromCache"/>.
    /// </para>
    /// </summary>
    internal sealed class MetalMslProgram
    {
        readonly MetalMslStage[] _stages;

        internal MetalMslProgram(MetalMslStage[] stages, MetalShaderIndexTable table)
        {
            ArgumentNullException.ThrowIfNull(stages);
            ArgumentNullException.ThrowIfNull(table);

            _stages = stages;
            Table = table;
        }

        /// <summary>Every stage of the program, in the order the emission produced them.</summary>
        internal IReadOnlyList<MetalMslStage> Stages => _stages;

        /// <summary>Where the engine authored each declared element, and which stages bind it (M-B1,
        /// 2.2b, #693).</summary>
        internal MetalShaderIndexTable Table { get; }

        /// <summary>The one stage of a compute program, or the named stage of a graphics pair.</summary>
        /// <exception cref="ShaderValidationException">The program has no such stage, which a caller asking for
        /// one it did not compile would hit.</exception>
        internal MetalMslStage StageOf(MetalShaderStage stage)
        {
            foreach (MetalMslStage candidate in _stages)
                if (candidate.Stage == stage) return candidate;

            throw new ShaderValidationException(
                $"this Metal program has no {stage.ToString().ToLowerInvariant()} stage, so there is no library "
                + "function to create from it.");
        }
    }
}
