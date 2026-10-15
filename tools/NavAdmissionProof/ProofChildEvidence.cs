using System;
using System.IO;
using System.Security.Cryptography;

namespace KhaozEngine.Tests.Movement.Research;

internal sealed class ProofChildEvidence
{
    private readonly string resultPath;
    private readonly string bakePath;
    internal ChildResult Result { get; }

    internal ProofChildEvidence(string directory, ChildResult result)
    {
        Result = result;
        resultPath = Path.Combine(directory, result.Label + ".json");
        bakePath = Path.Combine(directory, result.Label + ".kenb");
        if (File.Exists(resultPath) || File.Exists(bakePath))
            throw new IOException("Refusing to overwrite child evidence.");
        Save();
    }

    internal void Run(Action<ProofChildEvidence> work)
    {
        try
        {
            work(this);
            Result.Stage = "Completed";
            Result.Status = "Succeeded";
            Save();
        }
        catch (Exception error)
        {
            Result.Status = "Failed";
            Result.Failure = error.ToString();
            try { Save(); }
            catch (Exception retentionError)
            {
                throw new AggregateException("Child failed and its failure checkpoint could not be retained.",
                    error, retentionError);
            }
            throw;
        }
    }

    internal void Begin(string stage)
    {
        Result.Stage = stage;
        Save();
    }

    internal void RecordMeasurement(ProfileMetrics metrics)
    {
        Result.Metrics = metrics;
        Save();
    }

    internal void CheckGraph(GraphAcceptance validation)
    {
        Result.GraphValidation = validation;
        Begin("GraphAcceptance");
        validation.RequireAccepted();
    }

    internal void RecordCanonical(byte[] bytes)
    {
        using (var stream = new FileStream(bakePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        Result.KenbLength = bytes.Length;
        Result.KenbSha256 = Convert.ToHexString(SHA256.HashData(bytes));
        Save();
    }

    private void Save() => ProofJson.Write(resultPath, Result);
}
