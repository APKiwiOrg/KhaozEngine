using System;
using KhaozEngine.App;
using KhaozEngine.Diagnostics;
using KhaozEngine.Tests.App;
using Xunit;

namespace KhaozEngine.Tests.Logging;

internal static class AmbientFacadeAllocationProbe
{
    public static long[] Run(bool includeScopes)
    {
        var catalog = new DictionaryCatalog().Add("scope.key", "process");
        LocalizationContext.Catalog = catalog;
        Log.Configure(new LoggerOptions { Synchronous = true, MinimumLevel = LogLevel.Info, DefaultCategory = "process" });
        var logger = Log.For<AmbientFacadeAllocationTests>();
        var defaults = Log.Get("");
        LocalizedText text = new StringId("scope.key");
        var bytes = new long[includeScopes ? 6 : 2];
        try
        {
            Measure(0, "process");
            if (includeScopes)
            {
                using var manager = new LogManager(new LoggerOptions
                { Synchronous = true, MinimumLevel = LogLevel.Info, DefaultCategory = "scoped" });
                using (LocalizationContext.Use(new DictionaryCatalog().Add("scope.key", "scoped")))
                using (Log.Use(manager))
                    Measure(2, "scoped");
                Measure(4, "process");
            }
            return bytes;
        }
        finally { Log.Shutdown(); LocalizationContext.Catalog = null; }

        void Measure(int offset, string expected)
        {
            string resolved = "";
            string category = "";
            bool enabled = false;
            Action resolve = () =>
            {
                for (int i = 0; i < 1024; i++) resolved = text.Resolve();
            };
            Action log = () =>
            {
                for (int i = 0; i < 1024; i++)
                {
                    enabled = logger.IsEnabled(LogLevel.Info);
                    logger.Debug("filtered");
                    logger.Info("enabled with no sinks");
                    category = defaults.Category;
                }
            };
            resolve();
            log();
            bytes[offset] = AllocationMeasurement.Measure("LocalizedText.Resolve " + expected, resolve);
            bytes[offset + 1] = AllocationMeasurement.Measure("cached ambient logger " + expected, log);
            Assert.Equal(expected, resolved);
            Assert.Equal(expected, category);
            Assert.True(enabled);
        }
    }

}
