using System;
using System.Text;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Typed format 2 failures: the four appended disconnect reasons, their stable tokens, the refusal mapping a client
/// reads, the admission gate a restarting server closes, and the epoch allocator that never wraps.
/// </summary>
public class ReplicationFailureTests
{
    [Fact]
    public void NewReasonsAreAppendedSoOrdinalsStayStable()
    {
        Assert.Equal(11, (int)DisconnectReason.ContentClientTooOld);
        Assert.Equal(12, (int)DisconnectReason.ReplicationPolicyRefused);
        Assert.Equal(13, (int)DisconnectReason.ReplicationRecoveryFailed);
        Assert.Equal(14, (int)DisconnectReason.ReplicationCapacityExceeded);
        Assert.Equal(15, (int)DisconnectReason.ReplicationRestart);
        Assert.Equal(16, Enum.GetValues<DisconnectReason>().Length);
    }

    [Fact]
    public void StableTokens()
    {
        Assert.Equal("ke:replication-policy-refused", ReplicationFailure.PolicyRefusedToken);
        Assert.Equal("ke:replication-recovery-failed", ReplicationFailure.RecoveryFailedToken);
        Assert.Equal("ke:replication-capacity-exceeded", ReplicationFailure.CapacityExceededToken);
        Assert.Equal("ke:replication-restart", ReplicationFailure.RestartToken);
        Assert.Equal(ReplicationFailure.PolicyRefusedToken,
            ReplicationFailure.TokenFor(DisconnectReason.ReplicationPolicyRefused));
        Assert.Equal(ReplicationFailure.RecoveryFailedToken,
            ReplicationFailure.TokenFor(DisconnectReason.ReplicationRecoveryFailed));
        Assert.Equal(ReplicationFailure.CapacityExceededToken,
            ReplicationFailure.TokenFor(DisconnectReason.ReplicationCapacityExceeded));
        Assert.Equal(ReplicationFailure.RestartToken, ReplicationFailure.TokenFor(DisconnectReason.ReplicationRestart));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplicationFailure.TokenFor(DisconnectReason.Timeout));
    }

    [Fact]
    public void ReplicationTokensMapToTypedReasons()
    {
        AssertRefusal(ReplicationFailure.PolicyRefusedToken, DisconnectReason.ReplicationPolicyRefused,
            ConnectRefusal.RetryRule.Never);
        AssertRefusal(ReplicationFailure.RecoveryFailedToken, DisconnectReason.ReplicationRecoveryFailed,
            ConnectRefusal.RetryRule.Never);
        AssertRefusal(ReplicationFailure.CapacityExceededToken, DisconnectReason.ReplicationCapacityExceeded,
            ConnectRefusal.RetryRule.Never);
        AssertRefusal(ReplicationFailure.RestartToken, DisconnectReason.ReplicationRestart,
            ConnectRefusal.RetryRule.Backoff);

        // The restart rule is the one AlreadySignedIn uses, so it reconnects even with retry-on-reject off.
        Assert.Equal(ConnectRefusal.Read(SessionRejectReason.AlreadySignedIn).Retry,
            ConnectRefusal.Read(ReplicationFailure.RestartToken).Retry);
        Assert.True(ConnectRefusal.Read(ReplicationFailure.RestartToken).AllowsReconnect(retryOnReject: false));

        // Near misses stay ordinary rejected tokens.
        Assert.Equal(DisconnectReason.RejectedToken, ConnectRefusal.Read("ke:replication-restart:x").Reason);
        Assert.Equal(DisconnectReason.RejectedToken, ConnectRefusal.Read("KE:REPLICATION-RESTART").Reason);
    }

    private static void AssertRefusal(string token, DisconnectReason reason, ConnectRefusal.RetryRule retry)
    {
        ConnectRefusal refusal = ConnectRefusal.Read(token);
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(string.Empty, refusal.Detail);
        Assert.Equal(retry, refusal.Retry);
    }

    [Theory]
    [InlineData(DisconnectReason.ReplicationPolicyRefused)]
    [InlineData(DisconnectReason.ReplicationRecoveryFailed)]
    [InlineData(DisconnectReason.ReplicationCapacityExceeded)]
    public void TypedRefusalsAreTerminal(DisconnectReason reason)
    {
        var failure = new ReplicationFailure(reason, "detail");

        Assert.True(failure.IsTerminal);
        Assert.Equal(ReplicationFailure.TokenFor(reason), failure.Token);
        ConnectRefusal refusal = ConnectRefusal.Read(failure.Token);
        Assert.False(refusal.AllowsReconnect(retryOnReject: true));
        Assert.False(refusal.AllowsReconnect(retryOnReject: false));
    }

    [Fact]
    public void RestartIsNotTerminal()
    {
        var failure = new ReplicationFailure(DisconnectReason.ReplicationRestart, string.Empty);

        Assert.False(failure.IsTerminal);
        Assert.True(ConnectRefusal.Read(failure.Token).AllowsReconnect(retryOnReject: false));
    }

    [Fact]
    public void AdmissionWrapperPreservesVerifiedClaims()
    {
        var inner = new ClaimsAuthenticator();
        var gate = new ReplicationAdmissionGate(WireGenerationAuthenticator.Install(inner));
        byte[] token = ProtocolHandshake.BuildClientToken(MoveProtocol.WireProtocolVersion, null,
            Encoding.UTF8.GetBytes("subject-7|Seven|key-7"));

        Assert.True(gate.IsOpen);
        Assert.True(gate.TryAuthenticate(token, out string subject, out string reject));
        Assert.Equal("subject-7", subject);
        Assert.Equal(string.Empty, reject);
        Assert.Equal("Seven", gate.ReadDisplayName(token));
        Assert.Equal("key-7", gate.ReadPersistenceKey(token));
        Assert.Equal(1, inner.Calls);

        // The wire gate inside still refuses a skewed generation with its own reason.
        byte[] skewed = ProtocolHandshake.BuildClientToken(MoveProtocol.WireProtocolVersion - 1, null,
            Encoding.UTF8.GetBytes("subject-7|Seven|key-7"));
        Assert.False(gate.TryAuthenticate(skewed, out _, out string skewReason));
        Assert.True(ProtocolHandshake.TryParseIncompatibleReason(skewReason, out _));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void ClosedGateRejectsWithRestartWithoutCallingInner()
    {
        var inner = new ClaimsAuthenticator();
        var gate = new ReplicationAdmissionGate(WireGenerationAuthenticator.Install(inner));
        byte[] token = ProtocolHandshake.BuildClientToken(MoveProtocol.WireProtocolVersion, null,
            Encoding.UTF8.GetBytes("subject-7|Seven|key-7"));

        gate.Close();

        Assert.False(gate.IsOpen);
        Assert.False(gate.TryAuthenticate(token, out string subject, out string reject));
        Assert.Equal(string.Empty, subject);
        Assert.Equal(ReplicationFailure.RestartToken, reject);
        Assert.Equal(0, inner.Calls);
        Assert.Equal(DisconnectReason.ReplicationRestart, ConnectRefusal.Read(reject).Reason);

        gate.Open();

        Assert.True(gate.TryAuthenticate(token, out subject, out _));
        Assert.Equal("subject-7", subject);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void GateWithoutClaimInterfacesReadsEmptyClaims()
    {
        var gate = new ReplicationAdmissionGate(new AllowAllAuthenticator());

        Assert.Equal(string.Empty, gate.ReadDisplayName(new byte[] { 1 }));
        Assert.Equal(string.Empty, gate.ReadPersistenceKey(new byte[] { 1 }));
        Assert.Throws<ArgumentNullException>(() => new ReplicationAdmissionGate(null!));
    }

    [Fact]
    public void EpochsStartAtOneAndIncrease()
    {
        var epochs = new ReplicationEpochAllocator();

        Assert.Equal(0UL, epochs.HighWater);
        Assert.True(epochs.TryNext(out ulong first));
        Assert.True(epochs.TryNext(out ulong second));
        Assert.Equal(1UL, first);
        Assert.Equal(2UL, second);
        Assert.Equal(2UL, epochs.HighWater);
    }

    [Fact]
    public void AllocatorRefusesAfterMaxValueRatherThanReusingZero()
    {
        var epochs = new ReplicationEpochAllocator();
        epochs.SeedForTest(ulong.MaxValue - 1);

        Assert.True(epochs.TryNext(out ulong last));
        Assert.Equal(ulong.MaxValue, last);
        Assert.False(epochs.TryNext(out ulong refused));
        Assert.Equal(0UL, refused);
        Assert.False(epochs.TryNext(out _));
        Assert.Equal(ulong.MaxValue, epochs.HighWater);
    }

    // Token "subject|name|key". Counts every TryAuthenticate call to prove the closed gate never reaches it.
    private sealed class ClaimsAuthenticator : IConnectionAuthenticator, IConnectionDisplayName,
        IConnectionPersistenceKey
    {
        internal int Calls { get; private set; }

        public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
        {
            Calls++;
            string[] parts = Encoding.UTF8.GetString(token).Split('|');
            if (parts.Length != 3)
            {
                subject = string.Empty;
                rejectReason = "bad-token";
                return false;
            }
            subject = parts[0];
            rejectReason = string.Empty;
            return true;
        }

        public string ReadDisplayName(ReadOnlySpan<byte> token) => Encoding.UTF8.GetString(token).Split('|')[1];

        public string ReadPersistenceKey(ReadOnlySpan<byte> token) => Encoding.UTF8.GetString(token).Split('|')[2];
    }
}
