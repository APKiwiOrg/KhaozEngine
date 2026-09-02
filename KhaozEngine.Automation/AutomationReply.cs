using System.Text.Json.Nodes;

namespace KhaozEngine.Automation
{
    /// <summary>
    /// One reply line. The wire form is a single JSON object per line, carrying the request's <c>id</c>, the
    /// <c>frame</c> the command took effect on, and exactly one of <c>ok</c> or <c>error</c>:
    /// <c>{"id":7,"frame":412,"ok":{}}</c> or <c>{"id":7,"frame":412,"error":"unknown verb 'walk_to'"}</c>.
    /// <para>
    /// The frame number is the point of the protocol. A report can say "the panel was still open at frame 412"
    /// rather than "it seemed to still be open", which is what makes a stepped session reproducible.
    /// </para>
    /// </summary>
    public sealed class AutomationReply
    {
        /// <summary>The request's correlation id, echoed unchanged.</summary>
        public long Id { get; }

        /// <summary>The frame the command took effect on, counted by the host's pump from 1.</summary>
        public long Frame { get; }

        /// <summary>The success payload, or null on a failure (and on a success whose command returns nothing).</summary>
        public JsonNode? Ok { get; }

        /// <summary>The failure text, or null on a success.</summary>
        public string? Error { get; }

        /// <summary>True when this reply carries no <see cref="Error"/>.</summary>
        public bool IsSuccess => Error is null;

        AutomationReply(long id, long frame, JsonNode? ok, string? error)
        {
            Id = id;
            Frame = frame;
            Ok = ok;
            Error = error;
        }

        /// <summary>A success reply. <paramref name="ok"/> may be null, which serializes as <c>"ok":null</c>.</summary>
        public static AutomationReply Success(long id, long frame, JsonNode? ok) => new(id, frame, ok, null);

        /// <summary>A failure reply carrying <paramref name="error"/> as its text.</summary>
        public static AutomationReply Failure(long id, long frame, string error) => new(id, frame, null, error);

        /// <summary>Serialize to the single wire line (no trailing newline: the writer adds it).</summary>
        public string ToJsonLine()
        {
            var node = new JsonObject
            {
                ["id"] = Id,
                ["frame"] = Frame,
            };
            // DeepClone because a JsonNode belongs to at most one parent: a game verb that returns a cached
            // document would throw on its second reply if the node itself were attached here.
            if (Error is null) node["ok"] = Ok?.DeepClone(); else node["error"] = Error;
            return node.ToJsonString();
        }
    }
}
