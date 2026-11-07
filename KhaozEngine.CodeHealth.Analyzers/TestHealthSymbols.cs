using Microsoft.CodeAnalysis;

namespace KhaozEngine.CodeHealth.Analyzers
{
    /// <summary>Compilation-local symbols shared by the test-health detectors.</summary>
    internal sealed class TestHealthSymbols
    {
        private TestHealthSymbols(INamedTypeSymbol assert, INamedTypeSymbol factAttribute,
            INamedTypeSymbol exception, INamedTypeSymbol? bindingFlags, INamedTypeSymbol? unsafeAccessorAttribute,
            INamedTypeSymbol? enumerable, INamedTypeSymbol? enumType)
        {
            Assert = assert;
            FactAttribute = factAttribute;
            Exception = exception;
            BindingFlags = bindingFlags;
            UnsafeAccessorAttribute = unsafeAccessorAttribute;
            Enumerable = enumerable;
            Enum = enumType;
        }

        public INamedTypeSymbol Assert { get; }
        public INamedTypeSymbol FactAttribute { get; }
        public INamedTypeSymbol Exception { get; }
        public INamedTypeSymbol? BindingFlags { get; }
        public INamedTypeSymbol? UnsafeAccessorAttribute { get; }
        public INamedTypeSymbol? Enumerable { get; }
        public INamedTypeSymbol? Enum { get; }

        public static TestHealthSymbols? Resolve(Compilation compilation)
        {
            var assert = compilation.GetTypeByMetadataName("Xunit.Assert");
            if (assert is null) return null;

            var factAttribute = compilation.GetTypeByMetadataName("Xunit.FactAttribute");
            var exception = compilation.GetTypeByMetadataName("System.Exception");
            if (factAttribute is null || exception is null) return null;

            return new TestHealthSymbols(assert, factAttribute, exception,
                compilation.GetTypeByMetadataName("System.Reflection.BindingFlags"),
                compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.UnsafeAccessorAttribute"),
                compilation.GetTypeByMetadataName("System.Linq.Enumerable"),
                compilation.GetTypeByMetadataName("System.Enum"));
        }
    }
}
