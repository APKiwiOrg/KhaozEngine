using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace KhaozEngine.Tests;

/// <summary>xUnit's default framework with test reporting delivered synchronously on the thread that reports it.
/// The default bus hands every message to one reporter thread, and the runner behind it serializes each result.
/// Under CPU contention that thread falls behind the parallel collections and drains megabytes of backlog while
/// a later serial collection measures allocation inside a process-wide no-GC budget (#1391, #1449).
/// Delivering on the reporting thread leaves nothing queued once a collection finishes.</summary>
public sealed class SerializedReportingTestFramework : XunitTestFramework
{
    public SerializedReportingTestFramework(IMessageSink messageSink) : base(messageSink) { }

    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    sealed class Executor : XunitTestFrameworkExecutor
    {
        internal Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider,
            IMessageSink diagnosticMessageSink) : base(assemblyName, sourceInformationProvider, diagnosticMessageSink) { }

        // Mirrors the base override, which xUnit declares async void.
        protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink,
            ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new Runner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
            await runner.RunAsync();
        }
    }

    sealed class Runner : XunitTestAssemblyRunner
    {
        internal Runner(ITestAssembly testAssembly, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnosticMessageSink,
            IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
            : base(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions) { }

        protected override IMessageBus CreateMessageBus() =>
            SerializedMessageBus.ForAssembly(ExecutionMessageSink, ExecutionOptions.GetValue<bool>("xunit.execution.StopOnFail"));

        protected override async Task<RunSummary> RunTestCollectionAsync(IMessageBus messageBus, ITestCollection testCollection,
            IEnumerable<IXunitTestCase> testCases, CancellationTokenSource cancellationTokenSource)
        {
            bool serial = AssemblyRunsSerially();
            CollectionIsolation.AssemblySerial = serial;
            CollectionIsolation.RunsAlone = serial || DisablesParallelization(testCollection);
            return await base.RunTestCollectionAsync(messageBus, testCollection, testCases, cancellationTokenSource);
        }

        // The same two sources xUnit reads, with the runner option winning over the assembly attribute.
        bool AssemblyRunsSerially() =>
            ExecutionOptions.GetValue<bool?>("xunit.execution.DisableParallelization") ??
            TestAssembly.Assembly.GetCustomAttributes(typeof(CollectionBehaviorAttribute)).SingleOrDefault()
                ?.GetNamedArgument<bool>(nameof(CollectionBehaviorAttribute.DisableTestParallelization)) ?? false;

        static bool DisablesParallelization(ITestCollection testCollection) =>
            testCollection.CollectionDefinition?.GetCustomAttributes(typeof(CollectionDefinitionAttribute)).SingleOrDefault()
                ?.GetNamedArgument<bool>(nameof(CollectionDefinitionAttribute.DisableParallelization)) == true;
    }
}

/// <summary>Delivers each message on the queueing thread, one delivery at a time across threads.
/// xUnit's own <see cref="SynchronousMessageBus"/> assumes a single queueing thread, so the lock extends it to
/// parallel collections and keeps the runner's sinks single-threaded as the queued bus did.</summary>
internal sealed class SerializedMessageBus : IMessageBus
{
    const string DeliveredKey = "KhaozEngine.Tests.SerializedMessageBus.Delivered";
    readonly SynchronousMessageBus inner;
    readonly object gate = new();
    readonly StrongBox<long> delivered;

    internal SerializedMessageBus(IMessageSink messageSink, bool stopOnFail) : this(messageSink, stopOnFail, new()) { }

    SerializedMessageBus(IMessageSink messageSink, bool stopOnFail, StrongBox<long> delivered)
    {
        inner = new SynchronousMessageBus(messageSink, stopOnFail);
        this.delivered = delivered;
    }

    /// <summary>Messages the bus reporting this assembly's run has delivered, or null under another framework.
    /// Published through <see cref="AppContext"/> so a copy of the assembly in another load context reads it too.</summary>
    internal static long? AssemblyDelivered =>
        AppContext.GetData(DeliveredKey) is StrongBox<long> count ? Interlocked.Read(ref count.Value) : null;

    internal static SerializedMessageBus ForAssembly(IMessageSink messageSink, bool stopOnFail)
    {
        var count = new StrongBox<long>();
        AppContext.SetData(DeliveredKey, count);
        return new SerializedMessageBus(messageSink, stopOnFail, count);
    }

    public bool QueueMessage(IMessageSinkMessage message)
    {
        lock (gate)
        {
            Interlocked.Increment(ref delivered.Value);
            return inner.QueueMessage(message);
        }
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>Whether the test collection on this execution flow runs with no other collection beside it.</summary>
internal static class CollectionIsolation
{
    const string Key = "KhaozEngine.Tests.CollectionIsolation.RunsAlone";

    // One flow-local flag for the process, published through AppContext, so a copy of the assembly loaded into
    // another load context reads the value the runner set. The runner publishes it before any test runs.
    static readonly AsyncLocal<bool> runsAlone = AppContext.GetData(Key) as AsyncLocal<bool> ?? Publish();

    static AsyncLocal<bool> Publish()
    {
        var flag = new AsyncLocal<bool>();
        AppContext.SetData(Key, flag);
        return flag;
    }

    internal static bool RunsAlone
    {
        get => runsAlone.Value;
        set => runsAlone.Value = value;
    }

    const string SerialKey = "KhaozEngine.Tests.CollectionIsolation.AssemblySerial";

    /// <summary>Whether the runner runs this assembly's collections one at a time, which isolates every collection.
    /// The Vulkan full suite does, through xUnit.ParallelizeTestCollections=false.</summary>
    internal static bool AssemblySerial
    {
        get => AppContext.GetData(SerialKey) is true;
        set => AppContext.SetData(SerialKey, value);
    }
}
