using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Checks source attributes and their encoded metadata layout for storage aliasing.</summary>
    internal sealed class Binary32FieldLayout
    {
        private readonly ConcurrentDictionary<ISymbol, bool> _reinterpreted =
            new ConcurrentDictionary<ISymbol, bool>(SymbolEqualityComparer.Default);

        public bool IsReinterpreted(Compilation compilation, IFieldSymbol field, CancellationToken cancellationToken) =>
            _reinterpreted.GetOrAdd(field.OriginalDefinition, symbol =>
                Check(compilation, (IFieldSymbol)symbol, cancellationToken));

        private static bool Check(Compilation compilation, IFieldSymbol field, CancellationToken cancellationToken)
        {
            if (field.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.FieldOffsetAttribute")
                || field.ContainingType.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.StructLayoutAttribute"
                    && attribute.ConstructorArguments.Length == 1
                    && attribute.ConstructorArguments[0].Value is int layout
                    && layout == (int)System.Runtime.InteropServices.LayoutKind.Explicit)) return true;
            if (field.Locations.Any(location => location.IsInSource)) return false;

            // StructLayout and FieldOffset become metadata flags/tables, not custom attributes.
            // Inspect Roslyn's reference image rather than loading or executing the assembly.
            if (!(compilation.GetMetadataReference(field.ContainingAssembly) is PortableExecutableReference reference))
                return true;
            try
            {
                var metadata = reference.GetMetadata();
                var modules = metadata is AssemblyMetadata assembly ? assembly.GetModules()
                    : ImmutableArray.Create((ModuleMetadata)metadata);
                var expectedName = MetadataName(field.ContainingType);
                foreach (var module in modules)
                {
                    var reader = module.GetMetadataReader();
                    foreach (var handle in reader.TypeDefinitions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (MetadataName(reader, handle) != expectedName) continue;
                        var type = reader.GetTypeDefinition(handle);
                        if ((type.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout) return true;
                        foreach (var fieldHandle in type.GetFields())
                        {
                            var definition = reader.GetFieldDefinition(fieldHandle);
                            if (reader.GetString(definition.Name) == field.MetadataName)
                                return definition.GetOffset() >= 0;
                        }
                    }
                }
            }
            catch (BadImageFormatException) { return true; }
            catch (IOException) { return true; }
            // Missing or unreadable layout cannot establish independent field storage.
            return true;
        }

        private static string MetadataName(INamedTypeSymbol type)
        {
            if (type.ContainingType != null) return MetadataName(type.ContainingType) + "+" + type.MetadataName;
            var ns = type.ContainingNamespace;
            return ns.IsGlobalNamespace ? type.MetadataName : ns.ToDisplayString() + "." + type.MetadataName;
        }

        private static string MetadataName(MetadataReader reader, TypeDefinitionHandle handle)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = reader.GetString(type.Name);
            var parent = type.GetDeclaringType();
            if (!parent.IsNil) return MetadataName(reader, parent) + "+" + name;
            var ns = reader.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }
    }
}
