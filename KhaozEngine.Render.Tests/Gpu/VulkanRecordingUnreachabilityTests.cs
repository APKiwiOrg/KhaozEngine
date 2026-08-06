using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE STRUCTURAL ENFORCEMENT OF DECISIONS V-M11 AND V-D2: nothing that could create an image view, and
    /// nothing that could allocate or write a descriptor set, is REACHABLE from the recording type, save the ONE
    /// lifetime edge named and argued below. A draw-time view or a draw-time descriptor allocation is therefore
    /// something the type graph will not express, rather than a number a counter happened to find at zero.
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
    /// <para><b>THE WALK CROSSES ONE EDGE AND EXACTLY ONE, AND THAT IS A NARROWING OF THE CLAIM RATHER THAN A
    /// WEAKENING OF THE WALK.</b> Once the walk descends into an interface-typed field's IMPLEMENTATIONS, which it
    /// must (the recorder's <c>_uploads</c> is an interface and everything behind it was invisible without that),
    /// the recorder's live graph does reach <see cref="IVulkanResourceApi"/>:
    /// <c>_uploads</c> to <see cref="VulkanListUploads"/> to <see cref="VulkanStagingArena"/> to
    /// <see cref="IVulkanStagingSource"/> to <see cref="VulkanStagingSource"/> to
    /// <see cref="VulkanResourceOwner"/>. That edge is a staging block's LIFETIME: the source creates, binds,
    /// frees and destroys the <c>VkBuffer</c> a block is. What V-M11 requires is that no VIEW FACTORY is reachable
    /// as a callable path from recording, meaning a draw cannot CREATE a view, and an allocate-and-destroy
    /// reference to the resource seam is not one.</para>
    ///
    /// <para><b>THE ALTERNATIVE WAS WEIGHED AND DOES NOT WORK.</b> Forbidding view-creating MEMBERS instead of
    /// types (walk the method surfaces and refuse anything that creates a view) fails identically, because the one
    /// that creates one is <c>IVulkanResourceApi.CreateImageView</c>, on the very type the edge leads to: the same
    /// reachable set, the same answer. Any member-level rule over this graph is the type-level rule with more
    /// code. So the edge is named instead, in one place, with its reasoning here, and
    /// <see cref="TheAllowedEdge_IsLoadBearingAndTheOnlyRouteToTheViewFactory"/> pins that it is load-bearing and
    /// one edge wide, so it can neither rot into a tautology nor quietly cover a second route.</para>
    ///
    /// <para><b>ROW 10 HAS LANDED AND ITS HALF IS HERE</b>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/520). Row 9 landed the walk and the view half of the
    /// forbidden set. Row 10 added the descriptor pool, its seam and the subsystem that bundles them, and needed
    /// nothing else: the walk, the assertion and the diagnostic were already here, which was the point of landing
    /// the shape one row early.</para>
    ///
    /// <para><b>AND ROW 10 INHERITED THE ALLOWED EDGE, WHICH WAS THE ONE THING IT HAD TO WATCH.</b> Adding the
    /// pool type to the list would not have been enough on its own: a pool manager hung off
    /// <see cref="VulkanResourceOwner"/>, or off anything else behind <see cref="VulkanStagingSource"/>'s owner
    /// reference, would sit on the far side of the allowance and the walk would never see it. So the descriptor
    /// subsystem has its OWN owner record (<see cref="VulkanDescriptorOwner"/>) carrying its own seam, the
    /// device's timeline and the device's retire list, held by the device and by
    /// <see cref="VulkanResourceFactory"/> and by nothing a recorder can reach.
    /// <see cref="TheWalk_FindsTheDescriptorPoolWhereItReallyIs"/> proves the walk can find it where it really
    /// lives, which is what stops this claim from resting on a walk that reaches nothing.</para>
    ///
    /// <para><b>THE OBLIGATION ROW 10 HANDS TO ROW 11</b>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521), in the same spirit row 9 handed one to row 10. A
    /// <see cref="VulkanResourceSet"/> HOLDS the descriptor pool, because it frees itself back into one, so a
    /// bind-flush record with a FIELD of that type would put <c>vkAllocateDescriptorSets</c> into the recorder's
    /// graph and fail <see cref="TheRecordingType_ReachesNoViewFactory"/>. The set therefore exposes everything a
    /// bind needs as plain data (its <c>VkDescriptorSet</c> handle, its layout, and
    /// <c>VulkanResourceSet.DynamicUniforms</c>, which is a ring plus three integers per dynamic descriptor), and
    /// row 11 reads those into its own per-slot records rather than holding the set. A resource set is a fine
    /// method PARAMETER: this walk is over fields, because a field is what a recorder could call through at draw
    /// time.</para>
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

            // V-D2, added by row 10 (https://github.com/APKiwiOrg/KhaozEngine/issues/520). The pool is what
            // vkAllocateDescriptorSets is made through and the seam is where that call and
            // vkUpdateDescriptorSets both live, so both are named: a recorder holding the seam could write a set
            // without allocating one, which is the other half of the same prohibition.
            "VulkanDescriptorPoolManager",
            "IVulkanDescriptorApi",
            "VulkanDescriptorApi",
            // And the bundle that reaches all three, so a future field typed as the subsystem is caught by name
            // rather than by whichever of its members the walk happened to descend into first.
            "VulkanDescriptors",
        ];

        /// <summary>
        /// THE ONE EDGE THE WALK DOES NOT CROSS, and the whole of what this test allows.
        /// <see cref="VulkanStagingSource"/> holds the device's <see cref="VulkanResourceOwner"/> so it can create,
        /// bind, free and destroy the <c>VkBuffer</c> behind a staging block. See the class note for why that is a
        /// lifetime edge rather than a view-creation one, and for the obligation it puts on row 10.
        /// </summary>
        const string LifetimeEdgeType = "VulkanStagingSource";

        /// <summary>The field of <see cref="LifetimeEdgeType"/> the walk stops at.</summary>
        const string LifetimeEdgeField = "_owner";

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
        public void NothingARecorderReachesTakesAViewFactoryAsAParameter()
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

        /// <summary>
        /// AND IT FINDS THE DESCRIPTOR POOL WHERE IT REALLY IS, which is the same assertion for decision V-D2's
        /// half and matters more here, because the pool was PLACED to be findable. Hanging it off
        /// <see cref="VulkanResourceOwner"/> would have been the obvious home and would have put it behind the
        /// one edge this walk does not cross, where <see cref="TheRecordingType_ReachesNoViewFactory"/> would
        /// have kept passing while a draw could allocate a descriptor set.
        /// <para>
        /// The resource factory holds <see cref="VulkanDescriptors"/>, and the walk reaches the pool, the seam
        /// and the bundle through it. So the forbidden list is not a list of names nothing has.
        /// </para>
        /// </summary>
        [Fact]
        public void TheWalk_FindsTheDescriptorPoolWhereItReallyIs()
        {
            string[] reachable = ReachableFrom(typeof(VulkanResourceFactory)).Select(t => t.Name).ToArray();

            Assert.Contains("VulkanDescriptors", reachable);
            Assert.Contains("VulkanDescriptorPoolManager", reachable);
            Assert.Contains("IVulkanDescriptorApi", reachable);
        }

        /// <summary>
        /// AND THE POOL IS NOT BEHIND THE ALLOWED EDGE, which is the trap row 9's note warned row 10 about. Even
        /// with the one allowance CROSSED, the recorder must not reach the descriptor pool: the edge exists for a
        /// staging block's lifetime and nothing about a descriptor is on the far side of it.
        /// <para>
        /// This is deliberately stronger than <see cref="TheRecordingType_ReachesNoViewFactory"/>, which the
        /// allowance protects. If a later row moves the pool onto <see cref="VulkanResourceOwner"/>, that test
        /// keeps passing and this one fails, which is exactly the failure that would otherwise be silent.
        /// </para>
        /// </summary>
        [Fact]
        public void TheDescriptorPool_IsNotEvenBehindTheAllowedEdge()
        {
            string[] reachable = ReachableFrom(typeof(VulkanCommandList), crossLifetimeEdge: true)
                .Select(t => t.Name)
                .ToArray();

            Assert.DoesNotContain("VulkanDescriptorPoolManager", reachable);
            Assert.DoesNotContain("IVulkanDescriptorApi", reachable);
            Assert.DoesNotContain("VulkanDescriptors", reachable);
        }

        /// <summary>
        /// DECISION V-D2's OTHER HALF: the zero-count assertion against a FAKE POOL. Every shipped layout shape
        /// is built into a real resource set BEFORE recording opens, and then a whole record-and-submit cycle
        /// moves neither the allocate counter nor the update counter.
        ///
        /// <para><b>THE UNREACHABILITY WALK IS THE STRONGER GUARANTEE AND THIS IS STILL WORTH HAVING</b>, because
        /// the walk answers a question about the type graph and this answers one about the shipped shapes: every
        /// layout the renderers declare really can be turned into a set with its descriptors resolved at creation,
        /// with nothing left over for a draw to finish.</para>
        ///
        /// <para><b>WHAT ROW 11 EXTENDS.</b> A recording today is a <c>Begin</c>, a record-time uniform write and
        /// an <c>End</c>, because binds and draws are row 11's
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521). That row adds the binds to the middle of this
        /// cycle and the two counters must still read zero afterwards, which is the assertion in its shipped
        /// form. The shape below is written to take that extension without moving.</para>
        /// </summary>
        [Fact]
        public void RecordingEveryShippedSetShape_MakesNoDescriptorCallAtAll()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                foreach (GpuResourceLayoutDescription description
                    in VulkanDescriptorLimitTests.ShippedLayouts.Values)
                {
                    fixture.CreateSetFor(description, owned);
                }

                int allocatesBeforeRecording = fixture.DescriptorApi.AllocateCount;
                int updatesBeforeRecording = fixture.DescriptorApi.UpdateCount;

                Assert.Equal(VulkanDescriptorLimitTests.ShippedLayouts.Count, allocatesBeforeRecording);

                IGpuBuffer uniform = fixture.Factory.CreateBuffer(
                    VulkanResourceFixture.Buffer(256, GpuBufferUsage.UniformBuffer));
                owned.Add(uniform);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                list.UpdateBuffer<byte>(uniform, 0, new byte[] { 1, 2, 3, 4 });
                list.End();
                fixture.Submits.Submit(list, null);

                Assert.Equal(allocatesBeforeRecording, fixture.DescriptorApi.AllocateCount);
                Assert.Equal(updatesBeforeRecording, fixture.DescriptorApi.UpdateCount);
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        /// <summary>
        /// AND IT WORKS THROUGH THE TWO HOPS IT USED TO SKIP, which is why the first version of it passed while
        /// reaching almost nothing. An INTERFACE-TYPED field stands for every implementation of that interface,
        /// and a GENERIC ARGUMENT is a type as much as an array element is. Both are asserted against the
        /// recorder's own live graph rather than a contrived one: <c>_uploads</c> is an interface whose
        /// implementation carries the staging arena, and the arena's free list is a <c>List&lt;T&gt;</c> of a
        /// backend type.
        /// </summary>
        [Fact]
        public void TheWalk_DescendsIntoImplementationsAndGenericArguments()
        {
            string[] reachable = ReachableFrom(typeof(VulkanCommandList)).Select(t => t.Name).ToArray();

            Assert.Contains("VulkanListUploads", reachable);      // through the interface-typed field
            Assert.Contains("VulkanStagingArena", reachable);     // and its own field, one hop further
            Assert.Contains("VulkanStagingBlock", reachable);     // through List<VulkanStagingBlock>
        }

        /// <summary>
        /// THE ALLOWED EDGE IS LOAD-BEARING AND EXACTLY ONE EDGE WIDE, which is what stops the allowance from
        /// rotting into a lie in either direction. Cross it and the recorder reaches the view factory, so the
        /// assertion above passes BECAUSE of the allowance and not because the graph is thin. Leave it and the
        /// factory is unreachable, so the allowance is not quietly hiding a second route to one.
        /// <para>
        /// A rename on either side of the edge does not disable it silently: the match fails, the walk crosses,
        /// and <see cref="TheRecordingType_ReachesNoViewFactory"/> fails naming what it found.
        /// </para>
        /// </summary>
        [Fact]
        public void TheAllowedEdge_IsLoadBearingAndTheOnlyRouteToTheViewFactory()
        {
            FieldInfo? edge = typeof(VulkanStagingSource).GetField(
                LifetimeEdgeField, BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.True(edge is not null && edge.FieldType == typeof(VulkanResourceOwner),
                $"{LifetimeEdgeType}.{LifetimeEdgeField} is not a VulkanResourceOwner any more. The one allowance "
                + "in this test is written as a declaring type and a field name, so a move or a rename has to be "
                + "made here deliberately, with the reasoning in the class note re-read rather than assumed.");

            Assert.DoesNotContain(ReachableFrom(typeof(VulkanCommandList)),
                t => string.Equals(t.Name, "IVulkanResourceApi", StringComparison.Ordinal));

            Assert.Contains(ReachableFrom(typeof(VulkanCommandList), crossLifetimeEdge: true),
                t => string.Equals(t.Name, "IVulkanResourceApi", StringComparison.Ordinal));
        }

        static bool Forbidden(Type type)
            => Constituents(type).Any(t => ForbiddenFromRecording.Contains(t.Name, StringComparer.Ordinal));

        // Every type reachable from `root` through instance and static FIELDS, transitively, restricted to the
        // backend's own assembly. Restricted because the framework's type graph is unbounded and because a
        // violation would be one of this package's own types by definition.
        static IReadOnlyCollection<Type> ReachableFrom(Type root, bool crossLifetimeEdge = false)
        {
            Assembly backend = typeof(VulkanCommandList).Assembly;
            Type[] all = backend.GetTypes();

            var seen = new HashSet<Type>();
            var pending = new Stack<Type>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                Type type = pending.Pop();

                if (type.Assembly != backend || !seen.Add(type)) continue;

                foreach (FieldInfo field in type.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.DeclaredOnly))
                {
                    if (!crossLifetimeEdge && IsLifetimeEdge(type, field)) continue;

                    foreach (Type part in Constituents(field.FieldType)) pending.Push(part);
                }

                // An interface a reachable type implements is reachable too: a recorder holding an interface can
                // call anything on it, and the implementation is chosen at run time.
                foreach (Type contract in type.GetInterfaces())
                {
                    foreach (Type part in Constituents(contract)) pending.Push(part);
                }

                // AND SO IS EVERY IMPLEMENTATION OF AN INTERFACE THE WALK REACHES, which is the half the first
                // version of this walk missed entirely. A field typed as an interface is a field whose run-time
                // object is one of these, so stopping at the declaration stopped one hop short of everything a
                // recorder actually holds: `VulkanCommandList._uploads` is an interface, and the whole staging
                // graph behind it was invisible.
                if (!type.IsInterface) continue;

                foreach (Type candidate in all)
                {
                    if (!candidate.IsInterface && type.IsAssignableFrom(candidate)) pending.Push(candidate);
                }
            }

            return seen;
        }

        // The one edge, matched by declaring type and field name so a rename cannot silently widen the walk: it
        // stops matching, the edge is crossed, and the assertion above fails loudly.
        static bool IsLifetimeEdge(Type type, FieldInfo field)
            => string.Equals(type.Name, LifetimeEdgeType, StringComparison.Ordinal)
                && string.Equals(field.Name, LifetimeEdgeField, StringComparison.Ordinal);

        // A type plus every type hiding inside it. Arrays, by-ref parameters and pointers hide theirs one level
        // down as an ELEMENT type, and a List<T> or a Dictionary<K,V> hides them as GENERIC ARGUMENTS, which the
        // first version of this walk did not look at: a field of type List<VulkanResourceFactory> read as a bare
        // List and its contents were never reached.
        static IEnumerable<Type> Constituents(Type type)
        {
            yield return type;

            if (type.HasElementType)
            {
                foreach (Type part in Constituents(type.GetElementType()!)) yield return part;
            }

            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type part in Constituents(argument)) yield return part;
            }
        }
    }
}
