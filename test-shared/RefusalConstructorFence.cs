using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Finds direct callers of forbidden exception constructors in the supplied assemblies.</summary>
internal static class RefusalConstructorFence
{
    static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => opcode.Value);
    static readonly TypeNames Names = new();

    internal static IReadOnlyList<string> EngineCallers(IEnumerable<Assembly> assemblies,
        IEnumerable<ConstructorInfo> constructors)
    {
        var forbidden = constructors.Select(constructor => (
            Type: TypeNames.FromType(constructor.DeclaringType!),
            Parameters: constructor.GetParameters()
                .Select(parameter => TypeNames.FromType(parameter.ParameterType)).ToArray())).ToArray();
        var callers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Assembly assembly in assemblies)
        {
            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            MetadataReader metadata = pe.GetMetadataReader();
            foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
            {
                MethodDefinition method = metadata.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0
                    || (method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL)
                    continue;

                BlobReader il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
                if (CallsForbiddenConstructor(metadata, il, forbidden))
                    callers.Add(Names.GetTypeFromDefinition(metadata, method.GetDeclaringType(), 0)
                        .Name + "." + metadata.GetString(method.Name));
            }
        }
        return callers.ToArray();
    }

    static bool CallsForbiddenConstructor(MetadataReader metadata, BlobReader il,
        (string Type, string[] Parameters)[] forbidden)
    {
        while (il.RemainingBytes > 0)
        {
            short code = il.ReadByte();
            if (code == 0xFE) code = unchecked((short)(0xFE00 | il.ReadByte()));
            if (!Opcodes.TryGetValue(code, out OpCode opcode))
                throw new BadImageFormatException("Unrecognized IL opcode.");

            if (opcode.OperandType == OperandType.InlineSwitch)
            {
                int count = il.ReadInt32();
                if (count < 0 || count > il.RemainingBytes / sizeof(int))
                    throw new BadImageFormatException("Invalid IL switch table.");
                il.Offset += count * sizeof(int);
                continue;
            }

            int size = OperandSize(opcode.OperandType);
            if (size > il.RemainingBytes) throw new BadImageFormatException("Truncated IL operand.");
            if (opcode == OpCodes.Newobj || opcode == OpCodes.Call)
            {
                var signature = ConstructorSignature(metadata, MetadataTokens.EntityHandle(il.ReadInt32()));
                if (signature is null && opcode == OpCodes.Newobj)
                    throw new BadImageFormatException("Newobj does not reference a constructor.");
                if (signature is { } target && forbidden.Any(candidate => candidate.Type == target.Type
                    && candidate.Parameters.SequenceEqual(target.Parameters, StringComparer.Ordinal)))
                    return true;
            }
            else
            {
                il.Offset += size;
            }
        }
        return false;
    }

    static (string Type, ImmutableArray<string> Parameters)? ConstructorSignature(
        MetadataReader metadata, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                MemberReference member = metadata.GetMemberReference((MemberReferenceHandle)handle);
                if (metadata.GetString(member.Name) != ".ctor") return null;
                SignatureType declaringType = Names.FromHandle(metadata, member.Parent);
                // Member signatures retain !n even when their declaring type is a closed instantiation.
                return (declaringType.Name,
                    member.DecodeMethodSignature(Names, declaringType.TypeArguments).ParameterTypes
                        .Select(parameter => parameter.Name).ToImmutableArray());
            case HandleKind.MethodDefinition:
                MethodDefinition method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
                if (metadata.GetString(method.Name) != ".ctor") return null;
                return (Names.GetTypeFromDefinition(metadata, method.GetDeclaringType(), 0).Name,
                    method.DecodeSignature(Names, genericContext: default).ParameterTypes
                        .Select(parameter => parameter.Name).ToImmutableArray());
            case HandleKind.MethodSpecification:
                return null;
            default:
                throw new BadImageFormatException("Invalid constructor token.");
        }
    }

    static int OperandSize(OperandType type) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => throw new BadImageFormatException("Unsupported IL operand type."),
    };

    // Only the supplied constructor identities use reflection. Scanned types stay in metadata so their
    // platform-specific dependencies never enter the test process.
    readonly record struct SignatureType(string Name, ImmutableArray<string> TypeArguments = default);

    sealed class TypeNames : ISignatureTypeProvider<SignatureType, ImmutableArray<string>>
    {
        internal static string FromType(Type type)
        {
            if (type.IsArray)
                return FromType(type.GetElementType()!) + (type.IsSZArray ? "[]"
                    : type.GetArrayRank() == 1 ? "[*]" : "[" + new string(',', type.GetArrayRank() - 1) + "]");
            if (type.IsByRef) return FromType(type.GetElementType()!) + "&";
            if (type.IsPointer) return FromType(type.GetElementType()!) + "*";
            if (type.IsGenericParameter)
                return (type.DeclaringMethod is null ? "!" : "!!") + type.GenericParameterPosition;
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
                return type.GetGenericTypeDefinition().FullName + "["
                    + string.Join(",", type.GetGenericArguments().Select(FromType)) + "]";
            return type.FullName!;
        }

        internal SignatureType FromHandle(MetadataReader reader, EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => GetTypeFromSpecification(reader, default, (TypeSpecificationHandle)handle, 0),
            _ => throw new BadImageFormatException("Invalid constructor declaring type."),
        };

        public SignatureType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            TypeDefinitionHandle parent = type.GetDeclaringType();
            return new(parent.IsNil ? Qualify(reader.GetString(type.Namespace), reader.GetString(type.Name))
                : GetTypeFromDefinition(reader, parent, 0).Name + "+" + reader.GetString(type.Name));
        }

        public SignatureType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference type = reader.GetTypeReference(handle);
            return new(type.ResolutionScope.Kind == HandleKind.TypeReference
                ? GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, 0).Name
                    + "+" + reader.GetString(type.Name)
                : Qualify(reader.GetString(type.Namespace), reader.GetString(type.Name)));
        }

        public SignatureType GetTypeFromSpecification(MetadataReader reader, ImmutableArray<string> genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public SignatureType GetPrimitiveType(PrimitiveTypeCode typeCode) => new("System." + typeCode);
        public SignatureType GetSZArrayType(SignatureType elementType) => new(elementType.Name + "[]");
        public SignatureType GetArrayType(SignatureType elementType, ArrayShape shape)
            => new(elementType.Name + (shape.Rank == 1 ? "[*]" : "[" + new string(',', shape.Rank - 1) + "]"));
        public SignatureType GetByReferenceType(SignatureType elementType) => new(elementType.Name + "&");
        public SignatureType GetPointerType(SignatureType elementType) => new(elementType.Name + "*");
        public SignatureType GetPinnedType(SignatureType elementType) => elementType;
        public SignatureType GetModifiedType(SignatureType modifier, SignatureType unmodifiedType, bool isRequired)
            => unmodifiedType;
        public SignatureType GetGenericInstantiation(SignatureType genericType, ImmutableArray<SignatureType> typeArguments)
        {
            var arguments = typeArguments.Select(argument => argument.Name).ToImmutableArray();
            return new(genericType.Name + "[" + string.Join(",", arguments) + "]", arguments);
        }
        public SignatureType GetGenericTypeParameter(ImmutableArray<string> genericContext, int index)
            => new(genericContext.IsDefault ? "!" + index : genericContext[index]);
        public SignatureType GetGenericMethodParameter(ImmutableArray<string> genericContext, int index) => new("!!" + index);
        public SignatureType GetFunctionPointerType(MethodSignature<SignatureType> signature)
            => new("methodptr(" + string.Join(",", signature.ParameterTypes.Select(parameter => parameter.Name))
                + ")->" + signature.ReturnType.Name);

        static string Qualify(string space, string name) => space.Length == 0 ? name : space + "." + name;
    }
}
