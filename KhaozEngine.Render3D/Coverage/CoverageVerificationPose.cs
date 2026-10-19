using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D;

/// <summary>Owned original pose and actual upload words. Immutable topology and supports outlive their conformer.</summary>
internal sealed class CoverageVerificationPose
{
    internal readonly CoverageRefinement Topology;
    internal readonly Matrix4x4[] Palette, InverseBind;
    internal readonly CoverageCommandPose[] Commands = new CoverageCommandPose[CoverageScratch.MaxCommands];
    internal readonly uint[] UploadWords, PoseWords;
    internal Matrix4x4 Model;
    internal int CommandCount;
    internal ulong FrameId, ViewId, Preparation;
    internal long FrameIndex;
    internal MotionKey Actor;
    internal CoverageGpuReservation Reservation;

    internal CoverageVerificationPose(CoverageRefinement topology, int bones)
    {
        Topology = topology;
        Palette = new Matrix4x4[bones]; InverseBind = new Matrix4x4[bones];
        UploadWords = new uint[CoverageGpuLayout.FrameWords(bones)];
        PoseWords = new uint[bones * 32 + 16 + CoverageScratch.MaxCommands * 42];
    }

    internal void Capture(CoveragePreparation p, long frameIndex, ReadOnlySpan<uint> upload)
    {
        p.Palette.CopyTo(Palette, 0); p.Owner.InverseBind.CopyTo(InverseBind, 0);
        Array.Clear(Commands); p.Commands.AsSpan(0, p.CommandCount).CopyTo(Commands);
        Model = p.Model; CommandCount = p.CommandCount;
        FrameId = p.Generation; ViewId = p.View; Actor = p.Actor; Preparation = p.Id; FrameIndex = frameIndex;
        Reservation = p.GpuReservation;
        upload.CopyTo(UploadWords);
        Array.Clear(PoseWords);
        int at = 0;
        Matrices(Palette); Matrices(InverseBind);
        CoverageGpuUpload.WriteMatrix(Model, PoseWords.AsSpan(at)); at += 16;
        for (int c = 0; c < CommandCount; c++)
        {
            var command = Commands[c];
            PoseWords[at++] = command.Id;
            PoseWords[at++] = (uint)command.EquipmentInstanceId; PoseWords[at++] = (uint)(command.EquipmentInstanceId >> 32);
            PoseWords[at++] = unchecked((uint)command.RegionIndex);
            CoverageGpuUpload.WriteMatrix(command.EquipmentWorld, PoseWords.AsSpan(at)); at += 16;
            CoverageGpuUpload.WriteMatrix(command.CoverageWorld, PoseWords.AsSpan(at)); at += 16;
            Scalar(command.MinZ); Scalar(command.MaxZ); Scalar(command.CollarWidth);
            Scalar(command.Clearance); Scalar(command.MaxWorldRecession); Scalar(command.TransitionWeight);
        }
        void Matrices(Matrix4x4[] matrices)
        {
            MemoryMarshal.Cast<Matrix4x4, uint>(matrices.AsSpan()).CopyTo(PoseWords.AsSpan(at));
            at += matrices.Length * 16;
        }
        void Scalar(float value) => PoseWords[at++] = BitConverter.SingleToUInt32Bits(value);
    }
}
