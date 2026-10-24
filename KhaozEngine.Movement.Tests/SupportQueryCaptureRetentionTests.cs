using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class SupportQueryCaptureRetentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetentionBalanceSurvivesNestedAndDisabledScopeBoundaries(bool enabled)
    {
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(enabled));
        try
        {
            using var fixture = new ContactNavigationReuseFixture();
            using var proof = fixture.Proof();
            var source = proof.BeginSource(ContactNavigationReuseFixture.Source());
            Assert.True(proof.TryEdge(fixture.From, fixture.To));
            var live = SupportQueryDiagnostics.Retention;
            Assert.True(live.Keys > 0);
            Assert.True(live.Bytes > 0);
            using (SupportQueryCapture.Begin("release")) source.Dispose();
            ContactNavigationReuseFixture.Released();
            Assert.Equal(live.PeakKeys, SupportQueryDiagnostics.Retention.PeakKeys);
            Assert.Equal(live.PeakBytes, SupportQueryDiagnostics.Retention.PeakBytes);
            source = proof.BeginSource(ContactNavigationReuseFixture.Source(1));
            using (SupportQueryCapture.Begin("store"))
            {
                Assert.True(proof.TryEdge(fixture.From, fixture.To));
                live = SupportQueryDiagnostics.Retention;
                using (SupportQueryCapture.Begin("steady"))
                {
                    Assert.Equal(live.Keys, SupportQueryDiagnostics.Retention.Keys);
                    Assert.Equal(live.Bytes, SupportQueryDiagnostics.Retention.Bytes);
                }
            }
            Assert.Equal(enabled, SupportQueryDiagnostics.Enabled);
            Assert.Equal(live.Keys, SupportQueryDiagnostics.Retention.Keys);
            Assert.Equal(live.Bytes, SupportQueryDiagnostics.Retention.Bytes);
            source.Dispose();
            ContactNavigationReuseFixture.Released();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Fact]
    public void RetentionPeaksDistinguishCompleteAndPartialLifetimes()
    {
        using var fixture = new ContactNavigationReuseFixture();
        using var proof = fixture.Proof();
        using var capture = SupportQueryCapture.Begin("complete");
        var source = proof.BeginSource(ContactNavigationReuseFixture.Source());
        Assert.True(proof.TryEdge(fixture.From, fixture.To));
        Assert.False(capture.Snapshot().Total.ProofReuse.HasCompleteLifetimes);
        using (var steady = SupportQueryCapture.Begin("steady"))
        {
            var oracle = SupportQueryDiagnostics.Retention;
            Assert.Equal(0, oracle.PeakKeys);
            Assert.Equal(0, oracle.PeakBytes);
            Assert.False(steady.Snapshot().Total.ProofReuse.HasCompleteLifetimes);
            Assert.Equal(oracle.PeakBytes, steady.Snapshot().Total.ProofReuse.PeakBytes);
        }
        source.Dispose();
        ContactNavigationReuseFixture.Released();
        Assert.False(capture.Snapshot().Total.ProofReuse.HasCompleteLifetimes);
        capture.Dispose();

        using var whole = SupportQueryCapture.Begin("whole");
        using (proof.BeginSource(ContactNavigationReuseFixture.Source(1)))
        {
            Assert.True(proof.TryEdge(fixture.From, fixture.To));
            var oracle = SupportQueryDiagnostics.Retention;
            Assert.True(oracle.PeakKeys > 0);
            Assert.True(oracle.PeakBytes > 0);
            Assert.Equal(oracle.PeakKeys, whole.Snapshot().Total.ProofReuse.PeakKeys);
            Assert.Equal(oracle.PeakBytes, whole.Snapshot().Total.ProofReuse.PeakBytes);
        }
        ContactNavigationReuseFixture.Released();
        var peaks = SupportQueryDiagnostics.Retention;
        whole.Dispose();
        Assert.True(whole.Snapshot().Total.ProofReuse.HasCompleteLifetimes);
        Assert.Equal(peaks.PeakKeys, whole.Snapshot().Total.ProofReuse.PeakKeys);
        Assert.Equal(peaks.PeakBytes, whole.Snapshot().Total.ProofReuse.PeakBytes);
    }
}
