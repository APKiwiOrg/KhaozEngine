using System;
using System.Reflection;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Lifecycle bookkeeping only. An initially empty source and all physical mutation facts are caller premises.
public class CapsuleSweepEvidenceTests
{
    [Fact]
    public void FreshEvidenceBindsTheExactSourceAtGenerationZero()
    {
        var source = new Source(1);
        var proof = new Proof(source);
        Assert.True(proof.Current(source, 0));
        Assert.False(proof.Current(new Source(1), 0));
        Assert.False(proof.Current(source, 1));
        Assert.True(proof.Current(source, 0));
    }

    [Fact]
    public void BeginningMutationInvalidatesUntilItsAuditedCompletion()
    {
        object source = new();
        var proof = new Proof(source);
        object token = proof.Begin(1);
        Assert.False(proof.Current(source, 0));
        Assert.False(proof.Current(source, 1));
        Assert.True(proof.Complete(token, 1));
        Assert.True(proof.Current(source, 1));
        Assert.False(proof.Current(source, 0));
    }

    [Fact]
    public void AbandonedMutationCannotBeHealedByLaterSuccess()
    {
        object source = new();
        var proof = new Proof(source);
        proof.Begin(1);
        object later = proof.Begin(2);
        Assert.False(proof.Complete(later, 2));
        Assert.False(proof.Current(source, 2));
    }

    [Fact]
    public void MissingHookCannotBeHealedByTheNextKnownHook()
    {
        object source = new();
        var proof = new Proof(source);
        Assert.False(proof.Current(source, 1));
        Assert.False(proof.Complete(proof.Begin(2), 2));
        Assert.False(proof.Current(source, 2));
    }

    [Fact]
    public void IncompleteRecordsKeepEvidenceInvalidAcrossLaterMutations()
    {
        object source = new();
        var proof = new Proof(source);
        Assert.False(proof.Complete(proof.Begin(1), 1, recordsComplete: false));
        Assert.False(proof.Complete(proof.Begin(2), 2));
        Assert.False(proof.Current(source, 2));
    }

    [Fact]
    public void UnexpectedInterveningGenerationRefusesPublication()
    {
        object source = new();
        var proof = new Proof(source);
        Assert.False(proof.Complete(proof.Begin(1), 2));
        Assert.False(proof.Complete(proof.Begin(3), 3));
        Assert.False(proof.Current(source, 3));
    }

    [Fact]
    public void AnotherEvidenceInstanceCannotSupplyTheCompletionToken()
    {
        object source = new();
        var proof = new Proof(source);
        var other = new Proof(source);
        proof.Begin(1);
        Assert.False(proof.Complete(other.Begin(1), 1));
        Assert.False(proof.Current(source, 1));
    }

    [Fact]
    public void SourcesEqualByValueRemainDifferentOwners()
    {
        var source = new Source(1);
        var equal = new Source(1);
        Assert.Equal(source, equal);
        Assert.NotSame(source, equal);
        var proof = new Proof(source);
        var other = new Proof(equal);
        proof.Begin(1);
        Assert.False(proof.Complete(other.Begin(1), 1));
        Assert.False(proof.Current(source, 1));
    }

    [Fact]
    public void ACompletionTokenCannotBeReplayed()
    {
        object source = new();
        var proof = new Proof(source);
        object token = proof.Begin(1);
        Assert.True(proof.Complete(token, 1));
        Assert.False(proof.Complete(token, 1));
        Assert.False(proof.Current(source, 1));
    }

    [Fact]
    public void OldCompletionCannotOverwriteANewerPendingMutation()
    {
        object source = new();
        var proof = new Proof(source);
        object old = proof.Begin(1);
        object next = proof.Begin(2);
        Assert.False(proof.Complete(old, 2));
        Assert.False(proof.Complete(next, 2));
        Assert.False(proof.Current(source, 2));
    }

    [Fact]
    public void NonpositiveMutationGenerationDoesNotManufactureInitialEvidence()
    {
        object source = new();
        var proof = new Proof(source);
        Assert.False(proof.Complete(proof.Begin(0), 0));
        Assert.False(proof.Complete(proof.Begin(-1), -1));
        Assert.False(proof.Current(source, 0));
    }

    [Fact]
    public void MissingTokenCannotPublishOrPreserveEvidence()
    {
        object source = new();
        var proof = new Proof(source);
        Assert.False(proof.Complete(null, 0));
        Assert.False(proof.Current(source, 0));
    }

    sealed record Source(int Identity);

    sealed class Proof
    {
        readonly Type type;
        readonly object value;

        internal Proof(object source)
        {
            Type? found = typeof(BepuPhysicsWorld).Assembly.GetType("KhaozEngine.Physics.Bepu.CapsuleSweepEvidence");
            Assert.NotNull(found);
            type = found;
            object? instance = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [source], culture: null);
            Assert.NotNull(instance);
            value = instance;
        }

        object? Call(string name, params object?[] arguments)
        {
            MethodInfo? method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method.Invoke(value, arguments);
        }

        internal bool Current(object source, long generation) => (bool)Call("IsCurrent", source, generation)!;
        internal object Begin(long generation) => Call("BeginMutation", generation)!;
        internal bool Complete(object? token, long generation, bool recordsComplete = true) =>
            (bool)Call("CompleteMutation", token, generation, recordsComplete)!;
    }
}
