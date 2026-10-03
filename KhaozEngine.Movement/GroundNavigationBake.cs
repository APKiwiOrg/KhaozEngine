using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>One capture's columns and one or more named <see cref="GroundNavigation"/> profiles, written as a
/// versioned <c>KENB</c> file and loaded without physics, a ground provider or any proof. A loaded profile is
/// equivalent to a fresh <see cref="PhysicsNavBake.BuildProfile(in KhaozEngine.Locomotion.MoveTuning, NavAreaFilter)"/>
/// from the same inputs. Loading refuses a bake whose engine version, capture options, source digests or profiles
/// differ from the expectation, and never substitutes a fresh build.</summary>
public sealed partial class GroundNavigationBake
{
    /// <summary>Per-cell surface counts are stored as bytes, so a bake holds at most 255 surfaces per column.</summary>
    internal const int MaxStoredSurfacesPerColumn = byte.MaxValue;

    /// <summary>Largest identity block a bake writes or reads.</summary>
    internal const int MaxIdentityLength = 1 << 20;

    private readonly byte[] _identity;
    private readonly byte[] _fingerprint;
    private readonly Vector3 _origin;
    private readonly PhysicsNavColumns _columns;
    private readonly string[] _names;
    private readonly GroundNavigation[] _profiles;
    private readonly Dictionary<string, GroundNavigation> _byName;

    private GroundNavigationBake(byte[] identity, Vector3 origin, PhysicsNavColumns columns,
        string[] names, GroundNavigation[] profiles)
    {
        _identity = identity;
        _fingerprint = SHA256.HashData(identity);
        _origin = origin;
        _columns = columns;
        _names = names;
        _profiles = profiles;
        _byName = new Dictionary<string, GroundNavigation>(names.Length, StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++) _byName.Add(names[i], profiles[i]);
        ProfileNames = Array.AsReadOnly(names);
    }

    /// <summary>SHA-256 of the identity block, for logs and build manifests.</summary>
    public ReadOnlySpan<byte> Fingerprint => _fingerprint;

    /// <summary>Profile names in identity order, which is ordinal order.</summary>
    public IReadOnlyList<string> ProfileNames { get; }

    /// <summary>Builds every profile through <paramref name="capture"/> while it is live, so each profile is exactly
    /// the fresh build. <paramref name="sources"/> and <paramref name="profiles"/> are snapshot, so later edits never
    /// change this bake.</summary>
    /// <exception cref="ObjectDisposedException">The capture has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The capture allows more than 255 surfaces per column, or a
    /// profile tuning is invalid.</exception>
    /// <exception cref="ArgumentException">A source, profile name, count or tuning is invalid, a profile slope differs
    /// from the capture slope, or the identity block exceeds 1 MiB.</exception>
    /// <exception cref="InvalidOperationException">The physics origin changed, or a fresh profile's candidate links
    /// differ from the list regenerated from its grids, which is an engine defect.</exception>
    public static GroundNavigationBake Create(PhysicsNavBake capture, NavBakeSources sources,
        IReadOnlyList<NavBakeProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(profiles);
        _ = capture.Context;
        PhysicsNavBakeOptions options = capture.Options;
        if (options.MaxSurfacesPerColumn > MaxStoredSurfacesPerColumn)
            throw new ArgumentOutOfRangeException(nameof(capture), "A bake stores at most 255 surfaces per column.");
        NavBakeProfile[] snapshot = [.. profiles];
        byte[] identity = NavBakeIdentity.Encode(new NavBakeExpectation(options, sources, snapshot),
            NavBakeIdentity.CurrentEngineVersion);
        if (identity.Length > MaxIdentityLength)
            throw new ArgumentException("The bake identity block exceeds 1 MiB.", nameof(sources));
        NavBakeProfile[] sorted = SortedByName(snapshot);

        var names = new string[sorted.Length];
        var built = new GroundNavigation[sorted.Length];
        for (int i = 0; i < sorted.Length; i++)
        {
            NavBakeProfile profile = sorted[i];
            GroundNavigation navigation = capture.BuildProfile(profile.Tuning, profile.Areas);
            IReadOnlyList<NavLink> regenerated = NavLayerLinks.GenerateGrounded(navigation.Space.Layers, profile.Tuning.StepHeight);
            if (!SameLinks(regenerated, navigation.Space.Links))
                throw new InvalidOperationException(
                    $"Profile '{profile.Name}' candidate links differ from the list regenerated from its grids.");
            names[i] = profile.Name;
            built[i] = navigation;
        }
        return new GroundNavigationBake(identity, capture.Origin, capture.Columns, names, built);
    }

    /// <summary>Loads a bake when its identity equals the one encoded from <paramref name="expected"/>. Checks run in
    /// container, identity and payload order, and the first failing check decides the status. A stale bake is refused
    /// after the header and identity block, without reading the payload. Content problems never throw. Stream errors
    /// such as <see cref="IOException"/> propagate.</summary>
    /// <exception cref="ArgumentException">The expectation is invalid, as <see cref="Create"/> would refuse it.</exception>
    public static NavBakeLoadResult Load(Stream source, NavBakeExpectation expected)
        => Load(source, expected, NavBakeIdentity.CurrentEngineVersion);

    /// <summary>The named profile.</summary>
    /// <exception cref="KeyNotFoundException">No profile has that name.</exception>
    public GroundNavigation GetProfile(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out GroundNavigation? profile)
            ? profile : throw new KeyNotFoundException($"The bake has no profile named '{name}'.");
    }

    private static NavBakeProfile[] SortedByName(NavBakeProfile[] profiles)
    {
        var sorted = (NavBakeProfile[])profiles.Clone();
        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return sorted;
    }

    private static bool SameLinks(IReadOnlyList<NavLink> a, IReadOnlyList<NavLink> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
