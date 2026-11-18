using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Collects generic bindings only after a step on a type parameter has been found.</summary>
    internal static class SubnormalFloatGenericWalk
    {
        public static void Record(CompilationAnalysisContext context, SubnormalFloatGenericUses uses)
        {
            var scope = new SubnormalFloatSourceScope(context);
            var selected = new SubnormalFloatSelectedUses(uses);
            var cancellationToken = context.CancellationToken;
            foreach (var tree in context.Compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!scope.ShouldAnalyzeTree(tree)) continue;

                var model = context.Compilation.GetSemanticModel(tree);
                var visitedRoots = new HashSet<IOperation>();
                foreach (var node in tree.GetRoot(cancellationToken).DescendantNodesAndSelf())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (node is GenericNameSyntax name && !scope.IsGenerated(name, model))
                        uses.RecordName(model.GetSymbolInfo(name, cancellationToken).Symbol, name);

                    var entry = ExecutableEntry(node);
                    if (entry is null || scope.IsGenerated(entry, model)) continue;
                    if (model.GetOperation(entry, cancellationToken) is not { } operation) continue;

                    // Several entry points can address the same operation tree, including implicit children.
                    while (operation.Parent is { } parent) operation = parent;
                    if (!visitedRoots.Add(operation)) continue;
                    foreach (var part in operation.DescendantsAndSelf())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsUse(part) || scope.IsGenerated(part.Syntax, model)) continue;
                        RecordUse(part, uses, selected);
                    }
                }
            }
        }

        private static SyntaxNode? ExecutableEntry(SyntaxNode node) => node switch
        {
            BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or ArrowExpressionClauseSyntax or
                EqualsValueClauseSyntax or AttributeSyntax or ConstructorInitializerSyntax or
                PrimaryConstructorBaseTypeSyntax => node,
            BlockSyntax { Parent: BaseMethodDeclarationSyntax or AccessorDeclarationSyntax } => node,
            GlobalStatementSyntax global => global.Statement,
            _ => null,
        };

        private static bool IsUse(IOperation operation) => operation.Kind is
            OperationKind.Invocation or OperationKind.MethodReference or OperationKind.ObjectCreation or
            OperationKind.PropertyReference or OperationKind.FieldReference or OperationKind.EventReference or
            OperationKind.CollectionExpression or OperationKind.Conversion or OperationKind.Binary or
            OperationKind.Unary or OperationKind.CompoundAssignment or OperationKind.Increment or
            OperationKind.Decrement or OperationKind.Loop or OperationKind.Using or OperationKind.UsingDeclaration or
            OperationKind.DeconstructionAssignment or OperationKind.RecursivePattern;

        private static void RecordUse(IOperation operation, SubnormalFloatGenericUses uses, SubnormalFloatSelectedUses selected)
        {
            switch (operation)
            {
                case IInvocationOperation invocation:
                    uses.RecordUse(invocation, invocation.TargetMethod);
                    break;
                case IObjectCreationOperation creation:
                    uses.RecordUse(creation, creation.Constructor);
                    break;
                case IMemberReferenceOperation reference:
                    uses.RecordUse(reference, reference.Member);
                    break;
                default:
                    selected.Record(operation);
                    break;
            }
        }
    }
}
