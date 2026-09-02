using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE COMPILED DIRECT3D 11 BACKEND ASSEMBLY, READ AS METADATA: which types a method constructs and in what
    /// order, which members it calls and in what order, and which fields a type declares. Nothing here loads a
    /// type, resolves a Vortice reference or JITs a body, which is what makes every assertion built on it safe
    /// to run on macOS beside the suite's process-wide "the interop is not loaded" checks.
    ///
    /// <para><b>WHY A SCAN AT ALL.</b> Most of this backend's rules are decided in types with no device in them
    /// and are ordinary <c>[Fact]</c>s. A handful are not: the device's construction and teardown ORDER, and the
    /// call a Windows-only emitter body makes, live in bodies no machine without Direct3D can execute. The
    /// metadata answers those without a device.</para>
    ///
    /// <para><b>WHAT THE SCAN CAN AND CANNOT GET WRONG.</b> It walks a method body for the <c>newobj</c>,
    /// <c>call</c> and <c>callvirt</c> opcodes without decoding the instructions in between, so in principle an
    /// operand byte could be mistaken for an opcode. Every hit is then RESOLVED through the metadata and kept
    /// only when it names one of the members the caller asked about, which is why that cannot produce a false
    /// pass: a spurious hit would have to be a valid token for exactly one of the named members, and a real call
    /// can never be missed, since a real call always begins with its opcode byte. The failure direction is a
    /// false alarm, which is loud.</para>
    ///
    /// <para><b>EXTRACTED RATHER THAN DUPLICATED</b>, the same call
    /// <c>IlCallGraph</c> answered for the Metal rows: it started as a nested helper of
    /// <see cref="D3D11DeviceWiringTests"/> and moved out here when the shared-sampler ownership row
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/506) and the ring allocator's spin
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/493) needed the same reader. It reads metadata and
    /// decides nothing: every caller keeps its own notion of what a violation is.</para>
    /// </summary>
    internal sealed class D3D11BackendMetadata : IDisposable
    {
        readonly PEReader _pe;
        readonly MetadataReader _md;

        D3D11BackendMetadata(PEReader pe, MetadataReader md)
        {
            _pe = pe;
            _md = md;
        }

        internal static D3D11BackendMetadata Open()
        {
            // Taken off a type with no Direct3D in it, so asking where the assembly lives does not load the
            // one thing these tests exist to keep unloaded.
            string path = typeof(D3D11DeviceState).Assembly.Location;
            Assert.True(File.Exists(path), $"the backend assembly was not on disk at '{path}'");

            var pe = new PEReader(File.OpenRead(path));
            return new D3D11BackendMetadata(pe, pe.GetMetadataReader());
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

        /// <summary>How many times <paramref name="method"/> calls <paramref name="member"/>, named
        /// <c>Type.Member</c>. The counted form of <see cref="CalledMembersIn"/>, for a rule about how MANY
        /// rather than about order.</summary>
        internal int CallCountIn(string typeName, string method, string member)
        {
            int count = 0;
            foreach (Instruction instruction in Instructions(Method(typeName, method)))
            {
                if (instruction.IsNewObject) continue;
                if ($"{instruction.DeclaringType}.{instruction.Member}" == member) count++;
            }
            return count;
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

                    // A GENERIC METHOD IS CALLED THROUGH A MethodSpec, never through the definition, so a scan
                    // that stopped at the two handle kinds above was blind to every call to one. That mattered
                    // the moment the device's construction scope grew a generic Track.
                    case HandleKind.MethodSpecification:
                        MethodSpecification specification =
                            _md.GetMethodSpecification((MethodSpecificationHandle)handle);
                        return TryResolve(MetadataTokens.GetToken(specification.Method), out declaringType,
                            out member);

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
    internal sealed class NameOnlySignatures : ISignatureTypeProvider<string, object?>
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
