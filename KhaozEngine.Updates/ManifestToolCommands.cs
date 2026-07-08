using System.IO;

namespace KhaozEngine.Updates;

/// <summary>
/// The reusable command logic behind the <c>ke-updater</c> dotnet tool. It lives in the Updates library
/// (not the tool exe) so it is unit-testable and the tool's <c>Program.cs</c> stays a one-liner. Thin
/// wrapper over the existing engine APIs: <see cref="UpdateManifest.GenerateFromDirectory"/>,
/// <see cref="ManifestSigner"/>, <see cref="ManifestVerifier"/>.
/// </summary>
public static class ManifestToolCommands
{
    const string Usage =
        "Usage: ke-updater <command>\n" +
        "  manifest --dir <path> --platform <id> --version <v> [--required] [--output <path>]\n" +
        "  genkey --out <dir>\n" +
        "  sign --manifest <manifest.json> --key <private.pem>\n" +
        "  verify --manifest <manifest.json> --sig <manifest.json.sig> --key <public.pem>";

    /// <summary>Dispatches on <c>args[0]</c>. Returns a process exit code (0 = success).</summary>
    public static int Run(string[] args, TextWriter outw, TextWriter errw)
    {
        if (args.Length == 0) { errw.WriteLine(Usage); return 1; }
        return args[0] switch
        {
            "manifest" => Manifest(args, outw, errw),
            "genkey" => GenKey(args, errw),
            "sign" => Sign(args, errw),
            "verify" => Verify(args, errw),
            _ => Fail(errw, $"Unknown command '{args[0]}'.\n{Usage}"),
        };
    }

    static int Manifest(string[] args, TextWriter outw, TextWriter errw)
    {
        string? dir = Opt(args, "--dir"), platform = Opt(args, "--platform"),
                version = Opt(args, "--version"), output = Opt(args, "--output");
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(version))
            return Fail(errw, "manifest: --dir, --platform and --version are required.");
        if (!Directory.Exists(dir)) return Fail(errw, $"Directory not found: {dir}");

        UpdateManifest manifest = UpdateManifest.GenerateFromDirectory(Path.GetFullPath(dir), version, platform);
        // Required is set here, not in GenerateFromDirectory: that method also builds the client's local and
        // staging manifests, where "required" is meaningless. Only a published build's manifest carries it.
        if (Flag(args, "--required")) manifest.Required = true;
        string json = manifest.Serialize();
        if (!string.IsNullOrWhiteSpace(output))
        {
            string? outDir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            File.WriteAllText(output, json);
            errw.WriteLine($"Manifest written to {output} ({manifest.Files.Count} files)");
        }
        else outw.Write(json);
        return 0;
    }

    static int GenKey(string[] args, TextWriter errw)
    {
        string? outDir = Opt(args, "--out");
        if (string.IsNullOrWhiteSpace(outDir)) return Fail(errw, "genkey: --out <dir> is required.");
        Directory.CreateDirectory(outDir);
        ManifestKeyPair pair = ManifestSigner.GenerateKeyPair();
        string priv = Path.Combine(outDir, "private.pem");
        string pub = Path.Combine(outDir, "public.pem");
        File.WriteAllText(priv, pair.PrivateKeyPem);
        File.WriteAllText(pub, pair.PublicKeyPem);
        errw.WriteLine($"Wrote {priv} and {pub}. Keep private.pem secret; embed public.pem in TrustedPublicKeys.");
        return 0;
    }

    static int Sign(string[] args, TextWriter errw)
    {
        string? manifestPath = Opt(args, "--manifest"), keyPath = Opt(args, "--key");
        if (string.IsNullOrWhiteSpace(manifestPath) || string.IsNullOrWhiteSpace(keyPath))
            return Fail(errw, "sign: --manifest and --key are required.");
        if (!File.Exists(manifestPath)) return Fail(errw, $"Manifest not found: {manifestPath}");
        if (!File.Exists(keyPath)) return Fail(errw, $"Key not found: {keyPath}");
        byte[] data = File.ReadAllBytes(manifestPath);
        byte[] sig = ManifestSigner.Sign(data, File.ReadAllText(keyPath));
        string sigPath = manifestPath + ".sig";
        File.WriteAllBytes(sigPath, sig);
        errw.WriteLine($"Wrote {sigPath} ({sig.Length} bytes).");
        return 0;
    }

    static int Verify(string[] args, TextWriter errw)
    {
        string? manifestPath = Opt(args, "--manifest"), sigPath = Opt(args, "--sig"), keyPath = Opt(args, "--key");
        if (string.IsNullOrWhiteSpace(manifestPath) || string.IsNullOrWhiteSpace(sigPath) || string.IsNullOrWhiteSpace(keyPath))
            return Fail(errw, "verify: --manifest, --sig and --key are required.");
        if (!File.Exists(manifestPath) || !File.Exists(sigPath) || !File.Exists(keyPath))
            return Fail(errw, "verify: one or more input files not found.");
        bool ok = ManifestVerifier.Verify(
            File.ReadAllBytes(manifestPath), File.ReadAllBytes(sigPath), new[] { File.ReadAllText(keyPath) });
        errw.WriteLine(ok ? "Signature OK." : "Signature INVALID.");
        return ok ? 0 : 2;
    }

    static string? Opt(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
        return null;
    }

    static bool Flag(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++) if (args[i] == name) return true;
        return false;
    }

    static int Fail(TextWriter errw, string message) { errw.WriteLine(message); return 1; }
}
