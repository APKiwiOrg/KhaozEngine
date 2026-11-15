using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Tests.WaterPrismCensus;

// One shared scenario roster for the exact-output corpus and the report-only diagnostic.
internal static class ContactQueryCostCases
{
    internal static readonly string[] Names =
        ["dry-stand", "dry-walk", "dry-colliders", "flat-water", "shore-stand", "shore-walk"];

    internal static WaterPrismScenario Create(string name, MovementControllerKind controller = MovementControllerKind.Contact)
    {
        if (Array.IndexOf(Names, name) < 0) throw new ArgumentOutOfRangeException(nameof(name));
        string source = name == "flat-water" ? "flat-control" :
            name.StartsWith("shore", StringComparison.Ordinal) ? "bank-stand" : "dry-control";
        return new(source, controller, name == "dry-colliders");
    }

    internal static MoveCommand Command(string name) => name.EndsWith("stand", StringComparison.Ordinal)
        ? MoveCommand.Idle : new(Vector2.UnitX, false, 0);
}
