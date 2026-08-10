using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// One stage of an emitted program, as the DEVICE half needs it: the MSL text to compile into an
    /// <c>MTLLibrary</c>, and the entry-point name to ask that library for.
    /// <para>
    /// THE SPIR-V IS DELIBERATELY NOT HERE. It is an input to the index table's join and nothing downstream reads
    /// it, so carrying it on the program would be a lie the day the emission arrives from a cache instead of from
    /// the front end. <see cref="MetalMslStageJoin"/> is the type that carries it, and it lives only as long as
    /// the join does.
    /// </para>
    /// </summary>
    /// <param name="Stage">Which stage this is.</param>
    /// <param name="EntryPointName">The function's name AS EMITTED, read rather than assumed to be
    /// <c>main0</c> (M-S5).</param>
    /// <param name="Msl">The emitted MSL source for this stage.</param>
    internal readonly record struct MetalMslStage(MetalShaderStage Stage, string EntryPointName, string Msl);

    /// <summary>
    /// What the index table's join reads for one stage: that stage's OWN SPIR-V module, and the resource
    /// arguments already parsed out of its emitted entry point.
    /// <para>
    /// PER-STAGE MODULES ARE THE MECHANISM RATHER THAN A DETAIL (2.2b, pin 2). Each stage renumbers its SPIR-V ids
    /// independently, so the pair's shared reflection cannot resolve either stage's argument names. Reading each
    /// stage's ids out of that stage's module is exactly what makes the id join work where the name join could
    /// not.
    /// </para>
    /// </summary>
    /// <param name="Stage">Which stage these belong to.</param>
    /// <param name="Spirv">That stage's own SPIR-V module.</param>
    /// <param name="Arguments">The resource arguments of that stage's emitted entry point, in declaration
    /// order.</param>
    internal readonly record struct MetalMslStageJoin(
        MetalShaderStage Stage, byte[] Spirv, IReadOnlyList<MetalMslArgument> Arguments);

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
    /// (2.2b, pin 6). A cache hit skips the emission, so the names and the table cannot be re-derived from
    /// anything the hit hands back. Consulting a cache before the table exists is invalid, which is why they are
    /// one object rather than two returns.
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

        /// <summary>Where the emission put each declared element, per stage (M-B1, 2.2b).</summary>
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
