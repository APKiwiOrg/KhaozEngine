using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Tracks stepped generic parameters and their specializations within a compilation.</summary>
    internal sealed class SubnormalFloatGenericUses
    {
        private readonly IAssemblySymbol _assembly;
        private readonly HashSet<ITypeParameterSymbol> _stepped;
        private readonly List<(ITypeParameterSymbol Target, ITypeParameterSymbol Forwarded)> _forwardings =
            new List<(ITypeParameterSymbol, ITypeParameterSymbol)>();
        private readonly List<(ITypeParameterSymbol Parameter, SyntaxNode Syntax)> _closures =
            new List<(ITypeParameterSymbol, SyntaxNode)>();

        public SubnormalFloatGenericUses(IAssemblySymbol assembly, IEnumerable<ITypeParameterSymbol> directSteps)
        {
            _assembly = assembly;
            _stepped = new HashSet<ITypeParameterSymbol>(directSteps.Select(DeclarationParameter), SymbolEqualityComparer.Default);
        }

        public void RecordName(ISymbol? symbol, GenericNameSyntax syntax)
        {
            // A containing type explicitly written to the left of this name has its own syntax action.
            if (symbol is INamedTypeSymbol type)
                RecordNamedType(type, syntax, default);
            else if (symbol is IMethodSymbol method)
                RecordMethod(method, syntax, default);
        }

        public void RecordUse(IOperation operation, ISymbol? target)
        {
            RecordUse(target, operation.Syntax);
            RecordType(operation.Type, operation.Syntax, default);
        }

        public void RecordUse(ISymbol? target, SyntaxNode syntax, ITypeSymbol? input = null, ITypeSymbol? otherInput = null)
        {
            var inputs = new InputTypes(input, otherInput);
            if (target is IMethodSymbol method)
                RecordMethod(method, syntax, inputs);
            RecordType(target as ITypeSymbol ?? target?.ContainingType, syntax, inputs);
        }

        public void Report(CompilationAnalysisContext context)
        {
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var (target, forwarded) in _forwardings)
                {
                    if (_stepped.Contains(target)) grew |= _stepped.Add(forwarded);
                }
            }

            var closures = _closures.Where(use => _stepped.Contains(use.Parameter)).ToArray();
            var namedRegions = new Dictionary<ITypeParameterSymbol, HashSet<Location>>(SymbolEqualityComparer.Default);
            var reportedLocations = new HashSet<Location>();

            // Recursion may discover the same closure at an outer name. Prefer the innermost explicit name.
            foreach (var (parameter, syntax) in closures.Where(use => use.Syntax is GenericNameSyntax)
                         .OrderBy(use => use.Syntax.Span.Length))
            {
                if (!namedRegions.TryGetValue(parameter, out var covered))
                {
                    covered = new HashSet<Location>();
                    namedRegions.Add(parameter, covered);
                }
                if (covered.Contains(syntax.GetLocation())) continue;
                foreach (var ancestor in syntax.AncestorsAndSelf())
                    covered.Add(ancestor.GetLocation());
                ReportClosure(context, parameter, syntax.GetLocation(), reportedLocations);
            }

            foreach (var (parameter, syntax) in closures)
            {
                if (namedRegions.TryGetValue(parameter, out var covered) &&
                    covered.Contains(ExplicitNameRegion(syntax).GetLocation())) continue;
                ReportClosure(context, parameter, syntax.GetLocation(), reportedLocations);
            }
        }

        private void RecordMethod(IMethodSymbol method, SyntaxNode syntax, InputTypes inputs)
        {
            // A reduced extension method can omit arguments inferred from its receiver.
            if (method.ReducedFrom != null)
                method = method.GetConstructedReducedFrom() ?? method;
            if (method.IsGenericMethod)
                RecordPairs(method.OriginalDefinition, method.OriginalDefinition.TypeParameters, method.TypeArguments,
                    syntax, inputs);
        }

        private void RecordType(ITypeSymbol? type, SyntaxNode syntax, InputTypes inputs)
        {
            switch (type)
            {
                case INamedTypeSymbol named:
                    for (var current = named; current != null && !inputs.Contains(current); current = current.ContainingType)
                        RecordNamedType(current, syntax, inputs);
                    break;
                case IArrayTypeSymbol array when !inputs.Contains(array):
                    RecordType(array.ElementType, syntax, inputs);
                    break;
                case IPointerTypeSymbol pointer when !inputs.Contains(pointer):
                    RecordType(pointer.PointedAtType, syntax, inputs);
                    break;
            }
        }

        private void RecordNamedType(INamedTypeSymbol type, SyntaxNode syntax, InputTypes inputs)
        {
            if (type.Arity != 0 && !type.IsUnboundGenericType)
                RecordPairs(type.OriginalDefinition, type.OriginalDefinition.TypeParameters, type.TypeArguments, syntax, inputs);
        }

        private void RecordPairs(ISymbol declaration, ImmutableArray<ITypeParameterSymbol> parameters,
            ImmutableArray<ITypeSymbol> arguments, SyntaxNode syntax, InputTypes inputs)
        {
            bool sameAssembly = SymbolEqualityComparer.Default.Equals(declaration.ContainingAssembly, _assembly);
            for (int i = 0; i < parameters.Length && i < arguments.Length; i++)
            {
                var argument = arguments[i];
                if (sameAssembly && argument.SpecialType == SpecialType.System_Single)
                    _closures.Add((DeclarationParameter(parameters[i]), syntax));
                else if (sameAssembly && argument is ITypeParameterSymbol forwarded)
                {
                    var parameter = DeclarationParameter(parameters[i]);
                    forwarded = DeclarationParameter(forwarded);
                    if (!SymbolEqualityComparer.Default.Equals(parameter, forwarded))
                        _forwardings.Add((parameter, forwarded));
                }
                else
                    RecordType(argument, syntax, inputs);
            }
        }

        private static ITypeParameterSymbol DeclarationParameter(ITypeParameterSymbol parameter)
        {
            parameter = parameter.OriginalDefinition;
            return parameter.ContainingSymbol is IMethodSymbol { PartialDefinitionPart: { } definition }
                ? definition.TypeParameters[parameter.Ordinal]
                : parameter;
        }

        private static SyntaxNode ExplicitNameRegion(SyntaxNode syntax) => syntax switch
        {
            InvocationExpressionSyntax invocation => invocation.Expression,
            ObjectCreationExpressionSyntax creation => creation.Type,
            _ => syntax,
        };

        private static void ReportClosure(CompilationAnalysisContext context, ITypeParameterSymbol parameter,
            Location location, HashSet<Location> reportedLocations)
        {
            if (reportedLocations.Add(location))
                context.ReportDiagnostic(Diagnostic.Create(SubnormalFloatDiagnostics.SubnormalFloatConstant, location,
                    $"'{parameter.ContainingSymbol.Name}' steps its type parameter '{parameter.Name}' " +
                    "with BitDecrement or BitIncrement and is used here with float"));
        }

        private readonly struct InputTypes
        {
            public InputTypes(ITypeSymbol? first, ITypeSymbol? second)
            {
                First = first;
                Second = second;
            }

            private ITypeSymbol? First { get; }
            private ITypeSymbol? Second { get; }

            public bool Contains(ITypeSymbol type) =>
                SymbolEqualityComparer.Default.Equals(type, First) || SymbolEqualityComparer.Default.Equals(type, Second);
        }
    }
}
