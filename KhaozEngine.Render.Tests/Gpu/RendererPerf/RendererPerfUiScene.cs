using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Three generated sprite textures plus one pinned libre font atlas, in fixed painter order.</summary>
internal sealed class RendererPerfUiScene : IDisposable
{
    const string FontDigest = "319cff6e7a31f0f2a41c475dca42890aa5d19fe16017e2290f8c1d4e14f76481";
    const float FontHeight = 16f;
    const int Oversample = 1;
    readonly byte[] _fontBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf"));
    readonly RendererPerfTexture[] _images = Enumerable.Range(0, 3)
        .Select(i => new RendererPerfTexture(8, 8, ColorPixels(i), TextureMipPolicy.None)).ToArray();
    readonly BakedFont _baked;
    readonly Rect[] _clips;
    readonly SpriteDraw[][] _sprites;
    readonly TextDraw[][] _text;
    readonly Vector2[] _offsets = Enumerable.Range(0, RendererPerfRecipe.Period)
        .Select(i => new Vector2(RendererPerfRecipe.Phase(i) * 2f, 0f)).ToArray();
    Texture2D[] _textures = [];
    SpriteFont? _font;
    internal RendererPerfUiScene()
    {
        if (RendererPerfRecipe.Hash(_fontBytes) != FontDigest)
            throw new InvalidOperationException("The redistributable renderer font differs from its pinned digest.");
        _baked = SpriteFont.BakeCpu(_fontBytes, FontHeight, Oversample);
        _clips = Enumerable.Range(0, 16).Select(i => new Rect(i % 4 * 480f, i / 4 * 270f, 480f, 270f)).ToArray();
        _sprites = _clips.Select(clip => Enumerable.Range(0, 64).Select(i => new SpriteDraw(
            i < 22 ? 0 : i < 43 ? 1 : 2,
            new Vector4(clip.X + 8f + i % 16 * 28f, clip.Y + 8f + i / 16 * 20f, 20f, 16f),
            new Vector4(0f, 0f, 1f, 1f), new Color(1f, .9f, .8f, .65f))).ToArray()).ToArray();
        _text = _clips.Select(clip => Enumerable.Range(0, 8).Select(row => new TextDraw(
            "RendererBaseline", new Vector2(clip.X + 12f, clip.Y + 96f + row * 20f), Color.White, 1f)).ToArray()).ToArray();
    }
    internal string FontSha256 => FontDigest;
    internal object Assets => new
    {
        Sprites = _images,
        Font = new
        {
            Path = "Assets/Roboto-Regular.ttf",
            License = "Apache-2.0",
            Sha256 = FontDigest,
            FontHeight,
            Oversample,
            _baked.AtlasW,
            _baked.AtlasH,
            _baked.Ascent,
            _baked.LineHeight,
            _baked.RenderScale,
            AtlasSha256 = RendererPerfRecipe.Hash(_baked.Atlas),
            Mips = 1,
            Format = "R8G8B8A8UNorm",
            Glyphs = _baked.Glyphs.OrderBy(pair => pair.Key).Select(pair => new { Codepoint = (int)pair.Key, Metrics = pair.Value }).ToArray(),
        },
    };
    internal object Construction => new
    {
        SpriteQuads = 1024,
        GlyphQuads = 2048,
        TextureCount = 4,
        ClipRegions = _clips,
        Sprites = _sprites,
        Text = _text,
        Offsets = _offsets,
        GroupByTexture = false,
        Blend = BlendMode.Alpha,
        Sampler = SamplerMode.Linear,
        Projection = Matrix4x4.CreateOrthographicOffCenter(0, RendererPerfRecipe.Width, RendererPerfRecipe.Height, 0, -1, 1),
        VertexStride = 64,
        VerticesPerQuad = 6,
        Draws = 64,
        Flushes = 16,
    };
    internal Vector2 FrameOffset(int frameIndex) => _offsets[RendererPerfRecipe.SequenceIndex(frameIndex)];
    internal void Load(IGpuDevice device)
    {
        Dispose();
        _textures = _images.Select(image => image.LoadUi(device)).ToArray();
        _font = SpriteFont.Build(device, _fontBytes, FontHeight, Oversample);
    }
    // The host owns NewFrame/Begin/End and brackets this after Scene3D on the display target.
    internal void Queue(SpriteBatch batch, int frameIndex)
    {
        if (_font is null) throw new InvalidOperationException("Load the UI before submitting it.");
        Vector2 offset = FrameOffset(frameIndex);
        batch.GroupByTexture = false;
        batch.BlendMode = BlendMode.Alpha;
        for (int clip = 0; clip < _clips.Length; clip++)
        {
            batch.SetScissor(_clips[clip]);
            foreach (SpriteDraw draw in _sprites[clip])
            {
                Vector4 destination = draw.Destination;
                destination.X += offset.X;
                destination.Y += offset.Y;
                batch.Draw(_textures[draw.Texture], destination, draw.SourceUv, draw.Color);
            }
            foreach (TextDraw draw in _text[clip])
                batch.DrawString(_font, draw.Text, draw.Position + offset, draw.Color, draw.Scale);
            batch.ClearScissor();
        }
    }
    internal static byte[] ColorPixels(int texture)
    {
        var pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < 64; i++)
        {
            pixels[i * 4] = (byte)(96 + texture * 32);
            pixels[i * 4 + 1] = (byte)(192 - texture * 24);
            pixels[i * 4 + 2] = (byte)(64 + i % 8 * 16);
            pixels[i * 4 + 3] = (byte)((i % 8 + i / 8) % 3 == 0 ? 0 : 255);
        }
        return pixels;
    }
    public void Dispose()
    {
        foreach (Texture2D texture in _textures) texture.Dispose();
        _textures = [];
        _font?.Dispose();
        _font = null;
    }
    sealed record SpriteDraw(int Texture, Vector4 Destination, Vector4 SourceUv, Color Color);
    sealed record TextDraw(string Text, Vector2 Position, Color Color, float Scale);
}

/// <summary>One generated texture descriptor used directly by hashing and cold resource loading.</summary>
internal sealed record RendererPerfTexture(int Width, int Height, byte[] Pixels, TextureMipPolicy Policy)
{
    public uint MipLevels => Policy.LevelsFor(Width, Height);
    public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8UNorm;
    internal Scene3D.TextureHandle Load(Scene3D scene) => scene.LoadTexture(Pixels, Width, Height, Policy);
    internal Texture2D LoadUi(IGpuDevice device)
    {
        if (MipLevels != 1) throw new InvalidOperationException("UI recipe images require one mip level.");
        IGpuTexture texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            (uint)Width, (uint)Height, Format, GpuTextureUsage.Sampled));
        device.UpdateTexture(texture, Pixels, 0, 0, (uint)Width, (uint)Height);
        return new Texture2D(device, texture, Width, Height, ownsHandle: true);
    }
}
