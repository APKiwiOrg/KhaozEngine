using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>What a resident material texture that an admitted upload created still needs before it can be sampled:
/// its level 0 written in bounded rows, then its mip chain generated (issue #1776). Every upload that references the
/// texture advances the same fill, so an image a batch of uploads in flight shares is written once and its chain is
/// generated once. The chain is generated on the GPU exactly as <see cref="Scene3D.LoadTexture(byte[],int,int,TextureMipPolicy)"/>
/// generates it, so a texture the convenience path and an admitted upload share is the same whichever built it. The
/// chain counts only once the recording that generates it is submitted: recording it changes nothing here, and its
/// owner marks it after the submission succeeds, so a recording, End or submission that fails leaves the chain owed
/// to whichever owner of the texture comes next.</summary>
internal sealed class MaterialTextureFill
{
    SceneUploadTransfer? _level0;
    MaterialImageSource? _source;

    internal MaterialTextureFill(IGpuTexture texture, in DecodedImage image)
    {
        Texture = texture;
        _level0 = new SceneUploadTransfer(texture, image.Rgba, image.Width, 0, 0);
        _source = image.Source;
        // A one-level texture has no chain, so no upload opens a list for it, as a one-level LoadTexture opens none.
        ChainSubmitted = texture.MipLevels <= 1;
    }

    internal IGpuTexture Texture { get; }

    /// <summary>Every level-0 row has been written.</summary>
    internal bool Written => _level0 is null;

    /// <summary>A submission that generates the chain has been accepted, ahead of the fence or drain that proves it.
    /// Work submitted later on the device runs after it, so a later owner may sample the texture.</summary>
    internal bool ChainSubmitted { get; private set; }

    internal bool Complete => Written && ChainSubmitted;

    /// <summary>Write level-0 rows within <paramref name="allowance"/> bytes.</summary>
    internal long Advance(IGpuDevice device, long allowance)
    {
        if (_level0 is null) return 0;
        long written = _level0.Advance(device, allowance);
        if (_level0.Finished) Finish();
        return written;
    }

    /// <summary>Write every remaining row at once, for a convenience load that cannot wait for the uploads in flight.</summary>
    internal void WriteAll(IGpuDevice device)
    {
        while (_level0 is not null) Advance(device, long.MaxValue);
    }

    /// <summary>Record the chain's generation into <paramref name="list"/> when it is still owed, and say whether it
    /// was. The chain stays owed until the list's owner calls <see cref="MarkSubmitted"/>.</summary>
    internal bool RecordGeneration(IGpuCommandList list)
    {
        if (ChainSubmitted) return false;
        list.GenerateMipmaps(Texture);
        return true;
    }

    /// <summary>The recording that generates the chain ended and its submission was accepted.</summary>
    internal void MarkSubmitted() => ChainSubmitted = true;

    // The rows are on their way to the GPU, so the decoded pixels are no longer this fill's to hold.
    void Finish()
    {
        _level0 = null;
        _source?.ReleaseAfterUpload();
        _source = null;
    }
}
