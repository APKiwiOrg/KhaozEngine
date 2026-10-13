using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class NameplateLegacyDrawCallsTests
{
    const string FixtureName = "nameplate-legacy-draw-calls.txt";

    [Fact]
    public void EveryLegacyCaseDrawsTheRecordedCalls()
    {
        var output = new StringBuilder();
        NameplateStyle fightStyle = NameplateStyle.Default with
        {
            PanelFill = NameplateStyle.Default.PanelFill.WithAlpha(0f),
            PanelBorderThickness = 0f,
            PadY = 0f,
            TitleShadow = Color.Black,
            EdgeBehavior = NameplateEdgeBehavior.Deflect,
            MinBarWidth = 48f,
        };

        RecordCase(output, "1-default-goblin-half-bar", NameplateStyle.Default, "Goblin", [Bar(0.5f)]);
        RecordCase(output, "2-text-only-goblin", NameplateStyle.TextOnly, "Goblin");
        RecordCase(output, "3-default-ox-no-bars", NameplateStyle.Default, "Ox");
        RecordCase(output, "4-fight-zombie-quarter-bar", fightStyle, "Zombie", [Bar(0.25f)]);
        RecordCase(output, "5-empty-title-two-bars", NameplateStyle.Default, "", [Bar(1f), Bar(0.4f)]);
        RecordCase(output, "6-long-title-capped-at-60", NameplateStyle.Default with { MaxWidth = 60f },
            "Abcdefghijklmnop", [Bar(0.5f)]);
        RecordCase(output, "7-width-cap-wins-over-floor",
            NameplateStyle.Default with { MinBarWidth = 120f, MaxWidth = 80f },
            "Abcdefghijklmnop", [Bar(0.5f)]);
        RecordCase(output, "8-fight-deflect-carried-state", fightStyle, "Zombie", [Bar(0.25f)],
            anchor: new Vector2(500f, 5f), drawCount: 2);
        RecordCase(output, "9-clamp-goblin-at-left-edge",
            NameplateStyle.Default with { EdgeBehavior = NameplateEdgeBehavior.Clamp }, "Goblin",
            anchor: new Vector2(3f, 500f));

        string recorded = output.ToString();
        if (Environment.GetEnvironmentVariable("KE_UPDATE_NAMEPLATE_FIXTURE") == "1")
        {
            File.WriteAllText(SourceFixturePath(), recorded);
            return;
        }

        string fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureName));
        AssertRecordedCallsEqual(fixture, recorded);
    }

    static NameplateBar Bar(float fraction) =>
        new(fraction, new Color(0.2f, 0.8f, 0.3f), new Color(0.15f, 0.15f, 0.15f, 0.8f));

    static void RecordCase(StringBuilder output, string name, NameplateStyle style, string title,
        NameplateBar[]? bars = null, Vector2? anchor = null, int drawCount = 1)
    {
        var canvas = new RecordingNameplateCanvas(new FixedGlyphFont());
        var plate = new Nameplate { Title = title, TitleColor = Color.White, Bars = bars };
        NameplatePlacementState placementState = default;
        for (int draw = 0; draw < drawCount; draw++)
            Assert.True(NameplateRenderer.DrawAt(ref canvas, anchor ?? new Vector2(500f, 500f), plate, style,
                1000, 1000, ref placementState));

        output.AppendLine($"case {name}");
        foreach (string call in canvas.Calls)
            output.AppendLine(call);
    }

    static string SourceFixturePath([CallerFilePath] string sourcePath = "") =>
        Path.Combine(Path.GetDirectoryName(sourcePath)!, "Fixtures", FixtureName);

    static void AssertRecordedCallsEqual(string expected, string actual)
    {
        string[] expectedLines = expected.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        string[] actualLines = actual.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        Assert.Equal(expectedLines.Length, actualLines.Length);

        for (int line = 0; line < expectedLines.Length; line++)
        {
            const string tokenPattern = "\"(?:\\\\.|[^\"\\\\])*\"|\\S+";
            MatchCollection expectedTokens = Regex.Matches(expectedLines[line], tokenPattern);
            MatchCollection actualTokens = Regex.Matches(actualLines[line], tokenPattern);
            Assert.Equal(expectedTokens.Count, actualTokens.Count);

            for (int token = 0; token < expectedTokens.Count; token++)
            {
                string expectedToken = expectedTokens[token].Value;
                string actualToken = actualTokens[token].Value;
                if (decimal.TryParse(expectedToken, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal expectedNumber))
                {
                    Assert.True(decimal.TryParse(actualToken, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal actualNumber));
                    Assert.True(Math.Abs(expectedNumber - actualNumber) <= 0.001m,
                        $"Line {line + 1}, token {token + 1}: expected {expectedToken}, actual {actualToken} (tolerance 0.001).");
                }
                else
                    Assert.Equal(expectedToken, actualToken);
            }
        }
    }
}
