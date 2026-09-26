using System;
using System.Numerics;
using KhaozEngine.Dungeon;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public class DungeonGenerationDialogTests
{
    [Fact]
    public void FocusedEditsPreserveAdvancedPresetWithoutMutatingCaller()
    {
        var preset = new DungeonConfig
        {
            PlotWidthTiles = 20,
            PlotDepthTiles = 30,
            LoopEdgeBudget = 7,
        };
        var dialog = new DungeonGenerationDialog(preset, new Vector3(10f, 3f, 20f));
        dialog.SeedText = ulong.MaxValue.ToString();
        dialog.Draft.RoomCountTarget = 9;
        dialog.YawDegrees = 90f;

        Assert.True(dialog.TryBuild(out DungeonConfig config, out ulong seed, out DungeonPlotTransform plot));
        Assert.Equal(ulong.MaxValue, seed);
        Assert.Equal(9, config.RoomCountTarget);
        Assert.Equal(7, config.LoopEdgeBudget);
        Assert.Equal(12, preset.RoomCountTarget);
        Assert.Equal(-10f, plot.OriginX, 3);
        Assert.Equal(-10f, plot.OriginZ, 3);
        Assert.Equal(3f, plot.BaseY, 3);
        Assert.Equal(MathF.PI * 0.5f, plot.YawRadians, 5);
        Assert.Equal("", dialog.Error);
    }

    [Fact]
    public void OverflowingSeedStaysInDialogWithFieldError()
    {
        var dialog = new DungeonGenerationDialog(new DungeonConfig(), Vector3.Zero)
        {
            SeedText = "18446744073709551616",
        };

        Assert.False(dialog.TryBuild(out _, out _, out _));
        Assert.Contains("Seed", dialog.Error);
        Assert.False(dialog.CloseRequested);
    }

    [Fact]
    public void InvalidCorridorRangeStaysInDialogWithFieldError()
    {
        var preset = new DungeonConfig();
        var dialog = new DungeonGenerationDialog(preset, Vector3.Zero);
        dialog.Draft.CorridorMinWidth = 5;
        dialog.Draft.CorridorMaxWidth = 2;

        Assert.False(dialog.TryBuild(out _, out _, out _));
        Assert.Contains("corridor", dialog.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, preset.CorridorMinWidth);
    }

    [Fact]
    public void CancelRequestsCloseWithoutChangingThePreset()
    {
        var preset = new DungeonConfig { RoomCountTarget = 6 };
        var dialog = new DungeonGenerationDialog(preset, Vector3.Zero);
        dialog.Draft.RoomCountTarget = 10;

        dialog.CancelButton.OnClick!.Invoke();

        Assert.True(dialog.CloseRequested);
        Assert.Equal(6, preset.RoomCountTarget);
    }
}
