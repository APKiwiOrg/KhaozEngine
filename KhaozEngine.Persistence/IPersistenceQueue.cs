namespace KhaozEngine.Persistence;

public interface IPersistenceQueue
{
    // Enqueue a write of json to path; rapid repeats to the same path coalesce
    // (per-path, last-writer-wins).
    void Enqueue(string path, string json);
    // Flush all pending writes synchronously (e.g. on shutdown).
    void Flush();
}
