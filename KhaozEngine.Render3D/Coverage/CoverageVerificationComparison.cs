using System;
using System.Text;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Render3D;

/// <summary>One completed construction comparison, independent of exact geometry certification.</summary>
internal readonly record struct CoverageVerificationComparison(CoverageVerificationPose Pose, bool Refused,
    bool ReferenceAccepted, CoverageFailureCode ReferenceFailure, int Vertex, string Field, uint Expected, uint Actual)
{
    static readonly string[] Fields = ["Position.X", "Position.Y", "Position.Z", "Normal.X", "Normal.Y", "Normal.Z",
        "Color.X", "Color.Y", "Color.Z", "Color.W", "Uv.X", "Uv.Y", "Tangent.X", "Tangent.Y", "Tangent.Z", "Tangent.W"];

    internal static CoverageVerificationComparison Compare(CoverageVerificationPose pose, bool refused,
        bool referenceAccepted, CoverageFailureCode referenceFailure, ReadOnlySpan<uint> expected, ReadOnlySpan<uint> actual)
    {
        var result = new CoverageVerificationComparison(pose, refused, referenceAccepted, referenceFailure, -1, "", 0, 0);
        for (int word = 0; word < Math.Min(expected.Length, actual.Length); word++)
            if (expected[word] != actual[word])
            {
                result = result with { Vertex = word / 16, Field = Fields[word % 16], Expected = expected[word], Actual = actual[word] };
                break;
            }
        if (result.Vertex < 0 && expected.Length != actual.Length)
            result = result with { Vertex = 0, Field = "WordCount", Expected = (uint)expected.Length, Actual = (uint)actual.Length };
        if (result.Vertex >= 0) result.LogMismatch();
        return result;
    }

    internal static CoverageVerificationComparison Failure(CoverageVerificationPose pose, bool refused,
        bool referenceAccepted, CoverageFailureCode referenceFailure, string field, uint expected, uint actual)
    {
        var result = new CoverageVerificationComparison(pose, refused, referenceAccepted, referenceFailure, 0, field, expected, actual);
        result.LogMismatch();
        return result;
    }

    void LogMismatch()
    {
        var p = Pose;
        var message = new StringBuilder($"Coverage verification mismatch frame={p.FrameId} frameIndex={p.FrameIndex} view={p.ViewId} actor={p.Actor.Value} preparation={p.Preparation} host={p.Topology.HostIdentity} source={p.Topology.Source.SourceSha256} refused={Refused} referenceAccepted={ReferenceAccepted} referenceFailure={ReferenceFailure} vertex={Vertex} field={Field} expected=0x{Expected:x8} actual=0x{Actual:x8} commands={p.CommandCount}");
        message.Append($" reservation={p.Reservation}");
        for (int c = 0; c < p.CommandCount; c++) message.Append($" support[{c}]={p.Commands[c].Support.Identity}");
        Words("poseWords", p.PoseWords); Words("uploadWords", p.UploadWords);
        Log.For<CoverageVerificationReadbacks>().Warn(message.ToString());
        void Words(string label, uint[] words)
        {
            message.Append(' ').Append(label).Append('=');
            for (int i = 0; i < words.Length; i++)
            {
                if (i > 0) message.Append(',');
                message.Append(words[i].ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}
