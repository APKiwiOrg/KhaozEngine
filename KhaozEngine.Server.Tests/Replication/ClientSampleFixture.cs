using System;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;

namespace KhaozEngine.Tests.Replication;

internal sealed class ClientSampleFixture
{
    internal readonly record struct Sample(float Value) : IComponent;

    private readonly World client = new();
    private readonly ClientReplicationView view;
    private readonly Entity first;

    internal ClientSampleFixture(ReplicationRegistry registry, float older, float newer, int entityCount = 1)
    {
        view = new ClientReplicationView(registry);
        var server = new World();
        var entities = new Entity[entityCount];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = server.Spawn();
            server.Set(entities[i], new NetId(i + 1));
            server.Set(entities[i], new Sample(older));
        }

        Apply(0);
        foreach (Entity entity in entities)
            server.Set(entity, new Sample(newer));
        Apply(1);
        first = view.Entities[1];

        void Apply(double time)
        {
            client.AdvanceTick();
            view.Apply(client, SnapshotWriter.Write(server, registry));
            view.RecordInterpolationSample(time);
        }
    }

    internal float Value => client.Get<Sample>(first).Value;

    internal void Interpolate(float factor) => view.InterpolateAt(client, factor);

    internal static ReplicationRegistry Registry(ushort typeId, bool discrete, Func<BinaryReader, Sample> read)
    {
        var registry = new ReplicationRegistry();
        registry.Register<Sample>(typeId, static (sample, writer) =>
        {
            writer.Write(sample.Value);
            if (sample.Value < 0) writer.Write(16);
        }, read, lerp: discrete ? null : static (a, b, t) => new Sample(a.Value + (b.Value - a.Value) * t),
            discreteSample: discrete);
        return registry;
    }
}
