using System.Runtime.CompilerServices;

// Test-to-test edge for the opt-in equipment coverage cost project outside the solution. It calls the internal cost
// harness and nothing else. No production assembly gains a dependency.
[assembly: InternalsVisibleTo("EquipmentCoverage.Cost.Tests")]
