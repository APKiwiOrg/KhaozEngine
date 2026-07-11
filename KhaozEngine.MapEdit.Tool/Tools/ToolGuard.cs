using System;
using ModelContextProtocol;

namespace KhaozEngine.MapEdit.Tools;

/// <summary>The one error-mapping choke point shared by every tool class. The MCP SDK passes an
/// <see cref="McpException"/> message to the client verbatim but masks every other exception behind a generic
/// message, so each adapter runs its delegating call through <see cref="Guard{T}"/>: an
/// <see cref="McpException"/> already thrown by a lower layer flows on untouched, and any other exception is
/// rewrapped as an <see cref="McpException"/> carrying its precise message. That keeps the service layer free to
/// throw ordinary, well-worded exceptions (MapDocumentException, InvalidOperationException, ArgumentException)
/// while the client still sees the real reason a verb failed.</summary>
internal static class ToolGuard
{
    /// <summary>Runs <paramref name="fn"/> and returns its result, translating any thrown exception other than an
    /// <see cref="McpException"/> into one so its message reaches the client.</summary>
    public static T Guard<T>(Func<T> fn)
    {
        try
        {
            return fn();
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
