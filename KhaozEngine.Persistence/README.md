# KhaozEngine.Persistence

Game-agnostic save/persistence helpers.

`SaveEncoder` wraps save JSON in a Base64 + HMAC-SHA256 envelope (`{prefix}:{hmac}:{base64}`) to
deter casual tampering. It is a deterrent, not real security: the HMAC key ships in the game binary.
Decoding is lenient (recovers the JSON even on an HMAC mismatch) and reports outcomes through the
engine logger.

```csharp
using System.Text;
using KhaozEngine.Diagnostics;   // ILogger / Log
using KhaozEngine.Persistence;

var encoder = new SaveEncoder(
    Encoding.UTF8.GetBytes("MyGame-SaveIntegrity-v1"),
    "MGSV1",
    Log.For<SaveEncoder>());

string onDisk = encoder.Encode(json);
string? loaded = encoder.Decode(onDisk);   // null only if not-our-format / malformed / corrupt
```
