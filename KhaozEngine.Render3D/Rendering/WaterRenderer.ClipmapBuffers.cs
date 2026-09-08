using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    internal sealed partial class WaterRenderer
    {
        /// <summary>The ring count for one plane: the explicit setting when it is positive, otherwise derived so
        /// the outermost ring covers the plane from any camera position on it.</summary>
        static int LevelsFor(in WaterPlane plane, WaterSettings settings, float cell, int ringCells)
            => settings.ClipmapLevels > 0
                ? Math.Clamp(settings.ClipmapLevels, 1, WaterClipmap.MaxLevels)
                : WaterClipmap.LevelsFor(plane, cell, ringCells);

        /// <summary>
        /// Size the clipmap's buffers, CPU scratch and per-plane cache slots for the frame. Effective flat planes
        /// do not contribute to the largest slice and never receive a lattice.
        /// </summary>
        void EnsureClipBuffers(ReadOnlySpan<WaterPlane> planes, WaterSettings settings, Vector3 renderOrigin)
        {
            float cell = MathF.Max(settings.ClipmapCellSize, 1e-4f);
            int ringCells = WaterClipmap.ClampRingCells(settings.ClipmapRingCells);
            int vcount = 0, icount = 0;
            foreach (WaterPlane relative in planes)
            {
                if (UsesFlatQuad(relative, settings)) continue;
                var plane = new WaterPlane(relative.CenterX + renderOrigin.X, relative.SurfaceY + renderOrigin.Y,
                    relative.CenterZ + renderOrigin.Z, relative.HalfExtentX, relative.HalfExtentZ);
                int levels = LevelsFor(plane, settings, cell, ringCells);
                vcount = Math.Max(vcount, WaterClipmap.VertexCount(levels, ringCells));
                icount = Math.Max(icount, WaterClipmap.IndexCount(levels, ringCells));
            }

            if (_clipSlots.Length < planes.Length)
            {
                var grown = new ClipSlot[planes.Length];
                Array.Copy(_clipSlots, grown, _clipSlots.Length);
                for (int i = _clipSlots.Length; i < grown.Length; i++) grown[i] = new ClipSlot();
                _clipSlots = grown;
            }

            if (_clipVb != null && _clipSliceVerts >= vcount && _clipSliceIndices >= icount
                && (long)_clipSliceVerts * planes.Length * ClipVertexBytes <= _clipVb.SizeInBytes) return;

            _clipSliceVerts = Math.Max(_clipSliceVerts, vcount);
            _clipSliceIndices = Math.Max(_clipSliceIndices, icount);
            if (_clipVb != null) _retired.Add(_clipVb);
            if (_clipIb != null) _retired.Add(_clipIb);
            _clipVerts = new WaterClipmapVertex[_clipSliceVerts];
            _clipIndices = new uint[_clipSliceIndices];
            _clipVb = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                (uint)(_clipSliceVerts * planes.Length) * ClipVertexBytes, GpuBufferUsage.VertexBuffer));
            _clipIb = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                (uint)(_clipSliceIndices * planes.Length) * sizeof(uint), GpuBufferUsage.IndexBuffer));
            foreach (ClipSlot slot in _clipSlots) slot.Valid = false;
        }

        /// <summary>Byte size of one <see cref="WaterClipmapVertex"/>.</summary>
        internal const uint ClipVertexBytes = 28;
    }
}
