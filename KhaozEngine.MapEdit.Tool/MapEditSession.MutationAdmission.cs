using System;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEdit;

public sealed partial class MapEditSession
{
    /// <summary>Runs a resource publication under the session lock only after its mutation callback closes.</summary>
    internal T WithNativePublication<T>(string operation, Func<NativeAssetContext, T> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        return WithNativeAssets(context =>
        {
            RequireClosedNativeMutation(operation, nativePublication: true);
            return fn(context);
        });
    }

    // The lock is reentrant, so it does not keep a callback from publishing or replacing the live session.
    // Each publication entry point checks before staging, writing storage or changing session state.
    void RequireClosedNativeMutation(string operation, bool nativePublication = false)
    {
        if (!_nativeMutation) return;
        const string message = "Publication must wait until the native mutation callback closes.";
        if (nativePublication)
            throw new MapDocumentException(MapDocumentRefusal.ToolPublicationDuringMutationUnsupported, message)
            { DocumentId = _doc?.Id, Operation = operation };
        throw new MapSessionException(MapDocumentRefusal.ToolPublicationDuringMutationUnsupported, message)
        { DocumentId = _doc?.Id, Operation = operation };
    }
}
