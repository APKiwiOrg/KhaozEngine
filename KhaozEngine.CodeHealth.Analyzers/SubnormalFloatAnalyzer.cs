using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>
    /// KEFP001 flags float code that puts a subnormal constant where the JIT can fold it. The analyzer reaches
    /// consumer projects through the umbrella packages, so it stays silent unless the compilation opts in through
    /// <see cref="SubnormalFloatDiagnostics.ScopeProperty"/>. The engine opts in from its Directory.Build.props.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class SubnormalFloatAnalyzer : DiagnosticAnalyzer
    {
        // Every operation kind the C# compiler can give a constant value of type float.
        private static readonly OperationKind[] ConstantKinds =
        {
            OperationKind.Literal, OperationKind.FieldReference, OperationKind.LocalReference,
            OperationKind.Conversion, OperationKind.Unary, OperationKind.Binary, OperationKind.Conditional,
            OperationKind.DefaultValue,
        };

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(SubnormalFloatDiagnostics.SubnormalFloatConstant);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            if (!InScope(context.Options, context.Compilation)) return;
            var run = new SubnormalFloatRun(context.Compilation);
            context.RegisterOperationAction(run.CheckConstant, ConstantKinds);
            context.RegisterOperationAction(run.CheckInvocation, OperationKind.Invocation);
            context.RegisterOperationAction(run.CheckMethodReference, OperationKind.MethodReference);
            context.RegisterOperationAction(run.CheckObjectCreation, OperationKind.ObjectCreation);
            context.RegisterOperationAction(run.CheckPropertyReference, OperationKind.PropertyReference);
            context.RegisterOperationAction(run.RecordFieldInitializer, OperationKind.FieldInitializer);
            context.RegisterCompilationEndAction(run.ReportDeferred);
        }

        private static bool InScope(AnalyzerOptions options, Compilation compilation)
        {
            if (!options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                    "build_property." + SubnormalFloatDiagnostics.ScopeProperty, out var value)) return false;
            value = value.Trim();
            if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)) return true;
            // A test compilation compares against the BCL steps on purpose, so "on" leaves it alone.
            return string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
                && compilation.GetTypeByMetadataName("Xunit.FactAttribute") is null;
        }
    }
}
