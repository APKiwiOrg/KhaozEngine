using System;
using System.IO;
using System.Linq;
using KhaozEngine.Tests.Gpu.RendererPerf;

// Admission runs only trusted managed code. The requested checkout supplies manifest bytes and Git facts.
internal static class RendererPerfManifestAdmission
{
    internal static void Admit(string root, string directory)
    {
        string unit = Env("RENDERER_PERF_UNIT");
        Require(unit.Length is >= 1 and <= 64 && unit[0] != '-'
            && unit.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'), "Unsafe renderer unit token.");
        var checkout = new RendererPerfCheckout(root);
        string path = $"tools/Renderer.Perf.Tests/authorized/{unit}.json";
        byte[] bytes = File.ReadAllBytes(checkout.ManifestPath(unit));
        var supplied = RendererPerfManifest.ReadJson<RendererPerfAuthorization>(
            File.ReadAllBytes(Path.Combine(directory, "authorization.json")), 64 * 1024);
        string head = checkout.Git("rev-parse", "HEAD");
        string[] parents = checkout.Git("rev-list", "--parents", "-n", "1", "HEAD").Split(' ');
        string workload = Env("RENDERER_PERF_WORKLOAD_SHA");
        Require(head == Env("TESTED_REF") && parents.Length == 2 && parents[0] == head
            && parents[1] == workload && head != workload
            && checkout.Git("diff", "--name-status", "--no-renames", "--ignore-submodules=none", workload, "HEAD") == "A\t" + path,
            "Authorization must add only the manifest to its immutable workload parent.");
        Require(Env("WORKFLOW_RUN_ATTEMPT") == "1", "Authorization requires attempt 1.");
        // Runner is an independent selector fact. Parse binds it to the preset's fixed hosted policy.
        var authorization = new RendererPerfAuthorization(Env("RENDERER_PERF_MANIFEST_SHA256"), workload,
            head, parents[1], parents.Length - 1, [path], 1, Env("LEGS"), supplied.Runner, "gpu-hosted-bench.yml");
        Require(!supplied.ChangedPaths.IsDefault && supplied.ChangedPaths.SequenceEqual(authorization.ChangedPaths)
            && supplied with { ChangedPaths = authorization.ChangedPaths } == authorization,
            "Authorization facts disagree with Git or dispatch.");
        var manifest = RendererPerfManifest.Parse(bytes, authorization);
        Require(manifest.UnitId == unit, "Manifest names a different unit.");
    }

    static string Env(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(name + " must be explicit.");
    static void Require(bool condition, string message) => RendererPerfManifest.Require(condition, "Authorization", message);
}
