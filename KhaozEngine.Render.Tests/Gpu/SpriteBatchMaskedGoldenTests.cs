using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using KhaozEngine.Tests.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class SpriteBatchMaskedGoldenTests
{
    const int Width = 32, Height = 16;
    static readonly Vector4 FullUv = new(0, 0, 1, 1);

    [GpuFact]
    public void UnmaskedBeforeAndAfterMaskedUseIsByteIdentical()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var factory = new SpriteBatchMaskedTestRig.ForwardingFactory(gpu.GpuDevice.Factory, recordCommands: true);
        using var device = new SpriteBatchMaskedTestRig.RecordingDevice(gpu.GpuDevice, factory);
        using var core = new Render2DCore(device, SpriteBatchMaskedTestRig.Output, ownsDevice: false);
        using var source = core.CreateTexture(SourceAtlasPixels(), 8, 4);
        using var mask = core.CreateTexture([0, 255, 0, 255], 1, 1);
        using var font = SpriteFont.Build(device, DefaultFont.Bytes, 12);
        var batch = core.Batch;
        Assert.Null(SpriteBatchMaskedTestRig.MaskedOwnerFor(batch));
        byte[] beforeMaskedUse = CaptureUnmaskedParityScene(device, batch, source, font);
        var referenceDraws = factory.CommandLists.SelectMany(commands => commands.Draws).ToArray();
        Assert.Equal(3, referenceDraws.Length);
        Assert.Null(SpriteBatchMaskedTestRig.MaskedOwnerFor(batch));

        // Warm the same batch on a separate target with compatible outputs, between ordinary frames.
        CaptureParityFrame(device, batch, current =>
        {
            current.Begin(SamplerMode.Point);
            current.DrawMasked(source, new Vector4(0, 0, 64, 64), mask, Color.White);
            current.End();
        });
        Assert.NotNull(SpriteBatchMaskedTestRig.MaskedOwnerFor(batch));
        int commandListsBeforeFinalFrame = factory.CommandLists.Count;
        byte[] afterMaskedUse = CaptureUnmaskedParityScene(device, batch, source, font);
        Assert.Equal(beforeMaskedUse, afterMaskedUse);
        Assert.Equal(64 * 64 * 4, afterMaskedUse.Length);

        var finalDraws = factory.CommandLists.Skip(commandListsBeforeFinalFrame)
            .SelectMany(commands => commands.Draws).ToArray();
        Assert.Equal(referenceDraws.Length, finalDraws.Length);
        for (int i = 0; i < referenceDraws.Length; i++)
        {
            // Pipelines and the VP set belong to the batch, so these also pin reuse of the original batch.
            Assert.Same(referenceDraws[i].Pipeline, finalDraws[i].Pipeline);
            Assert.Same(referenceDraws[i].Set0, finalDraws[i].Set0);
            Assert.Same(referenceDraws[i].Set1, finalDraws[i].Set1);
            Assert.Equal(referenceDraws[i].DynamicOffset, finalDraws[i].DynamicOffset);
        }
    }

    [GpuFact]
    public void OpaqueMaskMatchesOrdinaryDrawExactly()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var device = gpu.GpuDevice;
        using var core = new Render2DCore(device, SpriteBatchMaskedTestRig.Output, ownsDevice: false);
        using var source = core.CreateTexture(SourceAtlasPixels(), 8, 4);
        using var mask = core.CreateTexture([0, 255, 0, 255], 1, 1);
        byte[] Capture(bool masked, BlendMode blend) => CaptureParityFrame(device, core.Batch, batch =>
        {
            batch.Begin(SamplerMode.Point);
            batch.BlendMode = blend;
            var tint = new Color(0.75f, 0.5f, 0.25f, 0.625f);
            foreach (var rect in new[] { new Vector4(2, 3, 38, 31), new Vector4(24, 16, 32, 40) })
            {
                if (masked) batch.DrawMasked(source, rect, FullUv, mask, FullUv, tint, SamplerMode.Point);
                else batch.Draw(source, rect, FullUv, tint);
            }
            batch.End();
        });

        byte[] ordinaryAlphaBytes = Capture(false, BlendMode.Alpha);
        byte[] opaqueMaskAlphaBytes = Capture(true, BlendMode.Alpha);
        byte[] ordinaryAdditiveBytes = Capture(false, BlendMode.Additive);
        byte[] opaqueMaskAdditiveBytes = Capture(true, BlendMode.Additive);
        Assert.Equal(ordinaryAlphaBytes, opaqueMaskAlphaBytes);
        Assert.Equal(ordinaryAdditiveBytes, opaqueMaskAdditiveBytes);
        Assert.NotEqual(ordinaryAlphaBytes, ordinaryAdditiveBytes);
    }

    [GpuFact]
    public void SourceAndMaskAtlasRegionsAreIndependent()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
        {
            var source = ctx.CreateTexture(SourceAtlasPixels(), 8, 4);
            var mask = ctx.CreateTexture(MaskAtlasPixels(), 8, 4);
            var shared = ctx.CreateTexture(SharedAtlasPixels(), 8, 4);
            var sourceUv = new Vector4(1f / 8, 1f / 4, 4f / 8, 3f / 4);
            var maskUv = new Vector4(4f / 8, 1f / 4, 7f / 8, 3f / 4);
            ctx.Batch.Begin(SamplerMode.Point);
            for (int variant = 0; variant < 4; variant++)
            {
                Vector4 s = (variant & 1) == 0 ? sourceUv : ReverseRegion(sourceUv);
                Vector4 m = (variant & 2) == 0 ? maskUv : ReverseRegion(maskUv);
                ctx.Batch.DrawMasked(source, new Vector4(variant * 8, 0, 8, 8), s,
                    mask, m, Color.White, SamplerMode.Point);
                ctx.Batch.DrawMasked(shared, new Vector4(variant * 8, 8, 8, 8), s,
                    shared, m, Color.White, SamplerMode.Point);
            }
            ctx.Batch.End();
        });

        // Hand-derived red and alpha at the six selected texel centers, row by row.
        (byte Red, byte Alpha)[][] expected =
        [
            [(0, 0), (96, 64), (64, 255), (128, 255), (0, 0), (80, 64)],
            [(0, 0), (112, 64), (128, 255), (64, 255), (0, 0), (128, 64)],
            [(128, 64), (0, 0), (64, 255), (128, 255), (112, 64), (0, 0)],
            [(80, 64), (0, 0), (128, 255), (64, 255), (96, 64), (0, 0)]
        ];
        for (int variant = 0; variant < 4; variant++)
            for (int row = 0; row < 2; row++)
                for (int column = 0; column < 3; column++)
                {
                    var (red, alpha) = expected[variant][row * 3 + column];
                    int x = variant * 8 + new[] { 1, 4, 6 }[column];
                    AssertRgba(rgba, x, row * 4 + 2, red, 0, 0, alpha);
                    AssertRgba(rgba, x, row * 4 + 10, red, 0, 0, alpha);
                }
    }

    [GpuFact]
    public void NestedScissorAndPipelineSwitchesCompose()
    {
        byte[] rgba = Render2DSnapshot.Capture(8, 8, new Color(0f, 0f, 1f, 1f), ctx =>
        {
            var white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
            var mask = ctx.CreateTexture([0, 255, 0, 255], 1, 1);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.SetScissor(new Rect(1, 1, 6, 6));
            ctx.Batch.SetScissor(new Rect(2, 2, 2, 2));
            ctx.Batch.DrawMasked(white, new Vector4(0, 0, 8, 8), mask, new Color(1f, 0f, 0f, 1f));
            ctx.Batch.ClearScissor();
            ctx.Batch.Draw(white, new Vector4(1, 1, 1, 1), new Color(0f, 1f, 0f, 1f));
            ctx.Batch.ClearScissor();
            ctx.Batch.Draw(white, new Vector4(0, 0, 1, 1), Color.White);
            ctx.Batch.End();
        });

        AssertRgba(rgba, 3, 3, 255, 0, 0, 255, width: 8);
        AssertRgba(rgba, 1, 3, 0, 0, 255, 255, width: 8);
        AssertRgba(rgba, 1, 1, 0, 255, 0, 255, width: 8);
        AssertRgba(rgba, 0, 0, 255, 255, 255, 255, width: 8);
        AssertRgba(rgba, 7, 7, 0, 0, 255, 255, width: 8);
    }

    [GpuFact]
    public void InterleavedMasksAndOrdinarySpritesKeepPainterOrder()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
        {
            var red = ctx.CreateTexture([255, 0, 0, 255], 1, 1);
            var blue = ctx.CreateTexture([0, 0, 255, 255], 1, 1);
            var white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
            var cutout = ctx.CreateTexture([255, 0, 0, 0, 0, 255, 0, 255], 2, 1);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.DrawMasked(red, new Vector4(0, 0, 16, 8), white, Color.White);
            ctx.Batch.DrawMasked(blue, new Vector4(0, 0, 16, 8), FullUv, cutout, FullUv,
                Color.White, SamplerMode.Point);
            ctx.Batch.DrawMasked(red, new Vector4(10, 2, 1, 1), white, Color.White);
            ctx.Batch.DrawMasked(red, new Vector4(16, 0, 16, 8), white, Color.White);
            ctx.Batch.BlendMode = BlendMode.Additive;
            ctx.Batch.DrawMasked(blue, new Vector4(16, 0, 16, 8), white, Color.White);
            ctx.Batch.BlendMode = BlendMode.Alpha;
            ctx.Batch.Draw(white, new Vector4(24, 4, 1, 1), new Color(0f, 1f, 0f, 1f));
            ctx.Batch.End();
        });

        AssertRgba(rgba, 4, 4, 255, 0, 0, 255);
        AssertRgba(rgba, 12, 4, 0, 0, 255, 255);
        AssertRgba(rgba, 10, 2, 255, 0, 0, 255);
        AssertRgba(rgba, 20, 4, 255, 0, 255, 255);
        AssertRgba(rgba, 24, 4, 0, 255, 0, 255);
    }

    [GpuFact]
    public void RenderTargetSourceAndMaskNeedNoCpuCopy()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var factory = new SpriteBatchMaskedTestRig.ForwardingFactory(gpu.GpuDevice.Factory, recordCommands: true);
        using var device = new SpriteBatchMaskedTestRig.RecordingDevice(gpu.GpuDevice, factory);
        using var sourceTarget = SampledTarget(factory, 4, 4);
        using var maskTarget = SampledTarget(factory, 4, 4);
        using var destination = SampledTarget(factory, Width, Height);
        using var sourceFb = factory.CreateFramebuffer(null, sourceTarget);
        using var maskFb = factory.CreateFramebuffer(null, maskTarget);
        using var destinationFb = factory.CreateFramebuffer(null, destination);
        using var core = new Render2DCore(device, destinationFb.Outputs, ownsDevice: false);
        using var white = core.CreateTexture([255, 255, 255, 255], 1, 1);
        using var uploadedSource = core.CreateTexture(TargetControlPixels(source: true), 4, 4);
        using var uploadedMask = core.CreateTexture(TargetControlPixels(source: false), 4, 4);
        using var source = Texture2D.Wrap(sourceTarget, 4, 4, ownsHandle: false);
        using var mask = Texture2D.Wrap(maskTarget, 4, 4, ownsHandle: false);
        using var producer = factory.CreateCommandList();
        using (GpuRecording.Open(device, producer, "masked target producers"))
        {
            producer.SetFramebuffer(sourceFb);
            producer.ClearColorTarget(0, new Color(0f, 0f, 1f, 1f));
            core.Batch.NewFrame(producer, 4, 4);
            core.Batch.Begin(SamplerMode.Point);
            core.Batch.Draw(white, new Vector4(0, 0, 2, 2), new Color(1f, 0f, 0f, 1f));
            core.Batch.End();
            producer.SetFramebuffer(maskFb);
            producer.ClearColorTarget(0, new Color(0f, 0f, 0f, 0.5f));
            core.Batch.Begin(SamplerMode.Point);
            core.Batch.Draw(white, new Vector4(0, 0, 2, 2), Color.White);
            core.Batch.End();
        }
        device.Submit(producer);
        device.WaitForIdle();

        int uploadsBeforeConsumer = device.TextureUploadCalls;
        using var consumer = factory.CreateCommandList();
        using (GpuRecording.Open(device, consumer, "masked target consumer"))
        {
            consumer.SetFramebuffer(destinationFb);
            consumer.ClearColorTarget(0, Color.Transparent);
            core.Batch.NewFrame(consumer, Width, Height);
            core.Batch.Begin(SamplerMode.Point);
            core.Batch.DrawMasked(source, new Vector4(0, 0, 8, 8), uploadedMask, Color.White, SamplerMode.Point);
            core.Batch.DrawMasked(uploadedSource, new Vector4(8, 0, 8, 8), mask, Color.White, SamplerMode.Point);
            core.Batch.DrawMasked(source, new Vector4(16, 0, 8, 8), mask, Color.White, SamplerMode.Point);
            core.Batch.DrawMasked(uploadedSource, new Vector4(24, 0, 8, 8), uploadedMask, Color.White, SamplerMode.Point);
            core.Batch.End();
        }
        Assert.Equal(0, device.MapCalls);
        Assert.Equal(0, factory.CommandLists.Sum(commands => commands.CopyOperations));
        Assert.Equal(uploadsBeforeConsumer, device.TextureUploadCalls);
        device.Submit(consumer);
        device.WaitForIdle();
        byte[] rgba = GpuReadback.ToRgba(device, destination, Width, Height);
        Assert.Equal(1, device.MapCalls);
        Assert.Equal(1, factory.CommandLists.Sum(commands => commands.CopyOperations));

        for (int variant = 0; variant < 4; variant++)
        {
            int x = variant * 8;
            AssertRgba(rgba, x + 1, 1, 255, 0, 0, 255);
            AssertRgba(rgba, x + 6, 1, 0, 0, 128, 64);
            AssertRgba(rgba, x + 1, 6, 0, 0, 128, 64);
            AssertRgba(rgba, x + 6, 6, 0, 0, 128, 64);
            for (int y = 0; y < 8; y++)
                for (int column = 0; column < 8; column++)
                    for (int channel = 0; channel < 4; channel++)
                    {
                        int expected = rgba[(y * Width + 24 + column) * 4 + channel];
                        Assert.InRange((int)rgba[(y * Width + x + column) * 4 + channel], expected - 1, expected + 1);
                    }
        }
    }

    [GpuFact]
    public void HeaterSilhouetteKeepsSoftSlopingEdges()
    {
        byte[] rgba = Render2DSnapshot.Capture(64, 64, Color.Transparent, ctx =>
        {
            var source = ctx.CreateTexture([255, 0, 0, 255], 1, 1);
            var mask = ctx.CreateTexture(HeaterMaskPixels(), 64, 64);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 0, 64, 64), mask, Color.White, SamplerMode.Point);
            ctx.Batch.End();
        });

        AssertRgba(rgba, 0, 0, 0, 0, 0, 0, width: 64);
        AssertRgba(rgba, 32, 16, 255, 0, 0, 255, width: 64);
        AssertRgba(rgba, 13, 32, 64, 0, 0, 16, width: 64);
        AssertRgba(rgba, 14, 32, 128, 0, 0, 64, width: 64);
        AssertRgba(rgba, 15, 32, 192, 0, 0, 145, width: 64);
    }

    [GpuFact]
    public void ZeroAndOneCoveragePreserveBackgroundAndSource()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, new Color(0f, 0f, 1f, 1f), ctx =>
        {
            var source = ctx.CreateTexture([255, 0, 0, 255], 1, 1);
            var maskA = ctx.CreateTexture([255, 0, 0, 0, 0, 255, 0, 255], 2, 1);
            var maskB = ctx.CreateTexture([0, 0, 255, 0, 255, 0, 0, 255], 2, 1);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 0, Width, 8), FullUv, maskA,
                FullUv, Color.White, SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 8, Width, 8), FullUv, maskB,
                FullUv, Color.White, SamplerMode.Point);
            ctx.Batch.End();
        });

        AssertRgba(rgba, 8, 4, 0, 0, 255, 255);
        AssertRgba(rgba, 24, 4, 255, 0, 0, 255);
        AssertRgba(rgba, 8, 12, 0, 0, 255, 255);
        AssertRgba(rgba, 24, 12, 255, 0, 0, 255);
    }

    [GpuFact]
    public void HalfCoveredEdgeUsesCoverageOnce()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
        {
            var source = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
            var maskA = ctx.CreateTexture([255, 0, 0, 0, 0, 255, 0, 255], 2, 1);
            var maskB = ctx.CreateTexture([0, 0, 255, 0, 255, 0, 0, 255], 2, 1);
            // The midpoint between two alpha texels is exactly half covered without 8-bit quantization.
            var midpoint = new Vector4(0.5f, 0.5f, 0.5f, 0.5f);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 0, Width, 8), FullUv, maskA,
                midpoint, Color.White);
            ctx.Batch.DrawMasked(source, new Vector4(0, 8, Width, 8), FullUv, maskB,
                midpoint, Color.White);
            ctx.Batch.End();
        });

        AssertRgba(rgba, 16, 4, 128, 128, 128, 64);
        AssertRgba(rgba, 16, 12, 128, 128, 128, 64);
    }

    [GpuFact]
    public void MaskMultipliesSourceAndTintAlpha()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
        {
            var source = ctx.CreateTexture([255, 255, 255, 128], 1, 1);
            var mask = ctx.CreateTexture([255, 0, 0, 0, 0, 255, 0, 255], 2, 1);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 0, Width, Height), FullUv, mask,
                new Vector4(0.5f, 0.5f, 0.5f, 0.5f), new Color(1f, 0.5f, 0f, 0.5f));
            ctx.Batch.End();
        });

        AssertRgba(rgba, 16, 8, 32, 16, 0, 4);
    }

    [GpuFact]
    public void VerticalGradientComposesBeforeMask()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
        {
            var source = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
            var mask = ctx.CreateTexture([255, 0, 0, 0, 0, 255, 0, 255], 2, 1);
            ctx.Batch.Begin(SamplerMode.Point);
            // Pixel row 8 is exactly halfway down this gradient.
            ctx.Batch.DrawMasked(source, new Vector4(0, 0.5f, Width, Height), FullUv, mask,
                new Vector4(0.5f, 0.5f, 0.5f, 0.5f),
                new Color(1f, 0f, 0f, 1f), new Color(0f, 0f, 1f, 1f));
            ctx.Batch.End();
        });

        AssertRgba(rgba, 16, 8, 64, 0, 64, 64);
        for (int y = 1; y < Height; y++)
        {
            int previous = ((y - 1) * Width + 16) * 4;
            int current = (y * Width + 16) * 4;
            Assert.True(rgba[current] <= rgba[previous], $"Red increased at row {y}.");
            Assert.True(rgba[current + 2] >= rgba[previous + 2], $"Blue decreased at row {y}.");
            Assert.InRange((int)rgba[current + 3], 63, 65);
        }
    }

    [GpuFact]
    public void PointSourceStillUsesLinearMaskByDefault()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
        {
            var source = ctx.CreateTexture([255, 0, 0, 255, 0, 0, 255, 255], 2, 1);
            var mask = ctx.CreateTexture([255, 255, 255, 0, 255, 255, 255, 255], 2, 1);
            var nearestRed = new Vector4(0.375f, 0.5f, 0.375f, 0.5f);
            var halfCoverage = new Vector4(0.5f, 0.5f, 0.5f, 0.5f);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 0, Width, 4), nearestRed,
                mask, halfCoverage, Color.White);
            ctx.Batch.DrawMasked(source, new Vector4(0, 4, Width, 4), nearestRed,
                mask, halfCoverage, Color.White, SamplerMode.Linear);
            ctx.Batch.DrawMasked(source, new Vector4(0, 8, Width, 4), nearestRed,
                mask, new Vector4(0.375f, 0.5f, 0.375f, 0.5f), Color.White, SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 12, Width, 4), nearestRed,
                mask, new Vector4(0.625f, 0.5f, 0.625f, 0.5f), Color.White, SamplerMode.Point);
            ctx.Batch.End();
        });

        AssertRgba(rgba, 16, 2, 128, 0, 0, 64);
        AssertRgba(rgba, 16, 6, 128, 0, 0, 64);
        AssertRgba(rgba, 16, 10, 0, 0, 0, 0);
        AssertRgba(rgba, 16, 14, 255, 0, 0, 255);
    }

    [GpuFact]
    public void HalfCoverageOverOpaqueBackgroundUsesExistingBlend()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, new Color(0f, 0f, 1f, 1f), ctx =>
        {
            var source = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
            var mask = ctx.CreateTexture([255, 0, 0, 0, 0, 255, 0, 255], 2, 1);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.DrawMasked(source, new Vector4(0, 0, Width, Height), FullUv, mask,
                new Vector4(0.5f, 0.5f, 0.5f, 0.5f), Color.White);
            ctx.Batch.End();
        });

        AssertRgba(rgba, 16, 8, 128, 128, 255, 191);
    }

    [GpuFact]
    public void AdditiveCoverageUsesExistingFactors()
    {
        byte[] rgba = Render2DSnapshot.Capture(Width, Height, new Color(0.25f, 0f, 0f, 0.25f), ctx =>
        {
            var source = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
            var mask = ctx.CreateTexture([255, 0, 0, 0, 0, 255, 0, 255], 2, 1);
            ctx.Batch.Begin(SamplerMode.Point);
            ctx.Batch.BlendMode = BlendMode.Additive;
            ctx.Batch.DrawMasked(source, new Vector4(0, 0, Width, Height), FullUv, mask,
                new Vector4(0.5f, 0.5f, 0.5f, 0.5f), Color.White);
            ctx.Batch.End();
        });

        AssertRgba(rgba, 16, 8, 191, 128, 128, 128);
    }

    static byte[] CaptureUnmaskedParityScene(IGpuDevice device, SpriteBatch batch, Texture2D source, SpriteFont font) =>
        CaptureParityFrame(device, batch, current =>
        {
            current.Begin(SamplerMode.Point);
            SpriteBatchMaskedTestRig.DrawUnmaskedParityScene(current, source, font);
            current.End();
        });

    static byte[] CaptureParityFrame(IGpuDevice device, SpriteBatch batch, Action<SpriteBatch> draw)
    {
        using var target = SampledTarget(device.Factory, 64, 64);
        using var framebuffer = device.Factory.CreateFramebuffer(null, target);
        using var commands = device.Factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, "masked sprite ordinary parity"))
        {
            commands.SetFramebuffer(framebuffer);
            commands.ClearColorTarget(0, new Color(0.125f, 0.25f, 0.5f, 0.75f));
            batch.NewFrame(commands, 64, 64);
            draw(batch);
        }
        device.Submit(commands);
        device.WaitForIdle();
        return GpuReadback.ToRgba(device, target, 64, 64);
    }

    static Vector4 ReverseRegion(Vector4 uv) => new(uv.Z, uv.W, uv.X, uv.Y);

    static byte[] SourceAtlasPixels()
    {
        var pixels = new byte[8 * 4 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++) SetPixel(pixels, 8, x, y, 0, 255, 0, 255);
        byte[] reds = [255, 192, 64, 128, 224, 160];
        for (int i = 0; i < reds.Length; i++)
            SetPixel(pixels, 8, 1 + i % 3, 1 + i / 3, reds[i], 0, 0, 255);
        return pixels;
    }

    static byte[] MaskAtlasPixels()
    {
        var pixels = new byte[8 * 4 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++) SetPixel(pixels, 8, x, y, 255, 255, 255, 255);
        byte[] alphas = [0, 128, 255, 255, 0, 128];
        for (int i = 0; i < alphas.Length; i++)
            SetPixel(pixels, 8, 4 + i % 3, 1 + i / 3, 0, 255, 255, alphas[i]);
        return pixels;
    }

    static byte[] SharedAtlasPixels()
    {
        byte[] source = SourceAtlasPixels(), mask = MaskAtlasPixels();
        for (int y = 0; y < 4; y++)
            mask.AsSpan((y * 8 + 4) * 4, 4 * 4).CopyTo(source.AsSpan((y * 8 + 4) * 4));
        return source;
    }

    static IGpuTexture SampledTarget(IGpuResourceFactory factory, int width, int height) =>
        factory.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));

    static byte[] TargetControlPixels(bool source)
    {
        var pixels = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                bool upperLeft = x < 2 && y < 2;
                if (source) SetPixel(pixels, 4, x, y, upperLeft ? (byte)255 : (byte)0, 0,
                    upperLeft ? (byte)0 : (byte)255, 255);
                else SetPixel(pixels, 4, x, y, upperLeft ? (byte)255 : (byte)0,
                    upperLeft ? (byte)255 : (byte)0, upperLeft ? (byte)255 : (byte)0,
                    upperLeft ? (byte)255 : (byte)128);
            }
        return pixels;
    }

    static byte[] HeaterMaskPixels()
    {
        var pixels = new byte[64 * 64 * 4];
        for (int y = 8; y < 64; y++)
        {
            int left = 10 + Math.Max(0, y - 24) / 2;
            for (int x = left - 1; x <= 64 - left; x++)
            {
                int inset = Math.Min(x - left, 63 - left - x);
                byte alpha = inset switch { -1 => 64, 0 => 128, 1 => 192, _ => 255 };
                SetPixel(pixels, 64, x, y, 0, 255, 0, alpha);
            }
        }
        return pixels;
    }

    static void SetPixel(byte[] pixels, int width, int x, int y, byte r, byte g, byte b, byte a)
    {
        int offset = (y * width + x) * 4;
        pixels[offset] = r; pixels[offset + 1] = g; pixels[offset + 2] = b; pixels[offset + 3] = a;
    }

    static void AssertRgba(byte[] rgba, int x, int y, byte r, byte g, byte b, byte a,
        int tolerance = 1, int width = Width)
    {
        int offset = (y * width + x) * 4;
        byte[] expected = [r, g, b, a];
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange((int)rgba[offset + channel], expected[channel] - tolerance, expected[channel] + tolerance);
    }
}
