namespace KhaozEngine.Render3D
{
    /// <summary>One decoded material texture: tightly-packed RGBA8 pixels (row-major, top-left origin,
    /// <c>Rgba.Length == Width * Height * 4</c>) plus its dimensions. Carries no GPU resources; upload it with
    /// <see cref="Scene3D.LoadTexture(byte[],int,int,TextureMipPolicy)"/> (or via <see cref="Scene3D.LoadSurfaceMaps"/>).
    /// <para>
    /// An image the glTF loader decoded carries an <see cref="Identity"/> and is SHARED: every part, material and glb
    /// that names the same image gets the same decode, and a scene uploads it once (see
    /// <see cref="Scene3D.ResidentMaterialTextureCount"/>). Its pixels are decoded when the image is first loaded and
    /// held until a scene has uploaded them. After that only the image's encoded file bytes are kept, and reading
    /// <see cref="Rgba"/> decodes again. Treat a shared image's pixels as read-only. To edit them, copy them into a new
    /// <see cref="DecodedImage"/>, which has no identity and uploads on its own.
    /// </para></summary>
    public readonly struct DecodedImage
    {
        readonly byte[]? _rgba;
        readonly MaterialImageSource? _source;
        readonly string? _identity;

        /// <summary>Tightly-packed RGBA8 pixels, row-major, top-left origin (length = Width*Height*4). For a shared
        /// loader image this may decode the image again when its pixels were released after an upload.</summary>
        public byte[] Rgba => _source is { } source ? source.Pixels() : _rgba!;

        public int Width { get; }
        public int Height { get; }

        /// <summary>The residency key a scene shares this image's texture under, or <c>null</c> for an image the
        /// caller built. An external image is <c>file:</c> plus its absolute path, resolved against its glb's
        /// folder. An embedded image (a GLB buffer view or a data URI) is <c>sha256:</c> plus the lowercase hex
        /// SHA-256 of its encoded bytes. Two images with one identity are one texture in a scene.</summary>
        public string? Identity => _source?.Identity ?? _identity;

        public DecodedImage(byte[] rgba, int width, int height)
        {
            _rgba = rgba; Width = width; Height = height; _source = null; _identity = null;
        }

        // Pixels the caller's decode owns (the budgeted strict path), still named for scene residency.
        internal DecodedImage(byte[] rgba, int width, int height, string identity)
        {
            _rgba = rgba; Width = width; Height = height; _source = null; _identity = identity;
        }

        // A shared loader image. Width and height are the source's, known without its pixels.
        internal DecodedImage(MaterialImageSource source)
        {
            _rgba = null; _source = source; _identity = null; Width = source.Width; Height = source.Height;
        }

        /// <summary>The shared source behind a loader image, or null. A scene tells it when its pixels have been
        /// uploaded.</summary>
        internal MaterialImageSource? Source => _source;
    }
}
