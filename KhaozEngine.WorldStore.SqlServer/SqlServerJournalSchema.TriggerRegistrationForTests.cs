using System;
using System.Collections.Generic;

namespace KhaozEngine.WorldStore.SqlServer;

internal static partial class SqlServerJournalSchema
{
    private sealed class TriggerRegistrationForTests : IDisposable
    {
        private readonly HashSet<string> original = new(ExpectedTriggers, StringComparer.Ordinal);
        private bool disposed;

        internal TriggerRegistrationForTests(string signature)
        {
            if (!ExpectedTriggers.Add(signature))
                throw new InvalidOperationException("The test probe must add only its own trigger expectation.");
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ExpectedTriggers.Clear();
            ExpectedTriggers.UnionWith(original);
        }
    }
}
