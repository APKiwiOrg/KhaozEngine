using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// <see cref="MovementCommitmentRequest"/> moved below Locomotion with <see cref="IAdminControllable"/> (#826), so its
/// constructor restates the refusals <see cref="MovementCommitment"/> makes instead of borrowing them. This project
/// is the one that sees both assemblies, so this is where the two are held equal: the same inputs are refused by
/// both with the same exception, parameter and message, and admitted by both with the same normalized direction.
/// </summary>
public class MovementCommitmentRequestParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        float nan = float.NaN, inf = float.PositiveInfinity;
        // direction x, direction y, horizontal, vertical, gravity, preparation, recovery, timeout
        yield return new object[] { 1f, 0f, 3f, 5f, 25f, 0f, 0f, 5f };
        yield return new object[] { 3f, -4f, 0f, 0.1f, 9.8f, 0.25f, 0.5f, 1f };
        yield return new object[] { 1e-5f, 0f, 1f, 1f, 1f, 0f, 0f, 1f };
        yield return new object[] { 0f, 0f, 1f, 1f, 1f, 0f, 0f, 1f };
        yield return new object[] { nan, 1f, 1f, 1f, 1f, 0f, 0f, 1f };
        yield return new object[] { 1f, inf, 1f, 1f, 1f, 0f, 0f, 1f };
        yield return new object[] { 1f, 0f, -1f, 1f, 1f, 0f, 0f, 1f };
        yield return new object[] { 1f, 0f, nan, 1f, 1f, 0f, 0f, 1f };
        yield return new object[] { 1f, 0f, 1f, 0f, 1f, 0f, 0f, 1f };
        yield return new object[] { 1f, 0f, 1f, inf, 1f, 0f, 0f, 1f };
        yield return new object[] { 1f, 0f, 1f, 1f, 0f, 0f, 0f, 1f };
        yield return new object[] { 1f, 0f, 1f, 1f, -inf, 0f, 0f, 1f };
        yield return new object[] { 1f, 0f, 1f, 1f, 1f, -0.1f, 0f, 1f };
        yield return new object[] { 1f, 0f, 1f, 1f, 1f, nan, 0f, 1f };
        yield return new object[] { 1f, 0f, 1f, 1f, 1f, 0f, -1f, 1f };
        yield return new object[] { 1f, 0f, 1f, 1f, 1f, 0f, inf, 1f };
        yield return new object[] { 1f, 0f, 1f, 1f, 1f, 0f, 0f, 0f };
        yield return new object[] { 1f, 0f, 1f, 1f, 1f, 0f, 0f, nan };
        // Several wrong at once: the FIRST refusal wins on both, so the order of the checks is pinned too.
        yield return new object[] { 0f, 0f, -1f, 0f, 0f, -1f, -1f, 0f };
        yield return new object[] { 1f, 0f, -1f, 0f, 0f, -1f, -1f, 0f };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_request_refuses_and_normalizes_exactly_as_the_commitment_does(float dx, float dy,
        float horizontal, float vertical, float gravity, float preparation, float recovery, float timeout)
    {
        var direction = new Vector2(dx, dy);
        Exception? commitmentRefusal = Record.Exception(() =>
            new MovementCommitment(1u, direction, horizontal, vertical, gravity, preparation, recovery, timeout));
        MovementCommitmentRequest request = default;
        Exception? requestRefusal = Record.Exception(() =>
            request = new MovementCommitmentRequest(direction, horizontal, vertical, preparation, recovery, timeout,
                gravity));

        if (commitmentRefusal is null)
        {
            Assert.Null(requestRefusal);
            var commitment = new MovementCommitment(1u, direction, horizontal, vertical, gravity, preparation,
                recovery, timeout);
            Assert.Equal(commitment.Direction, request.Direction);
            return;
        }

        Assert.NotNull(requestRefusal);
        Assert.Equal(commitmentRefusal.GetType(), requestRefusal!.GetType());
        Assert.Equal(((ArgumentException)commitmentRefusal).ParamName, ((ArgumentException)requestRefusal).ParamName);
        Assert.Equal(commitmentRefusal.Message, requestRefusal.Message);
    }

    // A default request never ran a constructor, so the float heads still refuse it at the door through the real
    // MovementCommitment rather than starting a commitment with a zero direction.
    [Fact]
    public void A_default_request_is_still_refused_by_the_float_heads()
    {
        var hub = new KhaozEngine.Netcode.InMemoryTransportHub();
        var server = new WorldServer(hub.Server, new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 2 },
            (_, _) => 0f, MoveTuning.Default);

        Assert.Throws<ArgumentException>(() => server.BeginMovementCommitment(PlayerRef.Slot(0), default));
    }
}
