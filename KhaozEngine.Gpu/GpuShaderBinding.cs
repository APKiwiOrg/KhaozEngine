namespace KhaozEngine.Gpu;

/// <summary>One resource a compiled program reads, at its layout position and per-backend slot.</summary>
public readonly record struct GpuShaderBinding(string Name, int Set, int Binding, GpuResourceKind Kind, int MetalIndex, int HlslRegister);
