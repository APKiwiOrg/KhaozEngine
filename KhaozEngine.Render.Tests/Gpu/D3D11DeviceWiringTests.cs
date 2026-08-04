using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DEVICE ROW'S WIRING, AS FAR AS IT IS CHECKABLE WITHOUT A DIRECT3D DEVICE
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/497). Construction itself is Windows-only end to end, so
    /// what runs here is the two things that are not: the provider's refusal off Windows, and the SHAPE of the
    /// construction and teardown that a real device performs.
    ///
    /// <para><b>THE SHAPE IS READ OUT OF THE ASSEMBLY FILE, not off loaded types, and that is deliberate rather
    /// than exotic.</b> <c>D3D11GpuDevice</c> is Windows-only and holds Direct3D references, so a reflection walk
    /// that touched its field types or resolved its call targets would load the Vortice interop on macOS, and the
    /// suite asserts process-wide that nothing does (<c>D3D11InteropLoad</c>). A
    /// <see cref="MetadataReader"/> over the compiled file answers the same questions and loads nothing at all:
    /// which types the constructor instantiates and in what order, which members the teardown calls and in what
    /// order, and which fields the type declares.</para>
    ///
    /// <para><b>WHAT THE IL SCAN CAN AND CANNOT GET WRONG.</b> It walks the method body for the
    /// <c>newobj</c>, <c>call</c> and <c>callvirt</c> opcodes without decoding the instructions in between, so in
    /// principle an operand byte could be mistaken for an opcode. Every hit is then RESOLVED through the metadata
    /// and kept only when it names one of a handful of specific members, which is why that cannot produce a false
    /// pass: a spurious hit would have to be a valid token for exactly one of the named members, and a real call
    /// can never be missed, since a real call always begins with its opcode byte. The failure direction is a
    /// false alarm, which is loud, and the assertions below are about ORDER, which a genuine call sequence
    /// satisfies.</para>
    /// </summary>
    public sealed class D3D11DeviceWiringTests
    {
        const string DeviceType = "D3D11GpuDevice";

        // ---------------------------------------------------------------------------------------------------
        // The provider's throw-to-real transition.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// THE OLD "NOT BUILT YET" ANSWER IS GONE, and off Windows what replaces it is a PLATFORM answer. This is
        /// the exception a caller who named the native backend on macOS or Linux actually reads, and it must not
        /// say the backend is unfinished (it is not) or that their machine is at fault in some unnamed way.
        /// <para>
        /// It must also reach that answer WITHOUT loading the Direct3D interop, which is the whole reason the
        /// platform guard is the first statement of both entry points rather than a check inside the creation
        /// body.
        /// </para>
        /// </summary>
        [Fact]
        public void OffWindows_BothCreationEntryPoints_RefuseWithAPlatformAnswerAndLoadNoInterop()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;   // on Windows they create a real device

            var provider = new D3D11BackendProvider();

            var windowed = Assert.Throws<PlatformNotSupportedException>(
                () => provider.CreateForWindow(new GpuWindowedDeviceRequest(
                    new GpuWindowHandle(GpuWindowKind.Win32, new IntPtr(1)), 640, 480, true)));
            var headless = Assert.Throws<PlatformNotSupportedException>(() => provider.CreateHeadless());

            foreach (Exception ex in new Exception[] { windowed, headless })
            {
                Assert.Contains("Direct3D 11", ex.Message, StringComparison.Ordinal);
                Assert.Contains("operating system", ex.Message, StringComparison.Ordinal);
                // The retired message. A build that still said this would be telling a tester the row never
                // landed, which is the one thing this exception must no longer claim.
                Assert.DoesNotContain("still being built", ex.Message, StringComparison.Ordinal);
            }

            D3D11InteropLoad.AssertNotLoaded();
        }

        // ---------------------------------------------------------------------------------------------------
        // Issue #476: the device constructs exactly ONE state object and ONE emitter context.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// THE REMAINING HALF OF https://github.com/APKiwiOrg/KhaozEngine/issues/476, and the reason that issue
        /// stayed open after the replay row: the mechanical check there proves every emitter implementation
        /// RECEIVES a <c>D3D11DeviceState</c>, and this proves the DEVICE is the only thing that makes one.
        /// <para>
        /// Both halves are needed and neither implies the other. A readonly struct that allocated its own state
        /// in its constructor would satisfy the first and reintroduce exactly the defect: list B binds pipeline
        /// P, list A's copy still believes A's pipeline is current, A skips the rebind and draws with B's state,
        /// with nothing thrown and nothing logged. What makes that impossible is that there is ONE state in the
        /// process per device, and one construction site is how that is enforced.
        /// </para>
        /// </summary>
        [Fact]
        public void TheDeviceIsTheOnlyThingInTheBackendThatConstructsAStateOrAnEmitterContext()
        {
            using var backend = BackendMetadata.Open();

            AssertSoleConstructionSite(backend, "D3D11DeviceState");
            AssertSoleConstructionSite(backend, "D3D11EmitterContext");
            AssertSoleConstructionSite(backend, "D3D11NativeEmitter");
        }

        /// <summary>
        /// The other half of "one per device": the device holds exactly one FIELD of each, so there is no second
        /// slot a later edit could park a per-list copy in. Read off the field signatures rather than off loaded
        /// <c>FieldInfo.FieldType</c>s, which would resolve the Direct3D fields beside them.
        /// </summary>
        [Fact]
        public void TheDeviceDeclaresExactlyOneStateOneEmitterContextAndOneEmitter()
        {
            using var backend = BackendMetadata.Open();
            IReadOnlyList<string> fields = backend.FieldTypeNames(DeviceType);

            Assert.Equal(1, fields.Count(f => f == "D3D11DeviceState"));
            Assert.Equal(1, fields.Count(f => f == "D3D11EmitterContext"));
            Assert.Equal(1, fields.Count(f => f == "D3D11NativeEmitter"));
            // One ring, one fence subsystem and one loss latch, for the same reason: every one of them carries
            // state the whole device has to agree about (a segment's owner, the timeline's value, whether the
            // device is lost), and a second instance would be a second answer.
            Assert.Equal(1, fields.Count(f => f == "D3D11RingAllocator"));
            Assert.Equal(1, fields.Count(f => f == "D3D11FenceSubsystem"));
            Assert.Equal(1, fields.Count(f => f == "D3D11DeviceLossLatch"));
        }

        // ---------------------------------------------------------------------------------------------------
        // The construction order, and the teardown order.
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// THE CONSTRUCTION ORDER OF ISSUE #497, as the constructor actually performs it. Every step here is a
        /// dependency of the one after it, which is why the order is worth pinning at all rather than merely
        /// writing down: the ring reads the fence subsystem's completion value, the bind flush the state composes
        /// takes the ring, the factory validates against the capabilities, and the swapchain and the staging path
        /// both take the liveness token and the latch that were built first.
        /// <para>
        /// TWO STEPS RUN EARLIER THAN THE ISSUE LISTS THEM, and both are forced: the capability read is before
        /// the factory because the factory takes the capabilities, and the one device state is after the ring
        /// because the state composes the bind flush and the bind flush takes the ring. Neither changes what is
        /// built.
        /// </para>
        /// </summary>
        [Fact]
        public void TheConstructorBuildsEverySubsystemInDependencyOrder()
        {
            using var backend = BackendMetadata.Open();

            IReadOnlyList<string> built = backend.ConstructedTypesIn(DeviceType, ".ctor", new[]
            {
                "D3D11DeviceLiveness", "D3D11DeviceLossLatch", "D3D11FenceSubsystem", "D3D11RingAllocator",
                "D3D11DeviceState", "D3D11EmitterContext", "D3D11NativeEmitter", "D3D11ResourceFactory",
                "D3D11StagingAccess", "D3D11Swapchain",
            });

            Assert.Equal(new[]
            {
                "D3D11DeviceLiveness", "D3D11DeviceLossLatch", "D3D11FenceSubsystem", "D3D11RingAllocator",
                "D3D11DeviceState", "D3D11EmitterContext", "D3D11NativeEmitter", "D3D11ResourceFactory",
                "D3D11StagingAccess", "D3D11Swapchain",
            }, built);
        }

        /// <summary>
        /// THE TEARDOWN ORDER, which is the half of the wiring that only fails at shutdown and therefore the half
        /// nobody sees fail. Three clauses, and each one is a real hazard rather than tidiness:
        /// <list type="number">
        ///   <item><description>The DRAIN comes first, while the device is still live and while nothing holds the
        ///   submit lock. It refuses a caller holding that lock by name, and it is the one member here that can
        ///   block.</description></item>
        ///   <item><description>The releases come next, in the order that leaves nothing referenced: the debug
        ///   pump, the swapchain and its views, then the fence subsystem, which takes the timeline's fence and
        ///   event objects with it.</description></item>
        ///   <item><description>The liveness token is flipped LAST. Every release above reads it and no-ops when
        ///   it says dead, so flipping it first (which is what the Veldrid wrapper does, correctly, because
        ///   destroying a Veldrid device frees its children) would silently skip all of them and leave the
        ///   ID3D11Device alive holding a swapchain nobody can reach.</description></item>
        /// </list>
        /// </summary>
        [Fact]
        public void TeardownDrainsFirstReleasesNextAndFlipsLivenessLast()
        {
            using var backend = BackendMetadata.Open();

            IReadOnlyList<string> calls = backend.CalledMembersIn(DeviceType, "MarkDeviceDisposed", new[]
            {
                "D3D11FenceSubsystem.WaitForIdle", "D3D11InfoQueuePump.Dispose", "D3D11Swapchain.Dispose",
                "D3D11FenceSubsystem.Dispose", "D3D11DeviceLiveness.MarkDead",
            });

            Assert.Equal(new[]
            {
                "D3D11FenceSubsystem.WaitForIdle", "D3D11InfoQueuePump.Dispose", "D3D11Swapchain.Dispose",
                "D3D11FenceSubsystem.Dispose", "D3D11DeviceLiveness.MarkDead",
            }, calls);
        }

        /// <summary>
        /// The present boundary calls the two frame-boundary members that REFUSE a caller holding the submit
        /// lock, so it must call them after the swapchain's present has released it. Pinned by absence: the
        /// present body takes no lock of its own at all, which is what makes the two calls provably outside it.
        /// <para>
        /// The ring's <c>BeginFrame</c> is the one that matters, because it waits for the GPU to finish with the
        /// segment it opens, which is up to a frame. Inside the submit lock that is a frame-long hold of the lock
        /// decision W4 caps at microseconds, and on the event-query fence mechanism it also shuts out the
        /// submission that would end the wait.
        /// </para>
        /// </summary>
        [Fact]
        public void ThePresentBoundaryRollsTheFrameCountersOutsideTheSubmitLock()
        {
            using var backend = BackendMetadata.Open();

            IReadOnlyList<string> calls = backend.CalledMembersIn(DeviceType, "Present", new[]
            {
                "D3D11Swapchain.Present", "D3D11DeviceLossLatch.Check", "D3D11FenceSubsystem.BeginFrame",
                "D3D11RingAllocator.BeginFrame", "Monitor.Enter",
            });

            Assert.Equal(new[]
            {
                "D3D11Swapchain.Present", "D3D11DeviceLossLatch.Check", "D3D11FenceSubsystem.BeginFrame",
                "D3D11RingAllocator.BeginFrame",
            }, calls);
        }

        // ---------------------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------------------

        static void AssertSoleConstructionSite(BackendMetadata backend, string typeName)
        {
            IReadOnlyList<string> sites = backend.ConstructionSitesOf(typeName);

            Assert.Equal(new[] { $"{DeviceType}..ctor" }, sites);
        }

        /// <summary>
        /// The compiled backend assembly, read as metadata. Nothing here loads a type, resolves a Vortice
        /// reference or JITs a body, which is what makes every assertion above safe to run on macOS beside the
        /// suite's process-wide "the interop is not loaded" checks.
        /// </summary>
        sealed class BackendMetadata : IDisposable
        {
            readonly PEReader _pe;
            readonly MetadataReader _md;

            BackendMetadata(PEReader pe, MetadataReader md)
            {
                _pe = pe;
                _md = md;
            }

            internal static BackendMetadata Open()
            {
                // Taken off a type with no Direct3D in it, so asking where the assembly lives does not load the
                // one thing these tests exist to keep unloaded.
                string path = typeof(D3D11DeviceState).Assembly.Location;
                Assert.True(File.Exists(path), $"the backend assembly was not on disk at '{path}'");

                var pe = new PEReader(File.OpenRead(path));
                return new BackendMetadata(pe, pe.GetMetadataReader());
            }

            /// <summary>Every method in the assembly that constructs <paramref name="typeName"/>, as
            /// <c>Type..ctor</c> names, in metadata order.</summary>
            internal IReadOnlyList<string> ConstructionSitesOf(string typeName)
            {
                var sites = new List<string>();
                foreach (TypeDefinitionHandle typeHandle in _md.TypeDefinitions)
                {
                    TypeDefinition type = _md.GetTypeDefinition(typeHandle);
                    string owner = _md.GetString(type.Name);
                    foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                    {
                        MethodDefinition method = _md.GetMethodDefinition(methodHandle);
                        string name = _md.GetString(method.Name);
                        if (Instructions(method).Any(i => i.IsNewObject && i.DeclaringType == typeName))
                            sites.Add($"{owner}.{name}");
                    }
                }
                return sites;
            }

            /// <summary>The types <paramref name="method"/> constructs, filtered to
            /// <paramref name="ofInterest"/>, first occurrence only, in IL order.</summary>
            internal IReadOnlyList<string> ConstructedTypesIn(string typeName, string method,
                IEnumerable<string> ofInterest)
            {
                var wanted = new HashSet<string>(ofInterest, StringComparer.Ordinal);
                var seen = new List<string>();
                foreach (Instruction instruction in Instructions(Method(typeName, method)))
                {
                    if (!instruction.IsNewObject) continue;
                    if (!wanted.Contains(instruction.DeclaringType)) continue;
                    if (!seen.Contains(instruction.DeclaringType)) seen.Add(instruction.DeclaringType);
                }
                return seen;
            }

            /// <summary>The members <paramref name="method"/> calls, filtered to <paramref name="ofInterest"/> as
            /// <c>Type.Member</c> names, first occurrence only, in IL order.</summary>
            internal IReadOnlyList<string> CalledMembersIn(string typeName, string method,
                IEnumerable<string> ofInterest)
            {
                var wanted = new HashSet<string>(ofInterest, StringComparer.Ordinal);
                var seen = new List<string>();
                foreach (Instruction instruction in Instructions(Method(typeName, method)))
                {
                    if (instruction.IsNewObject) continue;
                    string name = $"{instruction.DeclaringType}.{instruction.Member}";
                    if (wanted.Contains(name) && !seen.Contains(name)) seen.Add(name);
                }
                return seen;
            }

            /// <summary>The declared type names of every instance field of <paramref name="typeName"/>, decoded
            /// from the field signatures.</summary>
            internal IReadOnlyList<string> FieldTypeNames(string typeName)
            {
                var names = new List<string>();
                foreach (FieldDefinitionHandle handle in Type(typeName).GetFields())
                {
                    FieldDefinition field = _md.GetFieldDefinition(handle);
                    names.Add(field.DecodeSignature(new NameOnlySignatures(), genericContext: null));
                }
                return names;
            }

            TypeDefinition Type(string typeName)
            {
                foreach (TypeDefinitionHandle handle in _md.TypeDefinitions)
                {
                    TypeDefinition type = _md.GetTypeDefinition(handle);
                    if (_md.GetString(type.Name) == typeName) return type;
                }
                throw new InvalidOperationException($"'{typeName}' is not a type in the native Direct3D 11 backend.");
            }

            MethodDefinition Method(string typeName, string methodName)
            {
                foreach (MethodDefinitionHandle handle in Type(typeName).GetMethods())
                {
                    MethodDefinition method = _md.GetMethodDefinition(handle);
                    if (_md.GetString(method.Name) == methodName) return method;
                }
                throw new InvalidOperationException($"'{typeName}' declares no method '{methodName}'.");
            }

            // The scan itself: newobj (0x73), call (0x28) and callvirt (0x6F) are all one byte plus a four-byte
            // metadata token, so a hit is read and resolved without decoding anything in between. See the class
            // note for why over-reporting is the only failure this shape can produce.
            IEnumerable<Instruction> Instructions(MethodDefinition method)
            {
                if (method.RelativeVirtualAddress == 0) yield break;

                ImmutableArray<byte> il = _pe.GetMethodBody(method.RelativeVirtualAddress).GetILContent();
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    byte opcode = il[i];
                    if (opcode is not (0x73 or 0x28 or 0x6F)) continue;

                    int token = il[i + 1] | (il[i + 2] << 8) | (il[i + 3] << 16) | (il[i + 4] << 24);
                    if (!TryResolve(token, out string declaringType, out string member)) continue;

                    yield return new Instruction(opcode == 0x73, declaringType, member);
                }
            }

            bool TryResolve(int token, out string declaringType, out string member)
            {
                declaringType = string.Empty;
                member = string.Empty;
                try
                {
                    EntityHandle handle = MetadataTokens.EntityHandle(token);
                    switch (handle.Kind)
                    {
                        case HandleKind.MethodDefinition:
                            MethodDefinition definition = _md.GetMethodDefinition((MethodDefinitionHandle)handle);
                            declaringType = _md.GetString(_md.GetTypeDefinition(definition.GetDeclaringType()).Name);
                            member = _md.GetString(definition.Name);
                            return true;

                        case HandleKind.MemberReference:
                            MemberReference reference = _md.GetMemberReference((MemberReferenceHandle)handle);
                            declaringType = NameOf(reference.Parent);
                            member = _md.GetString(reference.Name);
                            return declaringType.Length > 0;

                        default:
                            return false;
                    }
                }
                catch (Exception)
                {
                    // A byte that looked like an opcode and was not: the token is not a handle this metadata
                    // knows. Skipped rather than reported, which is the whole reason the scan is allowed to be
                    // this simple.
                    return false;
                }
            }

            string NameOf(EntityHandle parent) => parent.Kind switch
            {
                HandleKind.TypeReference => _md.GetString(_md.GetTypeReference((TypeReferenceHandle)parent).Name),
                HandleKind.TypeDefinition => _md.GetString(_md.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
                _ => string.Empty,
            };

            public void Dispose() => _pe.Dispose();

            readonly struct Instruction
            {
                internal Instruction(bool isNewObject, string declaringType, string member)
                {
                    IsNewObject = isNewObject;
                    DeclaringType = declaringType;
                    Member = member;
                }

                internal bool IsNewObject { get; }
                internal string DeclaringType { get; }
                internal string Member { get; }
            }
        }

        /// <summary>
        /// A signature decoder that answers the bare TYPE NAME and nothing else. The full provider would build
        /// System.Type-shaped names and would need the referenced assemblies resolved, which is exactly what must
        /// not happen here: the device's Direct3D fields sit beside the ones being counted.
        /// </summary>
        sealed class NameOnlySignatures : ISignatureTypeProvider<string, object?>
        {
            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
                => reader.GetString(reader.GetTypeDefinition(handle).Name);
            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
                => reader.GetString(reader.GetTypeReference(handle).Name);
            public string GetTypeFromSpecification(MetadataReader reader, object? genericContext,
                TypeSpecificationHandle handle, byte rawTypeKind) => "specification";
            public string GetSZArrayType(string elementType) => elementType + "[]";
            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
            public string GetByReferenceType(string elementType) => elementType + "&";
            public string GetPointerType(string elementType) => elementType + "*";
            public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
                => genericType;
            public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
            public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
            public string GetPinnedType(string elementType) => elementType;
            public string GetFunctionPointerType(MethodSignature<string> signature) => "function pointer";
        }
    }
}
