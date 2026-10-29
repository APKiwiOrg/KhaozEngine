using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfProfileRequest(string Schema, string OutputDirectory, ImmutableArray<RendererPerfBaselineLeg> Legs);

internal static class RendererPerfProfilePublisher
{
    internal static ImmutableArray<string> PublishFile(string requestPath)
        => Publish(RendererPerfBaselinePublisher.ReadRequest<RendererPerfProfileRequest>(requestPath, (value, parent) => value with
        {
            OutputDirectory = Path.GetFullPath(value.OutputDirectory, parent),
            Legs = [.. value.Legs.Select(l => l with { Directory = Path.GetFullPath(l.Directory, parent) })]
        }));

    internal static ImmutableArray<string> Publish(RendererPerfProfileRequest request)
    {
        Require(request.Schema == "khaozengine.renderer-profile-request/1" && !string.IsNullOrWhiteSpace(request.OutputDirectory),
            "Request", "Expected renderer-profile-request/1 and an explicit output directory.");
        var verified = RendererPerfBaselinePublisher.VerifyLegs(request.Legs, "water-optics-v2");
        string pair = verified[0].Summary.Raw.Identity.UnitId + "-" + verified[1].Summary.Raw.Identity.UnitId;
        var files = verified.Select(v => (Name: v.Input.Leg + "-" + v.Summary.Raw.Identity.UnitId + ".md",
            Text: RendererPerfBaselineMarkdown.Summary(v.Input, v.Summary))).ToList();
        files.Add(($"profiles-{pair}.md", RendererPerfProfileMarkdown.Profiles(verified)));
        files.Add(($"index-{pair}.md", RendererPerfProfileMarkdown.Index(verified)));
        Require(files.All(f => !Path.Exists(Path.Combine(request.OutputDirectory, f.Name))), "Exists", "An immutable summary, profile delta or index already exists.");
        try
        {
            Directory.CreateDirectory(request.OutputDirectory);
            FileStream Open(int fileIndex) => new(Path.Combine(request.OutputDirectory, files[fileIndex].Name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var linux = Open(0);
            using var windows = Open(1);
            using var profiles = Open(2);
            using var index = Open(3);
            var encoding = new UTF8Encoding(false, true);
            linux.Write(encoding.GetBytes(files[0].Text));
            windows.Write(encoding.GetBytes(files[1].Text));
            profiles.Write(encoding.GetBytes(files[2].Text));
            index.Write(encoding.GetBytes(files[3].Text));
        }
        catch (Exception error) { throw new RendererPerfBaselineException("Write", "Exclusive profile publication failed. Preserve any partial output.", error); }
        return [.. files.Select(f => f.Name)];
    }

    static void Require(bool condition, string code, string reason)
    { if (!condition) throw new RendererPerfBaselineException(code, reason); }
}
