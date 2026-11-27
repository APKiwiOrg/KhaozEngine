using System.Runtime.InteropServices;
using KhaozEngine.Gpu.Core.Interop;
using Xunit;

namespace KhaozEngine.Tests.GpuCore;

[Collection("CoreBackend")]
public sealed class InteropLayoutTests
{
    [NativeFact]
    public unsafe void Draw_record_layout_matches_the_header()
    {
        Assert.Equal(64, sizeof(khaoz_draw_record));
        Assert.Equal(0, Marshal.OffsetOf<khaoz_draw_record>(nameof(khaoz_draw_record.world)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<khaoz_draw_record>(nameof(khaoz_draw_record.mesh_id)).ToInt32());
        Assert.Equal(52, Marshal.OffsetOf<khaoz_draw_record>(nameof(khaoz_draw_record.material_id)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<khaoz_draw_record>(nameof(khaoz_draw_record.sort_key)).ToInt32());
    }
}
