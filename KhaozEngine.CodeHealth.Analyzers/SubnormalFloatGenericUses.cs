using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Tracks stepped generic parameters and their specializations within a compilation.</summary>
    internal sealed class SubnormalFloatGenericUses
    {
        private readonly IAssemblySymbol _assembly;
        private readonly ConcurrentDictionary<ITypeParameterSymbol, byte> _directSteps =
            new ConcurrentDictionary<ITypeParameterSymbol, byte>(SymbolEqualityComparer.Default);
        private readonly ConcurrentBag<(ITypeParameterSymbol Parameter, ITypeSymbol Argument, SyntaxNode Syntax)> _uses =
            new ConcurrentBag<(ITypeParameterSymbol, ITypeSymbol, SyntaxNode)>();

        public SubnormalFloatGenericUses(IAssemblySymbol assembly) => _assembly = assembly;

        public void RecordStep(ITypeParameterSymbol parameter) => _directSteps.TryAdd(parameter.OriginalDefinition, 0);

        public void RecordUse(OperationAnalysisContext context, ISymbol? target)
        {
            if (!context.Operation.IsImplicit)
                RecordUse(target, context.Operation.Syntax);
        }

        public void RecordUse(ISymbol? target, SyntaxNode syntax)
        {
            if (target is null || !SymbolEqualityComparer.Default.Equals(target.ContainingAssembly, _assembly)) return;
            if (target is IMethodSymbol method)
            {
                // A reduced extension method omits the type arguments inferred from its receiver.
                method = method.ReducedFrom is null ? method : method.GetConstructedReducedFrom() ?? method;
                if (method.IsGenericMethod)
                    RecordPairs(method.OriginalDefinition.TypeParameters, method.TypeArguments, syntax);
            }
            for (var type = target as INamedTypeSymbol ?? target.ContainingType; type != null; type = type.ContainingType)
            {
                if (type.IsGenericType)
                    RecordPairs(type.OriginalDefinition.TypeParameters, type.TypeArguments, syntax);
            }
        }

        public void Report(CompilationAnalysisContext context)
        {
            if (_directSteps.IsEmpty) return;
            var stepped = new HashSet<ITypeParameterSymbol>(_directSteps.Keys, SymbolEqualityComparer.Default);
            var uses = _uses.ToArray();

            // Forwarding a parameter to a stepped parameter marks it on its own declaration too.
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var use in uses)
                {
                    if (use.Argument is ITypeParameterSymbol forwarded && stepped.Contains(use.Parameter))
                        grew |= stepped.Add(forwarded.OriginalDefinition);
                }
            }

            var reportedLocations = new HashSet<Location>();
            foreach (var use in uses)
            {
                if (use.Argument.SpecialType != SpecialType.System_Single || !stepped.Contains(use.Parameter)) continue;
                var location = use.Syntax.GetLocation();
                if (!reportedLocations.Add(location)) continue;
                context.ReportDiagnostic(Diagnostic.Create(SubnormalFloatDiagnostics.SubnormalFloatConstant, location,
                    $"'{use.Parameter.ContainingSymbol.Name}' steps its type parameter '{use.Parameter.Name}' " +
                    "with BitDecrement or BitIncrement and is used here with float"));
            }
        }

        private void RecordPairs(ImmutableArray<ITypeParameterSymbol> parameters, ImmutableArray<ITypeSymbol> arguments,
            SyntaxNode syntax)
        {
            for (int i = 0; i < parameters.Length && i < arguments.Length; i++)
            {
                var argument = arguments[i];
                var parameter = parameters[i].OriginalDefinition;
                if (argument.SpecialType == SpecialType.System_Single ||
                    (argument is ITypeParameterSymbol forwarded &&
                     !SymbolEqualityComparer.Default.Equals(parameter, forwarded.OriginalDefinition)))
                    _uses.Add((parameter, argument, syntax));
            }
        }
    }
}
