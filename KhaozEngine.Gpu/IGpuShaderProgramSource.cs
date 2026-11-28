namespace KhaozEngine.Gpu;

/// <summary>Looks up compiled shader programs by id and variant.</summary>
public interface IGpuShaderProgramSource
{
    /// <summary>The program with this id and variant, refusing an unknown one.</summary>
    GpuShaderProgram Get(string id, string variant = "");
}
