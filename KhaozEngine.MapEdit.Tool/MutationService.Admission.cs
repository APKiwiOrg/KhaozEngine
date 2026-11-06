using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public sealed partial class MutationService
{
    /// <summary>The shared mutation choke point used by every verb below: applies <paramref name="command"/> to
    /// the open document, validates the result with <see cref="MapDocumentValidator"/>, and on any validation
    /// error reverts the command and throws <see cref="MapMutationException"/> with the first reason and all findings.
    /// <see cref="MutationResult.WorldChanged"/> mirrors the command's AffectsWorld flag, which also selects
    /// whether the session's cached terrain field is invalidated by this mutation.</summary>
    internal MutationResult Apply(EditorCommand command, string verb, string detail)
    {
        ArgumentNullException.ThrowIfNull(command);
        bool worldChanged = command.AffectsWorld;
        return session.Mutate((doc, registry) =>
        {
            if (doc.ResolverIdentity is not null)
            {
                session.ApplyNative(command, doc, registry);
                return new MutationResult(verb, detail, worldChanged);
            }
            command.Apply(doc);
            IReadOnlyList<MapDocumentFinding> errors = MapDocumentValidator.ValidateFindings(doc, registry);
            if (errors.Count > 0)
            {
                command.Revert(doc);
                throw MapMutationException.FromFindings(errors, "mutation rejected: " + string.Join("; ", errors.Select(e => e.Message)));
            }
            return new MutationResult(verb, detail, worldChanged);
        }, worldChanged);
    }

    /// <summary>Overload of <see cref="Apply(EditorCommand, string, string)"/> for verbs whose command needs a
    /// document read to construct (a precondition lookup, or a captured "old" value the command needs to be
    /// reversible). <paramref name="factory"/> runs INSIDE the <see cref="MapEditSession.Mutate{T}"/> callback,
    /// so the read, the apply, the validate, and any revert all happen under the same lock acquisition: no other
    /// call can mutate the document between the read and the apply. <paramref name="worldChanged"/> must equal
    /// the constructed command's <see cref="EditorCommand.AffectsWorld"/>. <see cref="MapEditSession.Mutate{T}"/>
    /// needs that flag before the callback runs (to decide whether to invalidate the cached field), so it cannot
    /// be read off the command itself, and a mismatch throws rather than silently mis-tagging the mutation.</summary>
    internal MutationResult Apply(Func<MapDocument, MapDocRegistry, EditorCommand> factory, string verb,
        Func<EditorCommand, string> detail, bool worldChanged)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(detail);
        return session.Mutate((doc, registry) =>
        {
            EditorCommand command = factory(doc, registry);
            bool affectsWorld = command.AffectsWorld;
            if (affectsWorld != worldChanged)
            {
                // Engine-owned callers pair each factory with its command's AffectsWorld value.
                throw new InvalidOperationException(
                    $"internal error: Apply was called with worldChanged={worldChanged} but " +
                    $"{command.GetType().Name}.AffectsWorld={affectsWorld}.");
            }

            if (doc.ResolverIdentity is not null)
            {
                string message = detail(command);
                session.ApplyNative(command, doc, registry);
                return new MutationResult(verb, message, worldChanged);
            }
            command.Apply(doc);
            IReadOnlyList<MapDocumentFinding> errors = MapDocumentValidator.ValidateFindings(doc, registry);
            if (errors.Count > 0)
            {
                command.Revert(doc);
                throw MapMutationException.FromFindings(errors, "mutation rejected: " + string.Join("; ", errors.Select(e => e.Message)));
            }
            return new MutationResult(verb, detail(command), worldChanged);
        }, worldChanged);
    }

}
