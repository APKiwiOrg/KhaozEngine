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
/// both with the same exception and parameter, and admitted by both with the same normalized direction.
/// </summary>
public class MovementCommitmentRequestParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        float nan = float.NaN, inf = float.PositiveInfinity;
        // direction x, direction y, horizontal, vertical, gravity, preparation, recovery, timeout
        yield return new object[] { 1f, 0f, 3f, 5f, 25f, 0f, 0f, 5f };
        yield return new object[] { 3f, -4f, 0f, 0.1f, 9.8f, 0.25f, 0.5f, 1f };
        yield return new object[] { float.MaxValue, float.MaxValue, 3f, 5f, 25f, 0f, 0f, 5f };
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
            Assert.InRange(request.Direction.Length(), 0.99999f, 1.00001f);
            Assert.False(request.IsGroundTravel);
            Assert.Equal(0f, request.DistanceRemaining);
            Assert.Equal(MovementCommitmentKind.Ballistic, commitment.Kind);
            return;
        }

        Assert.NotNull(requestRefusal);
        Assert.Equal(commitmentRefusal.GetType(), requestRefusal!.GetType());
        Assert.Equal(((ArgumentException)commitmentRefusal).ParamName, ((ArgumentException)requestRefusal).ParamName);
    }

    public static IEnumerable<object[]> GroundCases()
    {
        // direction x/y, speed, distance, preparation, recovery, timeout, refusal parameter
        yield return [3f, -4f, 3f, 1f, 0f, 0f, 5f, ""];
        yield return [float.MaxValue, float.MaxValue, 3f, 1f, 0.2f, 0.3f, 5f, ""];
        yield return [float.Epsilon, 0f, 3f, 1f, 0f, 0f, 5f, ""];
        yield return [0f, 0f, 3f, 1f, 0f, 0f, 5f, "direction"];
        yield return [float.NaN, 1f, 3f, 1f, 0f, 0f, 5f, "direction"];
        yield return [1f, float.PositiveInfinity, 3f, 1f, 0f, 0f, 5f, "direction"];
        yield return [1f, 0f, 0f, 1f, 0f, 0f, 5f, "speed"];
        yield return [1f, 0f, -1f, 1f, 0f, 0f, 5f, "speed"];
        yield return [1f, 0f, float.PositiveInfinity, 1f, 0f, 0f, 5f, "speed"];
        yield return [1f, 0f, 3f, 0f, 0f, 0f, 5f, "distance"];
        yield return [1f, 0f, 3f, -1f, 0f, 0f, 5f, "distance"];
        yield return [1f, 0f, 3f, float.NaN, 0f, 0f, 5f, "distance"];
        yield return [1f, 0f, 3f, 1f, -1f, 0f, 5f, "preparationSeconds"];
        yield return [1f, 0f, 3f, 1f, float.PositiveInfinity, 0f, 5f, "preparationSeconds"];
        yield return [1f, 0f, 3f, 1f, 0f, -1f, 5f, "recoverySeconds"];
        yield return [1f, 0f, 3f, 1f, 0f, float.NaN, 5f, "recoverySeconds"];
        yield return [1f, 0f, 3f, 1f, 0f, 0f, 0f, "timeoutSeconds"];
        yield return [1f, 0f, 3f, 1f, 0f, 0f, float.PositiveInfinity, "timeoutSeconds"];
    }

    [Theory]
    [MemberData(nameof(GroundCases))]
    public void Ground_factories_share_validation_and_normalization(float x, float y, float speed, float distance,
        float preparation, float recovery, float timeout, string parameter)
    {
        Vector2 direction = new(x, y);
        MovementCommitment state = default;
        MovementCommitmentRequest request = default;
        Exception? stateError = Record.Exception(() => state = MovementCommitment.ForGroundTravel(1, direction,
            speed, distance, preparation, recovery, timeout));
        Exception? requestError = Record.Exception(() => request = MovementCommitmentRequest.ForGroundTravel(direction,
            speed, distance, preparation, recovery, timeout));
        if (parameter.Length > 0)
        {
            var stateArgument = Assert.IsAssignableFrom<ArgumentException>(stateError);
            var requestArgument = Assert.IsAssignableFrom<ArgumentException>(requestError);
            Assert.Equal(parameter, stateArgument.ParamName);
            Assert.Equal(parameter, requestArgument.ParamName);
            Assert.Equal(stateArgument.GetType(), requestArgument.GetType());
            return;
        }
        Assert.Null(stateError);
        Assert.Null(requestError);
        Assert.True(request.IsGroundTravel);
        Assert.Equal(MovementCommitmentKind.Ground, state.Kind);
        Assert.InRange(state.Direction.Length(), 0.99999f, 1.00001f);
        Assert.Equal(state.Direction, request.Direction);
        Assert.Equal(distance, request.DistanceRemaining);
        Assert.Equal(state, request.Start(1));
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
