using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public partial class JsonTests
{
    [Theory]
    [InlineData("chains", AttachmentSetRefusal.ChainsEmpty, ".chains", null, null)]
    [InlineData("joints", AttachmentSetRefusal.ChainJointsEmpty, ".chains[0].joints", 0, null)]
    [InlineData("links", AttachmentSetRefusal.TooManyLinks, ".links", null, null)]
    [InlineData("linkStretch", AttachmentSetRefusal.LinkStretchOutOfRange, ".linkStretch", null, "linkStretch")]
    [InlineData("stiffness", AttachmentSetRefusal.ChainSettingOutOfRange, ".chains[0].stiffness", 0, "stiffness")]
    public void Invalid_fallback_json_reports_the_nested_path_and_preserves_the_chain_path(string fault,
        AttachmentSetRefusal reason, string suffix, int? chainIndex, string? setting)
    {
        Check(cloth: false);
        Check(cloth: true);

        void Check(bool cloth)
        {
            ChainMotionDefinition chains = AttachmentFixtures.Motion();
            AttachmentSetDefinition set = MotionWithFallback(chains, cloth);
            var json = JsonNode.Parse(AttachmentSetJson.Write(set))!.AsObject();
            if (!cloth) json["formatVersion"] = 6;
            JsonNode motion = json["parts"]![0]!["motion"]!;
            if (cloth) motion = motion["fallback"]!;
            switch (fault)
            {
                case "chains": motion["chains"] = new JsonArray(); break;
                case "joints": motion["chains"]![0]!["joints"] = new JsonArray(); break;
                case "links":
                    motion["links"] = new JsonArray(Enumerable.Range(0, 65).Select(_ => (JsonNode)new JsonArray(0, 1)).ToArray());
                    break;
                case "linkStretch": motion["linkStretch"] = 1.000000001; break;
                case "stiffness": motion["chains"]![0]!["stiffness"] = -1; break;
                default: throw new ArgumentOutOfRangeException(nameof(fault));
            }
            var error = Assert.Throws<AttachmentSetDataException>(() =>
                AttachmentSetJson.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()), "motion.json"));
            Assert.Equal(reason, error.Reason);
            Assert.Equal("motion.json", error.SourceName);
            Assert.Equal(set.Parts[0].Id, error.PartId);
            Assert.Equal(chainIndex, error.ChainIndex);
            Assert.Equal(setting, error.SettingName);
            Assert.Equal(ChainPath(cloth, suffix), error.JsonPath);
        }
    }

    [Theory]
    [InlineData("joint name", AttachmentSetRefusal.ChainJointNameEmpty, ".chains[0].joints[0]", 0, 0, null)]
    [InlineData("link index", AttachmentSetRefusal.ChainLinkInvalid, ".links[0]", null, null, 0)]
    [InlineData("stretch without links", AttachmentSetRefusal.LinkStretchWithoutLinks, ".linkStretch", null, null, null)]
    public void Invalid_constructed_fallback_reports_the_nested_path_and_preserves_the_chain_path(string fault,
        AttachmentSetRefusal reason, string suffix, int? chainIndex, int? jointIndex, int? linkIndex)
    {
        ChainMotionDefinition chains = AttachmentFixtures.Motion();
        chains = fault switch
        {
            "joint name" => chains with { Chains = [chains.Chains[0] with { Joints = [""] }] },
            "link index" => chains with { Links = [new(0, 0)] },
            "stretch without links" => chains with { LinkStretch = .25f, Links = [] },
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
        Check(cloth: false);
        Check(cloth: true);

        void Check(bool cloth)
        {
            var error = Assert.Throws<AttachmentSetArgumentException>(() => MotionWithFallback(chains, cloth));
            Assert.Equal(reason, error.Reason);
            Assert.Equal(AttachmentFixtures.TaggedSkinned().Id, error.PartId);
            Assert.Equal(chainIndex, error.ChainIndex);
            Assert.Equal(jointIndex, error.JointIndex);
            Assert.Equal(linkIndex, error.LinkIndex);
            Assert.Equal(ChainPath(cloth, suffix), error.JsonPath);
        }
    }

    static AttachmentSetDefinition MotionWithFallback(ChainMotionDefinition chains, bool cloth)
    {
        PartMotionDefinition motion = cloth ? new ClothMotionDefinition(new("coat.kecs", new string('a', 64)), chains) : chains;
        return new AttachmentSetDefinition([AttachmentFixtures.TaggedSkinned() with { Motion = motion }], AttachmentLayer.Garment, []);
    }

    static string ChainPath(bool cloth, string suffix) => (cloth ? "$.parts[0].motion.fallback" : "$.parts[0].motion") + suffix;
}
