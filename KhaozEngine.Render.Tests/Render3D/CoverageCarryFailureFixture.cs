using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Two synthetic records and independently authored packed inputs for actual SPIR-V execution.</summary>
internal sealed class CoverageCarryFailureFixture
{
    internal const uint Poison = 0x7fc01234;
    internal const int Source = 64, Before = 112, After = 144, Candidate = 176, Basis = 208, Moved = 300;
    internal readonly uint[] Static = new uint[384], Frames = new uint[102];
    internal readonly uint[] Work = Enumerable.Repeat(Poison, 512).ToArray();
    internal readonly uint[] Errors = [uint.MaxValue, uint.MaxValue, uint.MaxValue, 0, 0, 0, 0, 0];
    internal readonly uint[] Current = Enumerable.Repeat(Poison, 32).ToArray();
    internal readonly uint[] Table = [0, 1, Bits(.25f), Bits(.25f)];
    internal uint[] Expected = [];
    internal CoverageCarryFailureFixture()
    {
        Frames[0] = 1; Frames[1] = Frames[2] = Frames[3] = Frames[4] = 64; Frames[5] = 6;
        int f = 6;
        Frames[f + 5] = 3; Frames[f + 6] = 2; Frames[f + 7] = 2; Frames[f + 8] = 2;
        Frames[f + 11] = 1; Frames[f + 12] = 1; Frames[f + 13] = 7; Frames[f + 14] = 2;
        Frames[f + 32 + 2] = Bits(1); Frames[f + 32 + 3] = Bits(1);
        Frames.AsSpan(f + 64, 32).Fill(uint.MaxValue); Frames[f + 64] = 0;
        Work.AsSpan(0, 64).Clear();
        Row(0, Source, 3); Row(2, Source, 3); Row(4, Before, 2); Row(8, After, 2);
        Row(10, Candidate, 2); Row(42, Basis, 2); Row(44, Moved, 2);
        Static[12] = 64; Static[16] = 128; Static[22] = 144; Static[24] = 160;
        Static[26] = 168; Static[28] = 176; Static[20] = 184;
        Static[36] = 192; Static[38] = 196; Static[40] = 200; Static[42] = 204;
        for (int v = 0; v < 2; v++)
        {
            int record = 64 + v * 10;
            Static[record + 1] = (uint)v; Static[record + 6] = (uint)v;
            Static[record + 7] = (uint)v; Static[record + 9] = 1;
        }
        Static[128 + 5] = 0; Static[128 + 6] = 1; Static[128 + 7] = 2;
        Static[144] = 0; Static[145] = 1; Static[146] = 2; Static[147] = 12; Static[150] = 12;
        Static[151] = 0; Static[152] = 1; Static[153] = 2;
        Static[154] = 6; Static[155] = 6; Static[157] = 12;
        Floats(Static, 160, 12, 0, 0, 6, 6, 0);
        Floats(Static, 168, 12, 0, 0, 6, 6, 0); Static[177] = Bits(.5f);
        for (int i = 0; i < 6; i++) Static[184 + i] = i < 3 ? 0u : 1u;
        Static[192] = Static[200] = 0; Static[193] = Static[201] = 1; Static[194] = Static[202] = 2;
        Static[196] = Static[204] = 0; Static[197] = Static[205] = 3;
        void Row(int index, int offset, int count) { Work[index] = (uint)offset; Work[index + 1] = (uint)count; }
    }

    internal void CancellingTransport(bool execute = true)
    {
        Static[75] = uint.MaxValue;
        uint[] a = Record(Vector3.Zero, Vector3.UnitX, new(.25f, .5f, .75f, 1), Vector2.Zero, new(0, 1, 0, 1));
        uint[] b = Record(new(2, 0, 0), -Vector3.UnitX, new(.75f, .5f, .25f, 1), Vector2.UnitX, new(0, 1, 0, 1));
        a.CopyTo(Work, Source); b.CopyTo(Work, Source + 16); a.CopyTo(Work, Source + 32);
        Expected = [.. a, .. Record(new(1, 0, 0), Vector3.Zero, new(.5f, .5f, .5f, 1), new(.5f, 0), new(0, 1, 0, 1))];
        if (execute) Run("CoverageCarryTransport");
    }

    internal void SkinAndCancellingTransport(float xScale)
    {
        CancellingTransport(execute: false);
        Static[8] = 208; Static[10] = 272;
        for (int vertex = 0; vertex < 3; vertex++)
        {
            Work.AsSpan(Source + vertex * 16, 16).CopyTo(Static.AsSpan(208 + vertex * 20, 16));
            Static[208 + vertex * 20 + 16] = Bits(1);
        }
        int palette = 6 + 16;
        Frames[palette] = Bits(xScale); Frames[palette + 5] = Frames[palette + 10] = Frames[palette + 15] = Bits(1);
        Work.AsSpan(Source, 48).Fill(Poison);
        Expected[16] = Bits(xScale);
        Run("CoverageCarrySkin");
        Run("CoverageCarryTransport");
    }

    internal void FailingGather(bool fail)
    {
        uint[] a = Record(Vector3.Zero, Vector3.UnitX, new(.25f, .5f, .75f, 1), Vector2.Zero, Vector4.Zero);
        uint[] b = Record(new(2, 0, 0), Vector3.UnitX, new(.75f, .5f, .25f, 1), Vector2.UnitX, Vector4.Zero);
        a.CopyTo(Work, Before); b.CopyTo(Work, Before + 16); Expected = [.. a, .. b];
        Work[After] = Bits(0); Work[After + 1] = Bits(0); Work[After + 2] = Bits(-.25f);
        Work[After + 16] = Bits(2); Work[After + 17] = Bits(0); Work[After + 18] = Bits(-.25f);
        Work[Moved] = Work[Moved + 1] = 1;
        Work.AsSpan(Basis, 92).Clear();
        for (int face = 0; face < 2; face++)
        {
            int start = Basis + face * 46;
            for (int row = 0; row < 12; row++)
                if (!fail || face == 0 || row < 9) Work[start + row * 3 + row % 3] = Bits(1);
            Work[start + 45] = 2;
        }
    }

    internal void Publish()
    {
        if (CoverageCarryShaders.Kernels.Any(k => k.Name == "CoverageCarryGather")) Run("CoverageCarryGather");
        Run("CoverageCarryPublish");
    }
    void Run(string name)
    {
        var kernel = CoverageCarryShaders.Kernels.Single(k => k.Name == name);
        uint[][] buffers = name == "CoverageCarryTransport" ? [Static, Frames, Work, Errors, Table]
            : name == "CoverageCarryPublish" ? [Static, Frames, Work, Errors, Current] : [Static, Frames, Work, Errors];
        new CoverageSpirvExecutor(kernel.Source, name, buffers).Run(2);
    }
    internal void AssertUncovered()
    {
        Assert.NotEqual(0u, Errors[6]);
        Assert.DoesNotContain(Poison, Work.AsSpan(Before, 32).ToArray());
        Assert.DoesNotContain(Poison, Current);
        Assert.Equal(Expected, Current);
    }
    static uint[] Record(Vector3 p, Vector3 n, Vector4 color, Vector2 uv, Vector4 tangent) =>
        new[] { p.X, p.Y, p.Z, n.X, n.Y, n.Z, color.X, color.Y, color.Z, color.W, uv.X, uv.Y,
            tangent.X, tangent.Y, tangent.Z, tangent.W }.Select(Bits).ToArray();
    static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
    static void Floats(uint[] words, int offset, params float[] values)
    { for (int i = 0; i < values.Length; i++) words[offset + i] = Bits(values[i]); }
}
