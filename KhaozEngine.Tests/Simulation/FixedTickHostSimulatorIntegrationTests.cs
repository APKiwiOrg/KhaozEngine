using KhaozEngine.Netcode;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public class FixedTickHostSimulatorIntegrationTests
{
    // A trivial 1-D integrator: position advances by the command's velocity * dt each tick.
    private readonly record struct PosState(float X);
    private readonly record struct MoveCmd(float Velocity);

    private sealed class Integrator : ITickSimulator<PosState, MoveCmd>
    {
        public PosState Step(in PosState state, in MoveCmd command, float dt) =>
            new(state.X + command.Velocity * dt);
    }

    [Fact]
    public void FixedTicks_DrainCommands_AndStepSimulator_Deterministically()
    {
        const float dt = 0.1f;
        var host = new FixedTickHost(dt);
        var sim = new Integrator();
        var queue = new RemoteCommandQueue<MoveCmd>(neutralCommand: new MoveCmd(0f));

        // Two queued commands for slot 0: velocity 10 then 0.
        queue.Store(slot: 0, seq: 0, command: new MoveCmd(10f));
        queue.Store(slot: 0, seq: 1, command: new MoveCmd(0f));

        var state = new PosState(0f);
        // 0.3s elapsed -> exactly 3 ticks.
        int produced = host.Advance(0.3f, tickIndex =>
        {
            MoveCmd cmd = queue.Dequeue(slot: 0, out _);
            state = sim.Step(state, cmd, dt);
        });

        Assert.Equal(3, produced);
        // tick0: +10*0.1=1.0 ; tick1: +0 ; tick2: neutral(0) -> 1.0
        Assert.Equal(1.0, state.X, 5);
        Assert.Equal(1, queue.GetLastAcknowledgedSeq(0)); // both real commands consumed
    }
}
