using System;
using System.Linq.Expressions;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.Gpu;

internal readonly record struct CoveragePerfSubmissionCounts(int Participants, int Consumers);

/// <summary>Read actual production registration state without adding an engine API or a recording wrapper.</summary>
internal static class CoveragePerfSubmissionProbe
{
    // Compile and initialize during session setup. Frame sampling performs only cached field reads,
    // with no per-frame reflection, boxing, allocation or participant/consumer registration.
    static readonly Func<Frame, CoveragePerfSubmissionCounts> Reader = CreateReader();
    internal static CoveragePerfSubmissionCounts Capture(Frame frame) => Reader(frame);

    static Func<Frame, CoveragePerfSubmissionCounts> CreateReader()
    {
        var frame = Expression.Parameter(typeof(Frame), "frame");
        var submission = Expression.Variable(typeof(GpuFrameSubmission), "submission");
        var current = Expression.Field(submission, "current");
        var slots = Expression.Field(submission, "slots");
        var consumers = Expression.Condition(Expression.LessThan(current, Expression.Constant(0)), Expression.Constant(0),
            Expression.Field(Expression.ArrayIndex(slots, current), "Count"));
        var counts = Expression.New(typeof(CoveragePerfSubmissionCounts).GetConstructor([typeof(int), typeof(int)])!,
            Expression.Field(submission, "participantCount"), consumers);
        return Expression.Lambda<Func<Frame, CoveragePerfSubmissionCounts>>(Expression.Block([submission],
            Expression.Assign(submission, Expression.Field(frame, "_submission")),
            Expression.Condition(Expression.Equal(submission, Expression.Constant(null, typeof(GpuFrameSubmission))),
                Expression.Default(typeof(CoveragePerfSubmissionCounts)), counts)), frame).Compile();
    }
}
