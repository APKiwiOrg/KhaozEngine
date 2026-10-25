using System.Numerics;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui;

/// <summary>A leading square icon for a <see cref="TooltipLine"/>. The caller owns the texture lifetime.</summary>
/// <param name="Texture">The caller-owned icon texture.</param>
/// <param name="SourceUV">The source UV rectangle within the texture.</param>
/// <param name="Size">Square size in design units, independent of <see cref="TooltipLine.Scale"/>.
/// Must be finite and positive.</param>
public readonly record struct TooltipIcon(Texture2D Texture, Vector4 SourceUV, float Size);

/// <summary>A tiled fill inside a <see cref="Tooltip"/>'s border. The caller owns the texture lifetime.</summary>
/// <param name="Texture">The caller-owned texture, tiled from the interior's top-left corner.</param>
/// <param name="TileSize">Square tile size in design units. Must be finite and positive.</param>
/// <param name="Tint">The colour multiplied into each tile.</param>
public readonly record struct TooltipBackdrop(Texture2D Texture, float TileSize, Vector4 Tint);
