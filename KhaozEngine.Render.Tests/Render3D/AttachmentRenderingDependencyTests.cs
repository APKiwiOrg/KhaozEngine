using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Attachments.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>SharpGLTF stays inside <see cref="KhaozEngine.Render3D.GltfLoader"/>. The project graph check sees
/// PackageReferences only, so a SharpGLTF type named in attachment code would arrive transitively and pass it.
/// This walks the compiled assembly's references instead, as the native GPU backend Veldrid check does.</summary>
public sealed class AttachmentRenderingDependencyTests
{
    [Fact]
    public void Attachment_rendering_references_no_SharpGLTF_assembly()
    {
        Assembly arm = typeof(AttachmentSetCache).Assembly;
        Assert.Equal("KhaozEngine.Attachments.Render3D", arm.GetName().Name);

        string[] gltf = arm.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(n => n.StartsWith("SharpGLTF", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(gltf.Length == 0,
            "KhaozEngine.Attachments.Render3D names a type from a SharpGLTF assembly: [" + string.Join(", ", gltf)
            + "]. GltfLoader's byte overloads translate SharpGLTF failures to InvalidDataException so callers "
            + "never name the importer.");
    }
}
