using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE STRUCTURAL ENFORCEMENT OF DECISION M-N5: no path from an entry point of
    /// <c>KhaozEngine.Gpu.Metal</c> reaches an <c>objc_msgSend</c> without passing through a method that opens an
    /// <see cref="ObjCAutoreleasePool"/>. An unpooled Objective-C call is therefore something the assembly will
    /// not express, rather than something a reviewer has to notice.
    ///
    /// <para><b>WHY A RULE RATHER THAN A HABIT.</b> Metal's factory methods return AUTORELEASED objects:
    /// <c>-commandBuffer</c>, <c>-renderCommandEncoderWithDescriptor:</c>, <c>-name</c>, every descriptor. Without
    /// a pool in scope they live until the calling thread's implicit pool drains, and under a frame loop on a
    /// thread pool thread that is never. The incumbent Veldrid Metal backend wrapped FOUR sites and did not wrap
    /// others, which is exactly the shape that accumulates and is the observation M-N5 is made against. The design
    /// asks for this to be "enforced by a device-free architecture test over the type graph rather than by
    /// review", in the shape V-D2 used for descriptor-pool unreachability.</para>
    ///
    /// <para><b>IT IS AN IL WALK RATHER THAN A FIELD WALK, AND THAT IS THE ONE PLACE IT DEPARTS FROM V-D2's
    /// SHAPE.</b> V-D2 forbids a TYPE from being REACHABLE, which a field graph can answer. M-N5 requires that a
    /// CALL happens on a path, which a field graph cannot see at all: the pool and the message send are two calls
    /// from the same method with no type relationship between them. So the walk is over call sites, and the same
    /// discipline applies to it that applies there, a positive control that proves the walk finds what it claims
    /// to look for.</para>
    ///
    /// <para><b>ENTRY POINTS ARE COMPUTED, NOT LISTED.</b> An entry point is a method the package does not call
    /// itself: the ROOTS of its own call graph, which is exactly the set a consumer, the GPU seam or a test can
    /// reach. A hand-written list would be a second thing to maintain and would silently stop covering a member
    /// added later, which is the failure mode the class doc of <c>KhaozEngineMetal</c> already records for its
    /// own ledger paragraph.</para>
    ///
    /// <para><b>THE INTEROP LAYER ITSELF IS NOT AN ENTRY POINT</b>, and that carve-out is one namespace wide.
    /// Everything under <c>KhaozEngine.Gpu.Metal.Internal.ObjC</c> IS the layer: <c>MTLDevice.Name()</c> is a
    /// message send by definition and requiring it to open its own pool would put a push and a pop around every
    /// single selector, which is both wasteful and wrong (a pool per call defeats the batching the pool exists
    /// for). What the rule governs is who may CALL the layer.</para>
    ///
    /// <para><b>THE SPIKES ARE OUT OF SCOPE BY CONSTRUCTION RATHER THAN BY EXCEPTION.</b>
    /// <c>MetalInteropSpike</c> keeps its own <c>objc_msgSend</c> declarations, deliberately: it is a MEASUREMENT
    /// whose value is being self-contained, so it can be re-run to answer exactly what row 1 asked rather than
    /// whatever the backend has since grown. Its calls therefore do not go through <see cref="ObjCMsgSend"/> and
    /// this walk never sees them, with no exclusion list needed. Both it and
    /// <c>MetalCompileOptionsProbe</c> open pools of their own anyway, and neither is on any consumer path. The
    /// probe's duplicate declaration set was a different thing and row 4 deleted it, which is the other half of
    /// the handoff on https://github.com/APKiwiOrg/KhaozEngine/issues/570.</para>
    ///
    /// <para><b>AND ONE TYPE IN THE OTHER ASSEMBLY GETS ITS OWN NARROW ROOT SET.</b>
    /// <see cref="MetalFrameCapture"/> lives in <c>KhaozEngine.Gpu</c> and keeps its own <c>objc_msgSend</c>
    /// declarations, exactly like the spikes, so the walk above cannot see it for two independent reasons. Unlike
    /// the spikes it IS on a consumer path: a present boundary calls it, on a frame loop, which is precisely the
    /// case M-N5 exists for. Its pool discipline was correct and unguarded, so a second row below reads the same
    /// IL for that type alone, with the roots being its internal members and one documented exception.</para>
    /// </summary>
    public sealed class MetalAutoreleaseArchitectureTests
    {
        readonly ITestOutputHelper _output;

        public MetalAutoreleaseArchitectureTests(ITestOutputHelper output) => _output = output;

        /// <summary>The namespace that IS the interop layer. Members declared here are never entry points.</summary>
        const string InteropNamespace = "KhaozEngine.Gpu.Metal.Internal.ObjC";

        /// <summary>
        /// THE RULE. For every entry point, every path to an <c>objc_msgSend</c> passes through a method that
        /// opens a pool. A method that opens one covers everything below it, which is why an entry point may
        /// delegate: <c>MetalSupportProbe.MissingRequirement</c> opens nothing and is covered because
        /// <c>ReadFacts</c>, which it calls, does.
        /// </summary>
        [Fact]
        public void NoEntryPointReachesAMessageSendWithoutAPool()
        {
            var violations = new List<string>();
            foreach (MethodBase entry in EntryPoints())
            {
                var path = new List<MethodBase>();
                if (ReachesInteropUnpooled(entry, new HashSet<MethodBase>(), path))
                    violations.Add(Describe(entry) + " -> " + string.Join(" -> ", path.Select(Describe)));
            }

            Assert.True(violations.Count == 0,
                "These entry points of KhaozEngine.Gpu.Metal can reach objc_msgSend without an autorelease pool "
                + "on the path, which is decision M-N5's failure: every Objective-C factory method returns an "
                + "autoreleased object, and one that nothing pools lives until the calling thread's implicit pool "
                + "drains, which under a frame loop is never. Open one with "
                + "'using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();' at the top of the body.\n"
                + string.Join("\n", violations));
        }

        /// <summary>
        /// THE POSITIVE CONTROL, and without it the row above could pass because the walk finds nothing at all. It
        /// asserts that the walk really does reach the interop layer from a real entry point when the pool is not
        /// counted, so "no violations" means the rule held rather than that the reflection quietly returned an
        /// empty set.
        /// </summary>
        [Fact]
        public void TheWalk_ReachesTheInteropLayerFromARealEntryPoint()
        {
            MethodBase probe = typeof(MetalSupportProbe)
                .GetMethod(nameof(MetalSupportProbe.MissingRequirement),
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("MetalSupportProbe.MissingRequirement is gone.");

            var path = new List<MethodBase>();
            bool reaches = Reaches(probe, new HashSet<MethodBase>(), path, stopAtPools: false);

            _output.WriteLine(string.Join(" -> ", path.Select(Describe)));
            Assert.True(reaches,
                "The IL walk found no route from MetalSupportProbe.MissingRequirement to ObjCMsgSend, which means "
                + "the walk is broken rather than that the package is clean: that method reads a device name, "
                + "which is a message send. Every other row in this class is vacuous until this one passes.");
        }

        /// <summary>
        /// And the walk recognises a pool OPENER, which is the other half of the control: a rule that never
        /// classified anything as covered would report every entry point as a violation, and a rule that
        /// classified everything as covered would report none. This pins the specific method the probe relies on.
        /// </summary>
        [Fact]
        public void TheWalk_RecognisesTheProbesOwnPool()
        {
            MethodBase readFacts = typeof(MetalSupportProbe)
                .GetMethod(nameof(MetalSupportProbe.ReadFacts),
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("MetalSupportProbe.ReadFacts is gone.");

            Assert.True(OpensAPool(readFacts),
                "MetalSupportProbe.ReadFacts no longer calls ObjCAutoreleasePool.Enter, so nothing on the probe's "
                + "path opens a pool and every device name it reads leaks.");
        }

        /// <summary>
        /// The entry-point computation is not vacuous either: the package's own seam implementation must be in
        /// it. If <c>MetalBackendProvider.CreateHeadless</c> stopped being a root, the roots rule would have
        /// started counting a consumer-visible member as internal plumbing and the whole class would be checking
        /// a smaller surface than it claims.
        /// </summary>
        [Fact]
        public void TheEntryPoints_IncludeTheProvidersSeamMembers()
        {
            string[] roots = EntryPoints().Select(Describe).ToArray();
            _output.WriteLine(string.Join("\n", roots.OrderBy(r => r, StringComparer.Ordinal)));

            Assert.Contains("MetalBackendProvider.CreateHeadless", roots, StringComparer.Ordinal);
            Assert.Contains("MetalBackendProvider.IsSupported", roots, StringComparer.Ordinal);
        }

        // ---- The frame capture, in the other assembly ----------------------------------------------------------

        /// <summary>The one member of <see cref="MetalFrameCapture"/> that may reach libobjc without pushing a
        /// pool. Its own remarks say why, and the row below quotes the reason rather than restating it: it creates
        /// no Objective-C object at all, because <c>objc_getClass</c> and <c>sel_registerName</c> allocate nothing
        /// and <c>+sharedCaptureManager</c> is a retained singleton rather than an autoreleased instance.</summary>
        const string CaptureMemberWithNoPool = nameof(MetalFrameCapture.CaptureIsEnabledForThisProcess);

        /// <summary>
        /// M-N5 ON <see cref="MetalFrameCapture"/>, which the walk above cannot reach. Every internal member that
        /// reaches one of that type's own libobjc imports pushes a pool of its own, with exactly one allowlisted
        /// exception. Before this row the discipline was written once, correctly, and nothing would have noticed a
        /// member added later that sent a message with no pool under it: the type is called at a present boundary,
        /// so what leaks there leaks once per frame for the life of the process.
        /// </summary>
        [Fact]
        public void TheFrameCapturesOwnMembersPushTheirOwnPool()
        {
            var violations = new List<string>();
            foreach (MethodBase entry in CaptureRoots())
            {
                if (entry.Name == CaptureMemberWithNoPool) continue;
                if (!ReachesAnObjCImport(entry, new HashSet<MethodBase>())) continue;
                if (!PushesAPool(entry)) violations.Add(Describe(entry));
            }

            Assert.True(violations.Count == 0,
                "These members of MetalFrameCapture reach one of its libobjc imports with no autorelease pool "
                + "pushed on the way, which is decision M-N5's failure in the one type the architecture walk over "
                + "KhaozEngine.Gpu.Metal cannot see. The NSString, the NSURL and the shared capture manager are "
                + "autoreleased, and a present boundary on a thread with no pool holds them for the life of the "
                + "process. Push one with 'IntPtr pool = PoolPush();' inside the try, and pop it in the finally.\n"
                + string.Join("\n", violations));
        }

        /// <summary>
        /// THE POSITIVE CONTROL FOR THAT ROW, and without it the one above would pass on a walk that found
        /// nothing: both members really do reach a message send, and both really are recognised as pushing a pool,
        /// so a green run means the rule held rather than that the IL reader came back empty.
        /// </summary>
        [Fact]
        public void TheWalk_SeesBothHalvesOfTheFrameCapturesPoolDiscipline()
        {
            foreach (string name in new[] { nameof(MetalFrameCapture.Start), nameof(MetalFrameCapture.Stop) })
            {
                MethodBase member = CaptureMember(name);
                Assert.True(ReachesAnObjCImport(member, new HashSet<MethodBase>()),
                    $"the IL walk found no libobjc import under MetalFrameCapture.{name}, which means the walk is "
                    + "broken rather than that the member is clean: it drives MTLCaptureManager, and every step of "
                    + "that is a message send.");
                Assert.True(PushesAPool(member),
                    $"MetalFrameCapture.{name} no longer pushes an autorelease pool.");
            }
        }

        /// <summary>
        /// AND THE ONE EXCEPTION IS STILL EARNED. The allowlist entry is only sound while that member creates no
        /// Objective-C object, so this row asserts the two halves of its documented rationale: it does reach
        /// libobjc, and it pushes no pool. A member that started pushing one would make the exemption stale, and a
        /// member that stopped touching libobjc would make it pointless.
        /// </summary>
        [Fact]
        public void TheFrameCapturesOnePoolFreeMemberIsTheDocumentedOne()
        {
            MethodBase member = CaptureMember(CaptureMemberWithNoPool);

            Assert.True(ReachesAnObjCImport(member, new HashSet<MethodBase>()));
            Assert.False(PushesAPool(member),
                "MetalFrameCapture." + CaptureMemberWithNoPool + " pushes an autorelease pool now, so the reason "
                + "it is allowlisted here (\"IT PUSHES NO POOL OF ITS OWN ... it creates no Objective-C object at "
                + "all\") no longer describes it. Either the member gained an autoreleased object, in which case "
                + "the pool is right and this allowlist entry must go, or the push is unnecessary.");
        }

        // The roots for that type: its internal members, which are the only ones anything outside it can call. A
        // private helper is covered through its caller, exactly as the walk above covers a pooled delegation.
        static IReadOnlyList<MethodBase> CaptureRoots()
            => typeof(MetalFrameCapture)
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                    | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.IsAssembly)
                .ToArray();

        static MethodBase CaptureMember(string name)
            => typeof(MetalFrameCapture).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public
                | BindingFlags.Static)
                ?? throw new InvalidOperationException($"MetalFrameCapture.{name} is gone.");

        // Whether anything under this member calls one of the type's own libobjc imports, the pool pair excepted.
        // Every import there is either a message send or a runtime lookup that stands next to one, so the rule
        // does not have to tell them apart: what it needs to know is whether the member goes near libobjc at all.
        static bool ReachesAnObjCImport(MethodBase method, HashSet<MethodBase> seen)
        {
            if (!seen.Add(method)) return false;

            foreach (MethodBase callee in Callees(method))
            {
                if (callee.DeclaringType != typeof(MetalFrameCapture)) continue;
                if (IsPoolCall(callee)) continue;
                if (IsNativeImport(callee)) return true;
                if (ReachesAnObjCImport(callee, seen)) return true;
            }

            return false;
        }

        // POSITION-BLIND for the reason OpensAPool is, and the same comment applies: every push here is the first
        // statement of its try, and a positional check would have to model control flow for a case nobody writes.
        static bool PushesAPool(MethodBase method)
            => Callees(method).Any(c => c.DeclaringType == typeof(MetalFrameCapture) && c.Name == "PoolPush");

        static bool IsPoolCall(MethodBase method) => method.Name is "PoolPush" or "PoolPop";

        static bool IsNativeImport(MethodBase method)
            => (method.Attributes & MethodAttributes.PinvokeImpl) != 0;

        // ---- The entry-point set ------------------------------------------------------------------------------

        // Every method the package declares outside the interop namespace that no other package method calls.
        static IReadOnlyList<MethodBase> EntryPoints()
        {
            MethodBase[] all = PackageMethods();

            var called = new HashSet<MethodBase>();
            foreach (MethodBase method in all)
            {
                foreach (MethodBase callee in Callees(method)) called.Add(callee);
            }

            return all
                .Where(m => m.DeclaringType?.Namespace != InteropNamespace)
                .Where(m => !called.Contains(m))
                .ToArray();
        }

        static MethodBase[] PackageMethods()
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly;

            var methods = new List<MethodBase>();
            foreach (Type type in typeof(MetalSupportProbe).Assembly.GetTypes())
            {
                // Compiler-generated closures and iterator state machines are not anybody's entry point, and
                // their bodies are already covered through the method they were generated from.
                if (type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
                    continue;

                methods.AddRange(type.GetMethods(flags));
                methods.AddRange(type.GetConstructors(flags));
            }
            return methods.ToArray();
        }

        // ---- The rule -----------------------------------------------------------------------------------------

        static bool ReachesInteropUnpooled(MethodBase method, HashSet<MethodBase> seen, List<MethodBase> path)
            => !OpensAPool(method) && Reaches(method, seen, path, stopAtPools: true);

        // Depth-first over call sites. A method that opens a pool COVERS everything below it, so the walk stops
        // there rather than descending, which is the whole difference between "the entry point wraps its body"
        // and "something on the path does". The design's wording is the first and its intent is the second: a
        // helper that opens a pool is exactly as safe, and forbidding delegation would push every entry point to
        // inline its own body.
        static bool Reaches(MethodBase method, HashSet<MethodBase> seen, List<MethodBase> path, bool stopAtPools)
        {
            if (!seen.Add(method)) return false;

            foreach (MethodBase callee in Callees(method))
            {
                if (IsMessageSend(callee))
                {
                    path.Add(callee);
                    return true;
                }

                if (callee.DeclaringType?.Assembly != typeof(MetalSupportProbe).Assembly) continue;
                if (stopAtPools && OpensAPool(callee)) continue;

                path.Add(callee);
                if (Reaches(callee, seen, path, stopAtPools)) return true;
                path.RemoveAt(path.Count - 1);
            }

            return false;
        }

        // The forbidden call: anything on the layer's one dispatch function. objc_msgSend is the ONLY runtime
        // entry that can return an autoreleased object, which is why the set is this narrow and not "every import
        // in the layer". A create/copy/new selector returns +1 and is released by hand, sel_registerName and
        // objc_getClass return runtime singletons nobody owns, and the pool calls are the pool.
        static bool IsMessageSend(MethodBase method) => method.DeclaringType == typeof(ObjCMsgSend);

        // POSITION-BLIND, knowingly: a method counts as covered if its IL calls Enter anywhere, so an early
        // return that sends a message before entering the pool would read as clean. Every current opener enters
        // as its first statement, and a positional IL check would have to model control flow for a case nobody
        // writes. If an opener ever grows an early message-send path, this is the comment that says the walk
        // did not lie, it just cannot see ordering.
        static bool OpensAPool(MethodBase method)
            => Callees(method).Any(c => c.DeclaringType == typeof(ObjCAutoreleasePool)
                && c.Name == nameof(ObjCAutoreleasePool.Enter));

        // ---- The IL walk --------------------------------------------------------------------------------------

        // Read by IlCallGraph, which is the same reader row 10's name-blindness rule walks
        // (https://github.com/APKiwiOrg/KhaozEngine/issues/594). It was extracted from here when the second rule
        // needed it: the reader is generic, and what stays in this file is what M-N5 MEANS.
        static MethodBase[] Callees(MethodBase method) => IlCallGraph.Callees(method);

        static string Describe(MethodBase method) => IlCallGraph.Describe(method);
    }
}
