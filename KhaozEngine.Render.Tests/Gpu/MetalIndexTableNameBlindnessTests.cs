using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE MECHANICAL FORM OF ROW 10's NAME BLINDNESS: no member of
    /// <see cref="MetalShaderIndexTable"/> reads a layout element's NAME or its STAGE VISIBILITY.
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/594.
    ///
    /// <para><b>WHY IT IS A RULE AND NOT A COMMENT, WITH A MEASURED SIZE.</b> Row 10
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/576) deduplicates tables on
    /// <c>ContentKey</c>, which renders the layout SHAPE and every entry and deliberately renders no element
    /// name and no visibility. That is sound only while nothing the table answers reads either. Over the shipped
    /// catalog 42 programs produce 17 distinct tables, 25 programs merge onto an earlier program's table, and 16
    /// of those 25 disagree on at least one element NAME
    /// (<see cref="MetalIndexTableDedupTests.EveryShippedProgram_DedupsOnlyOntoTablesItAgreesWith"/> prints both
    /// numbers). So a member that started reading a name off <c>Layouts</c> would be reading ANOTHER PROGRAM's
    /// name in the MAJORITY of merges, not in a rare collision: a wrong diagnostic string at best, and at worst
    /// a bind resolved through a name, which is the join section 2.2b removed.</para>
    ///
    /// <para><b>WHAT THE BEHAVIOURAL PIN NEXT DOOR CANNOT CATCH.</b>
    /// <c>MetalIndexTableDedupTests.TwoTablesDifferingOnlyInElementNames_AreInterchangeable</c> catches a change
    /// to <c>ContentKey</c> or to <c>RequireLayoutShape</c>, because it drives both. It cannot catch a NEW member
    /// that reads a name, because nothing calls that member yet. This walks the IL instead, so the constraint
    /// holds for a member nobody has written a test for.</para>
    ///
    /// <para><b>ROOTED AT THE TABLE'S OWN METHODS AND TRANSITIVE FROM THERE, WITHIN THE PACKAGE.</b> The roots
    /// are what this type declares, plus its compiler-generated nested types: <c>Entries()</c> is an iterator,
    /// so its body lives in a state machine rather than in the method, and a name read there would be exactly
    /// as observable. From each root the walk follows call sites into <c>KhaozEngine.Gpu.Metal</c>, so a name
    /// read through a helper in another type of this package is caught. It was depth-1 when it landed, which
    /// made the prose's promise ("a member added later that reads one is a red test") false for the shape a
    /// member would most naturally take: read it in a helper and call the helper
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/594).</para>
    ///
    /// <para><b>AND THE PACKAGE BOUNDARY IS WHERE THE PROMISE ENDS, deliberately.</b> The descent stops at the
    /// assembly edge, so a read that happens inside another assembly's method is out of scope. External
    /// readers of <c>Layouts</c> (this file's own dedup sibling, and row 11's <c>RequireLayoutShape</c> call
    /// site, which passes its own array IN rather than reading the table's) are not covered either, and the
    /// prose on <c>ContentKey</c> stays the guard for them.</para>
    ///
    /// <para><b>AND IT IS POSITIVELY CONTROLLED, in the discipline
    /// <see cref="MetalAutoreleaseArchitectureTests"/> set for the shared IL reader.</b> A rule built on a walk
    /// that finds nothing passes for the wrong reason forever. The controls below prove the walk flags a member
    /// that DOES read a name, flags one that reads a name THROUGH A HELPER while reading nothing itself, leaves
    /// one that reads only the kind, and really does read the table's own bodies, state machine included.
    /// The transitive half was also verified against the real type rather than only against a control: a
    /// member reading a name through a helper in another package type was injected into
    /// <c>MetalShaderIndexTable</c>, the rule went red with the path
    /// <c>MetalShaderIndexTable.ProbeReadsANameThroughAHelper -&gt; ProbeNameHelper.Read -&gt;
    /// GpuResourceLayoutElement.get_Name</c>, and the probe was reverted.</para>
    /// </summary>
    public sealed class MetalIndexTableNameBlindnessTests
    {
        readonly ITestOutputHelper _output;

        public MetalIndexTableNameBlindnessTests(ITestOutputHelper output) => _output = output;

        const string NameGetter = "get_" + nameof(GpuResourceLayoutElement.Name);
        const string StagesGetter = "get_" + nameof(GpuResourceLayoutElement.Stages);
        const string KindGetter = "get_" + nameof(GpuResourceLayoutElement.Kind);

        /// <summary>THE RULE.</summary>
        [Fact]
        public void NoMemberOfTheTableReadsAnElementNameOrItsStages()
        {
            string[] violations = MembersReading(TableMembers(), NameGetter, StagesGetter)
                .Select(m => DescribeWithPath(m, NameGetter, StagesGetter))
                .ToArray();

            Assert.True(violations.Length == 0,
                "These members of MetalShaderIndexTable read a layout element's Name or Stages, which its "
                + "ContentKey does not render. Tables are content-deduplicated on that key, and over the shipped "
                + "catalog 16 of the 25 programs that merge onto an earlier program's table disagree on at least "
                + "one element name, so what a member reads here is another program's name in the majority of "
                + "merges. Either resolve the element from the caller's own declared array (which is what "
                + "RequireLayoutShape does), or render the field into ContentKey so it becomes part of the "
                + "table's identity.\n"
                + string.Join("\n", violations));
        }

        /// <summary>
        /// THE POSITIVE CONTROL. Without it the row above would pass on a walk that read no IL at all, which is
        /// the failure mode every rule of this shape has. Pointed at a member that does read a name, the same
        /// detector flags it.
        /// </summary>
        [Fact]
        public void TheWalk_FlagsAControlMemberThatReadsANameOrStages()
        {
            string[] flagged = MembersReading(IlCallGraph.DeclaredMethods(typeof(NameReadingControl)),
                    NameGetter, StagesGetter)
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            _output.WriteLine(string.Join(", ", flagged));
            Assert.Equal(
                new[] { nameof(NameReadingControl.ReadsTheName), nameof(NameReadingControl.ReadsTheStages) },
                flagged);
        }

        /// <summary>
        /// THE OTHER HALF OF THE CONTROL: the detector is not simply flagging everything. A member that reads
        /// only the KIND, which is what the table legitimately does everywhere, is left alone.
        /// </summary>
        [Fact]
        public void TheWalk_LeavesAControlMemberThatReadsOnlyTheKind()
        {
            MethodBase kindOnly = typeof(NameReadingControl)
                .GetMethod(nameof(NameReadingControl.ReadsOnlyTheKind),
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;

            Assert.Empty(MembersReading(new[] { kindOnly }, NameGetter, StagesGetter));
            Assert.Single(MembersReading(new[] { kindOnly }, KindGetter));
        }

        /// <summary>
        /// THE CONTROL FOR THE TRANSITIVE HALF, and the one the rule was missing. A member that reads no name
        /// itself and calls a helper in another type that does is exactly what a later row would write, and a
        /// depth-1 walk reported it clean. The path in the output is what a violation message looks like.
        /// </summary>
        [Fact]
        public void TheWalk_FlagsAMemberThatReadsANameThroughAHelperInAnotherType()
        {
            MethodBase indirect = typeof(IndirectNameReadingControl)
                .GetMethod(nameof(IndirectNameReadingControl.ReadsTheNameThroughAHelper),
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;

            _output.WriteLine(DescribeWithPath(indirect, NameGetter, StagesGetter));
            Assert.Single(MembersReading(new[] { indirect }, NameGetter, StagesGetter));

            // And it is genuinely the indirection being caught: the member reads nothing itself.
            Assert.DoesNotContain(IlCallGraph.Callees(indirect),
                c => c.DeclaringType == typeof(GpuResourceLayoutElement));
        }

        /// <summary>
        /// AND THE WALK REALLY DOES READ THE TABLE'S OWN BODIES. The table reads element KINDS in several
        /// places, so a walk that came back with no kind read either would be broken rather than reporting a
        /// clean type, and the rule above would be vacuous.
        /// </summary>
        [Fact]
        public void TheWalk_SeesTheTablesOwnKindReads()
        {
            string[] readers = MembersReading(TableMembers(), KindGetter)
                .Select(m => m.Name)
                .ToArray();

            _output.WriteLine(string.Join(", ", readers));
            Assert.Contains("get_ContentKey", readers, StringComparer.Ordinal);
            Assert.Contains(nameof(MetalShaderIndexTable.RequireLayoutShape), readers, StringComparer.Ordinal);
        }

        /// <summary>
        /// THE STATE MACHINE IS IN THE WALKED SET, which is the one place a name read could sit and be invisible
        /// to a walk over declared methods alone: <c>Entries()</c> is an iterator, so the body a reviewer reads
        /// in the source compiles into a nested type.
        /// </summary>
        [Fact]
        public void TheWalkedSet_IncludesTheIteratorsStateMachine()
        {
            string[] declaring = TableMembers()
                .Select(m => m.DeclaringType?.Name ?? "?")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            _output.WriteLine(string.Join(", ", declaring));
            Assert.Contains(declaring, n => n.Contains(nameof(MetalShaderIndexTable.Entries), StringComparison.Ordinal)
                && n.StartsWith("<", StringComparison.Ordinal));
        }

        // Every method the table declares, plus every method of the compiler-generated types nested in it (the
        // Entries iterator's state machine and the closure holding its sort comparator). A member's body is
        // where a name read would live, so both halves are the same question.
        static IReadOnlyList<MethodBase> TableMembers()
        {
            var members = new List<MethodBase>(IlCallGraph.DeclaredMethods(typeof(MetalShaderIndexTable)));
            foreach (Type nested in typeof(MetalShaderIndexTable).GetNestedTypes(
                BindingFlags.Public | BindingFlags.NonPublic))
            {
                members.AddRange(IlCallGraph.DeclaredMethods(nested));
            }
            return members;
        }

        // Which of these members can REACH one of the named getters on GpuResourceLayoutElement, directly or
        // through a helper. Matched on the declaring type and the accessor name rather than on a MethodInfo
        // handed in, so a resolved token from another assembly compares the way a reader expects.
        static IReadOnlyList<MethodBase> MembersReading(IEnumerable<MethodBase> members, params string[] getters)
            => members.Where(m => Reaches(m, getters, new List<MethodBase>())).ToArray();

        // A violation as a path rather than a name, because with a transitive walk the member that has to
        // change is often not the member that reads the name.
        static string DescribeWithPath(MethodBase member, params string[] getters)
        {
            var path = new List<MethodBase> { member };
            Reaches(member, getters, path);
            return string.Join(" -> ", path.Select(IlCallGraph.Describe));
        }

        static bool Reaches(MethodBase method, string[] getters, List<MethodBase> path)
            => Reaches(method, getters, method.DeclaringType?.Assembly, new HashSet<MethodBase>(), path);

        // Depth-first over call sites, the shape MetalAutoreleaseArchitectureTests.Reaches already walks. The
        // descent stops at the assembly boundary: within the package a helper is just a place the same read
        // happens, and outside it the walk would be asking a question about somebody else's code (see the class
        // remarks for why the boundary is where the rule's promise ends).
        static bool Reaches(MethodBase method, string[] getters, Assembly? scope, HashSet<MethodBase> seen,
            List<MethodBase> path)
        {
            if (!seen.Add(method)) return false;

            foreach (MethodBase callee in IlCallGraph.Callees(method))
            {
                if (callee.DeclaringType == typeof(GpuResourceLayoutElement)
                    && getters.Contains(callee.Name, StringComparer.Ordinal))
                {
                    path.Add(callee);
                    return true;
                }

                if (callee.DeclaringType?.Assembly != scope) continue;

                path.Add(callee);
                if (Reaches(callee, getters, scope, seen, path)) return true;
                path.RemoveAt(path.Count - 1);
            }

            return false;
        }

        /// <summary>
        /// The control the walk is pointed at, and the reason it is a type rather than an inline expression: the
        /// rule reads IL, so a control has to BE compiled IL that reads a name.
        /// </summary>
        static class NameReadingControl
        {
            internal static string ReadsTheName(GpuResourceLayoutElement element) => element.Name;

            internal static GpuShaderStages ReadsTheStages(GpuResourceLayoutElement element) => element.Stages;

            internal static GpuResourceKind ReadsOnlyTheKind(GpuResourceLayoutElement element) => element.Kind;
        }

        /// <summary>
        /// The INDIRECT control: it reads no name itself, and calls something in another type that does. This
        /// is the shape a depth-1 walk was blind to, and the shape a member would actually take when a later
        /// row wants a name for a diagnostic string.
        /// </summary>
        static class IndirectNameReadingControl
        {
            internal static string ReadsTheNameThroughAHelper(GpuResourceLayoutElement element)
                => NameHelper.Read(element);
        }

        static class NameHelper
        {
            internal static string Read(GpuResourceLayoutElement element) => element.Name;
        }
    }
}
