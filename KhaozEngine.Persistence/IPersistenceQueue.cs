namespace KhaozEngine.Persistence;

public interface IPersistenceQueue
{
    void Enqueue(string path, string json);   // per-path coalescing, last-writer-wins
    void Flush();                              // flush pending writes (e.g. on shutdown)
}
