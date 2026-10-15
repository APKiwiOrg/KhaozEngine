using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Movement.Research;

internal sealed class CanonicalProfileFile
{
    private readonly byte[] identity;
    private readonly object columns;
    private readonly ConstructorInfo compose;
    private readonly NavBakeExpectation expected;

    internal CanonicalProfileFile(PhysicsNavBake capture)
    {
        // There is no public composition API for an already-built profile. Resolve this test-only
        // adapter before timing rather than run Create, which would build a second profile.
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        Type encoding = typeof(PhysicsNavBake).Assembly.GetType("KhaozEngine.Movement.NavBakeIdentity", throwOnError: true)!;
        expected = new NavBakeExpectation(ProofFixture.Options, ProofFixture.Sources, [ProofFixture.Profile]);
        string version = (string)encoding.GetProperty("CurrentEngineVersion", hidden)!.GetValue(null)!;
        identity = (byte[])encoding.GetMethod("Encode", hidden)!.Invoke(null, [expected, version])!;
        columns = typeof(PhysicsNavBake).GetProperty("Columns", hidden)!.GetValue(capture)!;
        compose = typeof(GroundNavigationBake).GetConstructors(hidden).Single(constructor =>
            constructor.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[]
            { typeof(byte[]), typeof(Vector3), columns.GetType(), typeof(string[]), typeof(GroundNavigation[]) }));
    }

    internal static NavTraversalGraph Graph(GroundNavigation navigation) =>
        (NavTraversalGraph)typeof(GroundNavigation).GetProperty("Graph", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(navigation)!;

    internal byte[] WriteAndCheck(GroundNavigation navigation)
    {
        var regenerated = NavLayerLinks.GenerateGrounded(navigation.Space.Layers, ProofFixture.Tuning.StepHeight);
        if (!regenerated.SequenceEqual(navigation.Space.Links))
            throw new InvalidDataException("Candidate links differ from canonical grid regeneration.");
        var bake = (GroundNavigationBake)compose.Invoke(
            [identity, Vector3.Zero, columns, new[] { ProofFixture.Profile.Name }, new[] { navigation }]);
        byte[] bytes = Write(bake);
        using var input = new MemoryStream(bytes, writable: false);
        NavBakeLoadResult loaded = GroundNavigationBake.Load(input, expected);
        if (loaded.Status != NavBakeLoadStatus.Loaded || loaded.Bake is null ||
            !bytes.AsSpan().SequenceEqual(Write(loaded.Bake)))
            throw new InvalidDataException("KENB load and canonical rewrite failed.");
        return bytes;
    }

    private static byte[] Write(GroundNavigationBake bake)
    {
        using var stream = new MemoryStream();
        bake.WriteTo(stream);
        return stream.ToArray();
    }
}
