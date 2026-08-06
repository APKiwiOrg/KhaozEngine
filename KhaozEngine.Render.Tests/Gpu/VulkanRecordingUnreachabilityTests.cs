using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE STRUCTURAL ENFORCEMENT OF DECISIONS V-M11 AND V-D2: nothing that could create an image view, and
    /// nothing that could allocate or write a descriptor set, is REACHABLE from the recording type. A draw-time
    /// view or a draw-time descriptor allocation is therefore a compile error rather than a number a counter
    /// happened to find at zero.
    ///
    /// <para><b>WHY UNREACHABILITY RATHER THAN A COUNTER.</b> The device-free native-call budget counts through
    /// <see cref="IVkCmdSink"/>, which covers binds, draws, dispatches and barriers and nothing else. Neither
    /// <c>vkCreateImageView</c> nor <c>vkAllocateDescriptorSets</c> is any of those, so NO counting seam can see
    /// them: both drafts of the design claimed one could and neither could. A call that cannot be MADE is a
    /// stronger guarantee than a call that is counted and found to be zero.</para>
    ///
    /// <para><b>AND THE EVIDENCE IS SPECIFIC.</b> All 25 <c>DEVICE_REMOVED</c> stacks in
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/423 surfaced inside a LAZY VIEW CONSTRUCTOR on the draw
    /// path, so lazy creation put an allocation on the hot path and put it on the exact path a broken device makes
    /// fail. That is X1's decision on X1's evidence, and it is worth restating in a Vulkan seat where
    /// <c>vkCreateImageView</c> looks cheap enough to do at a bind.</para>
    ///
    /// <para><b>THIS FILE IS SHARED WITH ROW 10</b> (https://github.com/APKiwiOrg/KhaozEngine/issues/520). Row 9
    /// lands the walk and the view half of the forbidden set. Row 10 adds its descriptor pool type to
    /// <see cref="ForbiddenFromRecording"/> and nothing else: the walk, the assertion and the diagnostic are
    /// already here, which is the point of landing the shape one row early.</para>
    /// </summary>
    public sealed class VulkanRecordingUnreachabilityTests
    {
        /// <summary>
        /// THE TYPES A RECORDER MAY NOT REACH. Names rather than <c>typeof</c>, so a row that has not landed yet
        /// can be listed without the file failing to compile, and so a type RENAMED out from under this test fails
        /// loudly at <see cref="EveryForbiddenType_ExistsOrIsNamedAsPending"/> rather than quietly passing.
        /// </summary>
        static readonly string[] ForbiddenFromRecording =
        [
            // V-M11: the view factory. CreateImageView lives on this seam and on nothing else.
            "IVulkanResourceApi",
            "VulkanResourceApi",
            // The factory that calls it, which is the other way a recorder could reach a creation.
            "VulkanResourceFactory",
            // V-D2: the descriptor pool manager, added by row 10
            // (https://github.com/APKiwiOrg/KhaozEngine/issues/520) when it exists.
        ];

        /// <summary>
        /// THE RECORDING TYPE REACHES NONE OF THEM, transitively, through any field it holds. The walk is over
        /// FIELDS rather than over the whole type graph because a field is what a recorder could actually call
        /// through at draw time: a type it merely names in a signature it never invokes cannot allocate anything.
        /// </summary>
        [Fact]
        public void TheRecordingType_ReachesNoViewFactory()
        {
            IReadOnlyCollection<Type> reachable = ReachableFrom(typeof(VulkanCommandList));

            string[] violations = reachable
                .Where(t => ForbiddenFromRecording.Contains(t.Name, StringComparer.Ordinal))
                .Select(t => t.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.True(violations.Length == 0,
                "VulkanCommandList can reach " + string.Join(", ", violations)
                + " through its fields, which makes a draw-time image view or a draw-time descriptor allocation "
                + "expressible. Decisions V-M11 and V-D2 are enforced by unreachability rather than by a counter, "
                + "because no counting seam can see vkCreateImageView or vkAllocateDescriptorSets. Every view is "
                + "created at RESOURCE creation and every descriptor set at SET creation.");
        }

        /// <summary>
        /// NO METHOD ON ANYTHING A RECORDER REACHES TAKES OR RETURNS ONE EITHER, which closes the other door: a
        /// field-only walk would miss a recorder that was HANDED a factory as a parameter at record time.
        /// </summary>
        [Fact]
        public void NothingAReorderReachesTakesAViewFactoryAsAParameter()
        {
            IReadOnlyCollection<Type> reachable = ReachableFrom(typeof(VulkanCommandList));

            var violations = new List<string>();
            foreach (Type type in reachable)
            {
                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.DeclaredOnly))
                {
                    if (Forbidden(method.ReturnType)) violations.Add($"{type.Name}.{method.Name} returns it");

                    foreach (ParameterInfo parameter in method.GetParameters())
                    {
                        if (Forbidden(parameter.ParameterType))
                            violations.Add($"{type.Name}.{method.Name} takes it");
                    }
                }
            }

            Assert.True(violations.Count == 0, string.Join("; ", violations));
        }

        /// <summary>
        /// THE FORBIDDEN LIST NAMES REAL TYPES, so a rename cannot quietly empty it. A list of names is what lets
        /// row 10 add a type that does not exist yet, and this is the guard that stops the same flexibility from
        /// turning the assertion above into a tautology.
        /// </summary>
        [Fact]
        public void EveryForbiddenType_ExistsOrIsNamedAsPending()
        {
            Type[] all = typeof(VulkanCommandList).Assembly.GetTypes();

            foreach (string name in ForbiddenFromRecording)
            {
                Assert.True(all.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal)),
                    $"The unreachability list names {name}, which no type in KhaozEngine.Gpu.Vulkan has. Either "
                    + "it was renamed and this list was not, or the row that was going to create it changed its "
                    + "mind. Both are edits somebody has to make here deliberately.");
            }
        }

        /// <summary>
        /// AND THE WALK ITSELF WORKS, which is the assertion an unreachability test most needs and most often
        /// lacks: a walk that silently reached nothing would pass every row above forever. The resource FACTORY
        /// does reach the view factory, by construction, so finding it there proves the walk can find one.
        /// </summary>
        [Fact]
        public void TheWalk_FindsTheViewFactoryWhereItReallyIs()
        {
            IReadOnlyCollection<Type> reachable = ReachableFrom(typeof(VulkanResourceFactory));

            Assert.Contains(reachable, t => string.Equals(t.Name, "IVulkanResourceApi", StringComparison.Ordinal));
        }

        static bool Forbidden(Type type)
            => ForbiddenFromRecording.Contains(Unwrap(type).Name, StringComparer.Ordinal);

        // Every type reachable from `root` through instance and static FIELDS, transitively, restricted to the
        // backend's own assembly. Restricted because the framework's type graph is unbounded and because a
        // violation would be one of this package's own types by definition.
        static IReadOnlyCollection<Type> ReachableFrom(Type root)
        {
            Assembly backend = typeof(VulkanCommandList).Assembly;

            var seen = new HashSet<Type>();
            var pending = new Stack<Type>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                Type type = Unwrap(pending.Pop());

                if (type.Assembly != backend || !seen.Add(type)) continue;

                foreach (FieldInfo field in type.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.DeclaredOnly))
                {
                    pending.Push(field.FieldType);
                }

                // An interface a reachable type implements is reachable too: a recorder holding an interface can
                // call anything on it, and the implementation is chosen at run time.
                foreach (Type contract in type.GetInterfaces()) pending.Push(contract);
            }

            return seen;
        }

        // Arrays, by-ref parameters, nullables and generic arguments all hide the real type one level down, and a
        // walk that did not unwrap them would miss a field of type VulkanResourceFactory[] entirely.
        static Type Unwrap(Type type)
        {
            while (type.HasElementType) type = type.GetElementType()!;

            return type;
        }
    }
}
