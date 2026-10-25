using KhaozEngine.Gpu;

namespace KhaozEngine.Render2D;

public sealed partial class SpriteBatch
{
    readonly GpuOutputDescription _maskedOutput;
    readonly GpuVertexLayoutDescription _maskedVertexLayout;
    MaskedSpriteBatchResources? _maskedResources;

    // A failed construction releases its own resources and leaves the ordinary path usable for a retry.
    private MaskedSpriteBatchResources EnsureMaskedResources() =>
        _maskedResources ??= new MaskedSpriteBatchResources(_gd, _maskedOutput, _vpLayout, _maskedVertexLayout);
}
