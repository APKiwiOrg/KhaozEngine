using System;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.Tests.Movement.Research;

internal static class ProofChildAcceptance
{
    internal static void Retain(string directory, ChildAttempt attempt)
    {
        try
        {
            attempt.Result = ProofJson.ReadChild(Path.Combine(directory, attempt.Label + ".json"));
            attempt.EvidenceReadFailure = null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            attempt.EvidenceReadFailure = error.ToString();
        }
    }

    internal static void RequireCompleted(ChildAttempt attempt)
    {
        if (attempt.TimedOut || attempt.ExitStatus != 0)
            throw new InvalidOperationException("Child " + attempt.Label +
                " failed or exceeded its three-minute limit. No retry is allowed.");
        ChildResult result = attempt.Result
            ?? throw new InvalidDataException("Child evidence is missing or malformed. " + attempt.EvidenceReadFailure);
        if (attempt.EvidenceReadFailure is not null)
            throw new InvalidDataException("Child evidence could not be read. " + attempt.EvidenceReadFailure);
        if (result.Status != "Succeeded" || result.Stage != "Completed" || result.Failure is not null ||
            result.Metrics is null || result.Environment is null || result.GraphValidation is null ||
            !result.GraphValidation.Passed || result.CanonicalRoundTrip != true || result.ScopeReleasedAfterBuild != true)
            throw new InvalidOperationException("Child " + attempt.Label + " has incomplete or failed evidence. " +
                "Status=" + result.Status + ", Stage=" + result.Stage + ". No retry is allowed.");
    }
}
