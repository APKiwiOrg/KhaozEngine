using KhaozEngine.Ecs;
using KhaozEngine.Replication;

namespace KhaozEngine.Tests.Replication;

internal struct CountedBuiltin : IComponent { public int N; }

internal struct CountedExtension : IComponent { public int N; }

/// <summary>
/// A fresh registry whose two readers count their calls: an unframed built-in at type id 9 and a framed extension at
/// <c>FirstExtensionTypeId + 1</c>. Oracle: per accepted packet the two counters together advance by exactly the number
/// of known components in the reconstructed projection. Publication adds zero reads. A rejected packet reads at most
/// its received known components.
/// </summary>
internal sealed class CountingCodec
{
    public const ushort BuiltinId = 9;
    public const ushort ExtensionId = ReplicationRegistry.FirstExtensionTypeId + 1;

    public CountingCodec()
    {
        Registry = new ReplicationRegistry();
        Registry.Register<CountedBuiltin>(BuiltinId, (v, bw) => bw.Write(v.N), br =>
        {
            BuiltinReads++;
            return new CountedBuiltin { N = br.ReadInt32() };
        });
        Registry.Register<CountedExtension>(ExtensionId, (v, bw) => bw.Write(v.N), br =>
        {
            ExtensionReads++;
            return new CountedExtension { N = br.ReadInt32() };
        });
    }

    public ReplicationRegistry Registry { get; }

    public int BuiltinReads { get; private set; }

    public int ExtensionReads { get; private set; }

    public int Reads => BuiltinReads + ExtensionReads;
}
