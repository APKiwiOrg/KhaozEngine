using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Tracks which member steps each generic type parameter, including forwarding within the compilation.</summary>
    internal sealed class SubnormalFloatGenericUses
    {
        private readonly IAssemblySymbol _assembly;
        private readonly ConcurrentDictionary<ISymbol, ConcurrentDictionary<ISymbol, byte>> _directSteps =
            new ConcurrentDictionary<ISymbol, ConcurrentDictionary<ISymbol, byte>>(SymbolEqualityComparer.Default);
        private readonly ConcurrentBag<GenericUse> _uses = new ConcurrentBag<GenericUse>();

        public SubnormalFloatGenericUses(IAssemblySymbol assembly) => _assembly = assembly;

        public void RecordStep(ISymbol member, ITypeParameterSymbol parameter) =>
            _directSteps.GetOrAdd(member.OriginalDefinition, _ => new ConcurrentDictionary<ISymbol, byte>(
                SymbolEqualityComparer.Default)).TryAdd(parameter.OriginalDefinition, 0);

        public void RecordUse(OperationAnalysisContext context, IMethodSymbol? target)
        {
            if (target is null || !SymbolEqualityComparer.Default.Equals(target.ContainingAssembly, _assembly)) return;
            // A reduced extension method omits the type arguments inferred from its receiver.
            var member = target.ReducedFrom is null ? target : target.GetConstructedReducedFrom() ?? target;
            List<TypeArgumentPair>? pairs = null;
            if (member.IsGenericMethod)
                AddPairs(ref pairs, member.OriginalDefinition.TypeParameters, member.TypeArguments);
            for (var type = member.ContainingType; type != null; type = type.ContainingType)
            {
                if (type.IsGenericType)
                    AddPairs(ref pairs, type.OriginalDefinition.TypeParameters, type.TypeArguments);
            }
            if (pairs != null)
                _uses.Add(new GenericUse(context.ContainingSymbol.OriginalDefinition, member.OriginalDefinition,
                    pairs.ToArray(), context.Operation.Syntax.GetLocation()));
        }

        public void Report(CompilationAnalysisContext context)
        {
            if (_directSteps.IsEmpty) return;
            var steps = _directSteps.ToDictionary(entry => entry.Key,
                entry => new HashSet<ISymbol>(entry.Value.Keys, SymbolEqualityComparer.Default),
                SymbolEqualityComparer.Default);
            var uses = _uses.ToArray();

            // A caller steps its own parameter if it passes it to a member that steps it.
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var use in uses)
                {
                    if (!steps.TryGetValue(use.Target, out var steppedParameters)) continue;
                    foreach (var pair in use.Pairs)
                    {
                        if (!(pair.Argument is ITypeParameterSymbol forwarded) || !steppedParameters.Contains(pair.Parameter))
                            continue;
                        if (!steps.TryGetValue(use.Caller, out var callerParameters))
                        {
                            callerParameters = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                            steps.Add(use.Caller, callerParameters);
                        }
                        if (callerParameters.Add(forwarded.OriginalDefinition)) grew = true;
                    }
                }
            }
            var reportedLocations = new HashSet<Location>();
            foreach (var use in uses)
            {
                if (!steps.TryGetValue(use.Target, out var steppedParameters)) continue;
                foreach (var pair in use.Pairs)
                {
                    if (pair.Argument.SpecialType != SpecialType.System_Single || !steppedParameters.Contains(pair.Parameter))
                        continue;
                    if (reportedLocations.Add(use.Location))
                    {
                        var member = (use.Target as IMethodSymbol)?.AssociatedSymbol ?? use.Target;
                        context.ReportDiagnostic(Diagnostic.Create(SubnormalFloatDiagnostics.SubnormalFloatConstant,
                            use.Location, $"'{member.Name}' steps its type parameter '{pair.Parameter.Name}' " +
                            "with BitDecrement or BitIncrement and is used here with float"));
                    }
                    break;
                }
            }
        }

        private static void AddPairs(ref List<TypeArgumentPair>? pairs,
            ImmutableArray<ITypeParameterSymbol> parameters, ImmutableArray<ITypeSymbol> arguments)
        {
            for (int i = 0; i < parameters.Length && i < arguments.Length; i++)
            {
                var argument = arguments[i];
                if (argument.SpecialType != SpecialType.System_Single && !(argument is ITypeParameterSymbol)) continue;
                (pairs ??= new List<TypeArgumentPair>()).Add(new TypeArgumentPair(parameters[i].OriginalDefinition, argument));
            }
        }

        private readonly struct TypeArgumentPair
        {
            public TypeArgumentPair(ITypeParameterSymbol parameter, ITypeSymbol argument)
            {
                Parameter = parameter;
                Argument = argument;
            }

            public ITypeParameterSymbol Parameter { get; }
            public ITypeSymbol Argument { get; }
        }

        private sealed class GenericUse
        {
            public GenericUse(ISymbol caller, ISymbol target, TypeArgumentPair[] pairs, Location location)
            {
                Caller = caller;
                Target = target;
                Pairs = pairs;
                Location = location;
            }

            public ISymbol Caller { get; }
            public ISymbol Target { get; }
            public TypeArgumentPair[] Pairs { get; }
            public Location Location { get; }
        }
    }
}
