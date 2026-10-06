using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// THE OPT-IN FOR THE SEAM PROBE'S EVIDENCE, and the only thing that decides whether it is written at all. Unset,
/// the probe runs exactly as it always has. Set, it must also be told which source it is running, because a record
/// that cannot say what rendered it is not evidence.
/// <para>
/// THE SOURCE IDENTITY IS AN INPUT, NEVER A DISCOVERY. The commit and tree come from whoever built the binaries,
/// which on CI is the checkout step, and are validated as full object ids. Nothing here asks git, reads the working
/// directory or guesses. What the loaded assemblies themselves carry is read as well, and an assembly whose
/// informational version names a different revision refuses the run instead of filing evidence under the wrong
/// source. An assembly that carries no revision is recorded as such.
/// </para>
/// <para>
/// Reading the configuration only reads environment variables. It never sets one, and it touches nothing on disk,
/// so ordinary parallel tests cannot see it.
/// </para>
/// </summary>
internal sealed class PointShadowSeamEvidenceOptions
{
    /// <summary>An ABSOLUTE directory to write the evidence into. Unset or empty leaves the probe unconfigured.</summary>
    public const string DirectoryVariable = "KE_POINT_SHADOW_SEAM_EVIDENCE_DIR";

    /// <summary>The 40-character lower-case commit the binaries were built from. Required once the directory is set.</summary>
    public const string CommitVariable = "KE_POINT_SHADOW_SEAM_SOURCE_COMMIT";

    /// <summary>The 40-character lower-case tree of that commit. Required once the directory is set.</summary>
    public const string TreeVariable = "KE_POINT_SHADOW_SEAM_SOURCE_TREE";

    PointShadowSeamEvidenceOptions(string outputDirectory, string sourceCommit, string sourceTree)
    {
        OutputDirectory = outputDirectory;
        SourceCommit = sourceCommit;
        SourceTree = sourceTree;
    }

    public string OutputDirectory { get; }
    public string SourceCommit { get; }
    public string SourceTree { get; }

    /// <summary>The process environment's configuration. See <see cref="Read"/>.</summary>
    public static PointShadowSeamEvidenceOptions? FromEnvironment() => Read(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Null when <see cref="DirectoryVariable"/> is unset or empty. Otherwise the validated configuration, or an
    /// <see cref="InvalidOperationException"/> naming every problem at once, so a misconfigured diagnostic run
    /// fails loudly before it renders anything.
    /// </summary>
    public static PointShadowSeamEvidenceOptions? Read(Func<string, string?> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        string? directory = variable(DirectoryVariable);
        if (string.IsNullOrEmpty(directory)) return null;

        var problems = new List<string>();
        if (!Path.IsPathFullyQualified(directory))
            problems.Add($"{DirectoryVariable} must be an absolute path, and it is '{directory}'");
        else if (HasRelativeSegment(directory))
            problems.Add($"{DirectoryVariable} must not contain '.' or '..' segments, and it is '{directory}'");

        string? commit = variable(CommitVariable), tree = variable(TreeVariable);
        if (!IsObjectId(commit))
            problems.Add($"{CommitVariable} must be the full 40-character lower-case commit the binaries were built "
                + $"from, and it is {Shown(commit)}");
        if (!IsObjectId(tree))
            problems.Add($"{TreeVariable} must be the full 40-character lower-case tree of that commit, and it is "
                + Shown(tree));

        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"The point-shadow seam evidence is switched on by {DirectoryVariable} but its configuration cannot "
                + $"be trusted. {string.Join(". ", problems)}. Unset {DirectoryVariable} for an ordinary run.");
        return new PointShadowSeamEvidenceOptions(Path.GetFullPath(directory), commit!, tree!);
    }

    /// <summary>The configured identity checked against the assemblies that actually ran. Throws when any of
    /// them was built from a different revision.</summary>
    public SeamProvenance Provenance(IReadOnlyList<SeamAssembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var disagreeing = new List<string>();
        bool anyRevision = false;
        foreach (SeamAssembly assembly in assemblies)
        {
            if (assembly.SourceRevision is not string revision) continue;
            anyRevision = true;
            if (!SourceCommit.StartsWith(revision, StringComparison.Ordinal))
                disagreeing.Add($"{assembly.Name} was built from {revision}");
        }
        if (disagreeing.Count > 0)
            throw new InvalidOperationException(
                $"{CommitVariable} names {SourceCommit}, but {string.Join(", ", disagreeing)}. The evidence would "
                + "describe a different source from the one that rendered it.");
        return new SeamProvenance(SourceCommit, SourceTree, anyRevision ? "agree" : "absent", assemblies);
    }

    /// <summary>What one loaded assembly says about itself. The file hash is of the binary that was loaded.</summary>
    public static SeamAssembly Describe(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string location = assembly.Location;
        string? sha = string.IsNullOrEmpty(location)
            ? null
            : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(location)));
        return new SeamAssembly(assembly.GetName().Name ?? "", informational, SourceRevision(informational),
            assembly.ManifestModule.ModuleVersionId, sha);
    }

    /// <summary>The source revision an informational version carries as its build metadata, lower-cased, or null
    /// when the metadata is absent or is not an abbreviated or full hexadecimal object id.</summary>
    public static string? SourceRevision(string? informationalVersion)
    {
        if (informationalVersion is null) return null;
        int plus = informationalVersion.IndexOf('+');
        if (plus < 0) return null;
        string metadata = informationalVersion[(plus + 1)..].ToLowerInvariant();
        return metadata.Length is >= 7 and <= 40 && IsLowerHex(metadata) ? metadata : null;
    }

    static bool IsObjectId(string? value) => value is { Length: 40 } && IsLowerHex(value);

    static bool IsLowerHex(string value)
    {
        foreach (char c in value)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    static bool HasRelativeSegment(string path)
    {
        foreach (string segment in path.Split('/', '\\'))
            if (segment is "." or "..") return true;
        return false;
    }

    static string Shown(string? value) => value is null ? "unset" : $"'{value}'";
}
