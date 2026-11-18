using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Checks constructed types and members selected by C# syntax without a named call.</summary>
    internal sealed class SubnormalFloatSelectedUses
    {
        private readonly SubnormalFloatGenericUses _genericUses;

        public SubnormalFloatSelectedUses(SubnormalFloatGenericUses genericUses) => _genericUses = genericUses;

        public void Record(OperationAnalysisContext context)
        {
            var syntax = context.Operation.Syntax;
            switch (context.Operation)
            {
                case ICollectionExpressionOperation collection:
                    _genericUses.RecordUse(collection.ConstructMethod, syntax);
                    _genericUses.RecordUse(collection.Type, syntax);
                    break;
                case IConversionOperation { OperatorMethod: { } } conversion:
                    var operandType = OperandType(conversion.Operand);
                    _genericUses.RecordUse(conversion.OperatorMethod, syntax, operandType);
                    _genericUses.RecordUse(conversion.Type, syntax, operandType);
                    break;
                case IBinaryOperation { OperatorMethod: { } } binary:
                    _genericUses.RecordUse(binary.OperatorMethod, syntax, OperandType(binary.LeftOperand),
                        OperandType(binary.RightOperand));
                    break;
                case IUnaryOperation { OperatorMethod: { } } unary:
                    _genericUses.RecordUse(unary.OperatorMethod, syntax, OperandType(unary.Operand));
                    break;
                case ICompoundAssignmentOperation { OperatorMethod: { } } assignment:
                    _genericUses.RecordUse(assignment.OperatorMethod, syntax, OperandType(assignment.Target),
                        OperandType(assignment.Value));
                    break;
                case IIncrementOrDecrementOperation { OperatorMethod: { } } increment:
                    _genericUses.RecordUse(increment.OperatorMethod, syntax, OperandType(increment.Target));
                    break;
                case IForEachLoopOperation loop when syntax is CommonForEachStatementSyntax each:
                    RecordForEach(loop, each);
                    break;
                case IUsingOperation usingOperation:
                    RecordUsing(usingOperation.Resources, syntax);
                    break;
                case IUsingDeclarationOperation declaration:
                    RecordUsing(declaration.DeclarationGroup, syntax);
                    break;
                case IDeconstructionAssignmentOperation deconstruction when syntax is AssignmentExpressionSyntax deconstruct
                    && deconstruction.SemanticModel is { } model:
                    RecordDeconstruction(model.GetDeconstructionInfo(deconstruct), syntax, OperandType(deconstruction.Value));
                    break;
                case IRecursivePatternOperation { DeconstructSymbol: { } } pattern:
                    _genericUses.RecordUse(pattern.DeconstructSymbol, syntax, pattern.InputType);
                    _genericUses.RecordUse(pattern.MatchedType, syntax, pattern.InputType);
                    break;
            }
        }

        private void RecordForEach(IForEachLoopOperation loop, CommonForEachStatementSyntax syntax)
        {
            if (loop.SemanticModel is not { } model) return;
            var info = model.GetForEachStatementInfo(syntax);
            var input = OperandType(loop.Collection);
            var collectionSite = syntax.Expression;
            _genericUses.RecordUse(info.GetEnumeratorMethod, collectionSite, input);
            _genericUses.RecordUse(info.MoveNextMethod, collectionSite, input);
            _genericUses.RecordUse(info.CurrentProperty, collectionSite, input);
            _genericUses.RecordUse(info.DisposeMethod, collectionSite, input);
            _genericUses.RecordUse(info.ElementType, collectionSite, input);
            if (syntax is ForEachVariableStatementSyntax deconstruct)
                RecordDeconstruction(model.GetDeconstructionInfo(deconstruct), deconstruct.Variable, info.ElementType);
        }

        private void RecordUsing(IOperation resources, SyntaxNode syntax)
        {
            // Roslyn exposes the resolved resource type here. An initializer has already established its own type.
            if (resources is IVariableDeclarationGroupOperation group)
            {
                foreach (var declaration in group.Declarations)
                foreach (var resource in declaration.Declarators)
                    _genericUses.RecordUse(resource.Symbol.Type, resource.Syntax,
                        OperandType((resource.Initializer ?? declaration.Initializer)?.Value));
            }
            else
                _genericUses.RecordUse(resources.Type, syntax, OperandType(resources));
        }

        private void RecordDeconstruction(DeconstructionInfo info, SyntaxNode syntax, ITypeSymbol? input)
        {
            _genericUses.RecordUse(info.Method, syntax, input);
            foreach (var nested in info.Nested)
                RecordDeconstruction(nested, syntax, input);
        }

        private static ITypeSymbol? OperandType(IOperation? operation)
        {
            while (operation is IConversionOperation { IsImplicit: true, OperatorMethod: null } conversion)
                operation = conversion.Operand;
            return operation?.Type;
        }
    }
}
