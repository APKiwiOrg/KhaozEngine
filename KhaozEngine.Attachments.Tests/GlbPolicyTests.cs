using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class GlbPolicyTests
{
    const string Embedded = """{"asset":{"version":"2.0"},"buffers":[{"byteLength":4}],"bufferViews":[{"buffer":0,"byteLength":4}],"images":[{"bufferView":0,"mimeType":"image/png"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}]}""";

    [Fact]
    public void ValidEmbeddedRigidContainer_IsAccepted() => AttachmentGlb.ValidateRigidEmbedded(Container(Embedded), "rigid.glb");

    [Theory]
    [InlineData("buffer-uri")]
    [InlineData("image-uri")]
    [InlineData("data-uri")]
    [InlineData("skins")]
    [InlineData("node-skin")]
    [InlineData("joints-attribute")]
    [InlineData("weights-attribute")]
    [InlineData("morph-target")]
    [InlineData("animation")]
    [InlineData("missing-binary")]
    [InlineData("short-binary")]
    [InlineData("invalid-json")]
    [InlineData("duplicate-json")]
    public void Policy_RejectsExternalOrNonRigidData(string name)
    {
        string json = name switch
        {
            "buffer-uri" => Embedded.Replace("\"byteLength\":4", "\"uri\":\"other.bin\",\"byteLength\":4", StringComparison.Ordinal),
            "image-uri" => Embedded.Replace("\"mimeType\":", "\"uri\":\"image.png\",\"mimeType\":", StringComparison.Ordinal),
            "data-uri" => Embedded.Replace("\"mimeType\":", "\"uri\":\"data:image/png;base64,AAAA\",\"mimeType\":", StringComparison.Ordinal),
            "skins" => Embedded.Replace("\"meshes\":", "\"skins\":[{\"joints\":[0]}],\"meshes\":", StringComparison.Ordinal),
            "node-skin" => Embedded.Replace("\"meshes\":", "\"nodes\":[{\"skin\":0}],\"meshes\":", StringComparison.Ordinal),
            "joints-attribute" => Embedded.Replace("\"POSITION\":0", "\"POSITION\":0,\"JOINTS_0\":1", StringComparison.Ordinal),
            "weights-attribute" => Embedded.Replace("\"POSITION\":0", "\"POSITION\":0,\"WEIGHTS_0\":1", StringComparison.Ordinal),
            "morph-target" => Embedded.Replace("\"attributes\":", "\"targets\":[{}],\"attributes\":", StringComparison.Ordinal),
            "animation" => Embedded.Replace("\"meshes\":", "\"animations\":[{}],\"meshes\":", StringComparison.Ordinal),
            "short-binary" => Embedded.Replace("\"byteLength\":4", "\"byteLength\":8", StringComparison.Ordinal),
            "invalid-json" => "{",
            "duplicate-json" => Embedded.Replace("\"version\":\"2.0\"", "\"version\":\"2.0\",\"version\":\"2.0\"", StringComparison.Ordinal),
            _ => Embedded,
        };
        byte[] glb = Container(json, binary: name != "missing-binary");
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => AttachmentGlb.ValidateRigidEmbedded(glb, name + ".glb"));
        Assert.Contains(name + ".glb", error.Message);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("total-length")]
    [InlineData("truncated-chunk")]
    [InlineData("overflow-chunk")]
    [InlineData("unaligned-chunk")]
    [InlineData("json-not-first")]
    [InlineData("trailing-header")]
    [InlineData("overflow-binary")]
    public void Envelope_RejectsMalformedLengthsBeforeSlicing(string name)
    {
        byte[] glb = Container(Embedded);
        switch (name)
        {
            case "header": glb = new byte[11]; break;
            case "magic": glb[0] = 0; break;
            case "version": BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 1); break;
            case "total-length": BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), uint.MaxValue); break;
            case "truncated-chunk": Array.Resize(ref glb, glb.Length - 1); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length); break;
            case "overflow-chunk": BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), uint.MaxValue - 3); break;
            case "unaligned-chunk": BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), 3); break;
            case "json-not-first": BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x004e4942); break;
            case "trailing-header": Array.Resize(ref glb, glb.Length + 4); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length); break;
            case "overflow-binary": BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(glb.Length - 12), uint.MaxValue - 3); break;
        }
        Assert.Throws<InvalidDataException>(() => AttachmentGlb.ValidateRigidEmbedded(glb, name + ".glb"));
    }

    static byte[] Container(string json, bool binary = true)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        int padded = (utf8.Length + 3) & ~3;
        byte[] glb = new byte[20 + padded + (binary ? 12 : 0)];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546c67);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)padded);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4e4f534a);
        utf8.CopyTo(glb, 20);
        glb.AsSpan(20 + utf8.Length, padded - utf8.Length).Fill(0x20);
        if (binary)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(20 + padded), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(24 + padded), 0x004e4942);
        }
        return glb;
    }
}
