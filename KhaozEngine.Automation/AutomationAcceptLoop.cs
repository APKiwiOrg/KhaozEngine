using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace KhaozEngine.Automation
{
    /// <summary>
    /// Runs the listener's accept boundary. Interrupted accepts resume only while running, shutdown is quiet,
    /// and every terminal path signals completion without changing established clients' independent lifetimes.
    /// </summary>
    sealed class AutomationAcceptLoop(
        TcpListener listener, Func<TcpListener, TcpClient> accept, Func<bool> stopping,
        Action<TcpClient> startClient, Action<string, Exception?> report)
    {
        readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Completion => _completion.Task;

        internal void Run()
        {
            try
            {
                while (!stopping())
                {
                    TcpClient client;
                    try
                    {
                        client = accept(listener);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
                    {
                        if (stopping()) return;
                        continue;
                    }
                    startClient(client);
                }
            }
            catch (Exception ex)
            {
                if (!stopping())
                    report("automation: the accept loop stopped, so the endpoint is gone for the rest of this run", ex);
            }
            finally
            {
                _completion.TrySetResult();
            }
        }
    }
}
