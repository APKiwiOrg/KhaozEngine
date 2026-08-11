using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Serial collection for test classes that ARM or DISARM the process-global crash report
/// (<c>KhaozEngine.Diagnostics/CrashReport.cs</c>: <c>Install</c> subscribes a single static handler to
/// <see cref="System.AppDomain.UnhandledException"/> and stores the directory, process label and retention
/// count in statics, <c>Uninstall</c> tears it back down, and <c>IsInstalled</c> reads that same one slot for
/// the whole process). xUnit parallelizes across collections, so a class that installs, asserts and uninstalls
/// leaves a window in which any other class in the assembly sees an armed handler pointing at a directory it
/// did not choose, or sees its own arming disappear under it.
///
/// <para><c>DisableParallelization</c> is what closes it: a collection marked this way runs in its own
/// sequential phase with no other collection running, so while the ambient arming is being swapped nothing else
/// in the assembly is executing. The plain <c>[Collection("name")]</c> attribute alone does NOT do that. It only
/// serializes the classes carrying that same name against each other, and with no definition anywhere the name
/// is orphaned and the classes run in parallel with everything else. That is exactly how #349 sat open under a
/// collection attribute that looked like a fix, and this collection was the second orphan of that kind found in
/// the repo.</para>
///
/// <para>Membership rule: a class that calls <c>CrashReport.Install</c> or <c>CrashReport.Uninstall</c>, or that
/// asserts on <c>CrashReport.IsInstalled</c>, must be here, and must still restore the previous state (an
/// <c>Uninstall</c> in a <c>finally</c> or an <c>IDisposable</c>). Readers that never touch the arming stay out
/// of it and are covered anyway by the paragraph above.</para>
/// </summary>
[CollectionDefinition("CrashReportSerial", DisableParallelization = true)]
public sealed class CrashReportSerialCollection { }
