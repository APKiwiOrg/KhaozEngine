using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// The ONE validator (spec 5.1, contracts 10.4), shared by publish, server boot and every test. It takes
/// its whole world as arguments: a complete candidate, the previous published snapshot or null, the full
/// ordered rule set and the registry. Nothing is read from a database, a file or an ambient static.
/// <para>
/// It is PURE. No logging, no counters, no mutation of the candidate, and no throwing for a content
/// reason. A throw out of here is a bug in the validator, which is why the untrusted per-type validators
/// are wrapped and why every encode is guarded. The sweep ACCUMULATES, so one run reports every defect
/// rather than the earliest.
/// </para>
/// <para>
/// <b>The five passes run in order and never stop early</b> (spec 5.3). Structure, schema, references,
/// visibility and codec, then the remap rules. Pass 3 needs pass 1's walk over the live rows and pass 5
/// needs to know which ids ever existed, and nothing else is ordered. The passes are single threaded.
/// After them comes pass 6, Scope B's own band, and the per-type validators run LAST.
/// </para>
/// <para>
/// <b>What this validator deliberately does NOT check</b> (spec 5.5), named so nobody adds one later
/// without a decision:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Whether a value is sensible.</b> A sword worth 0 coins and a tree with a 100 percent chance are
///     legal. The validator enforces the SHAPE of content and the owner owns the numbers.
///   </description></item>
///   <item><description>
///     <b>Whether a client has the art.</b> The minimum client build is the publisher's statement about
///     that, and the validator cannot see a client's asset bundle.
///   </description></item>
///   <item><description>
///     <b>Whether a localization key resolves.</b> <c>IStringCatalog.Get</c> never throws for a missing key
///     and returns the key itself as a visible placeholder, so a missing string is a visible defect rather
///     than a publish blocker.
///   </description></item>
///   <item><description>
///     <b>Whether a remap rule is a GOOD idea.</b> <c>KEC0015</c> refuses a rule set that is not idempotent
///     and nothing refuses a rule that moves every sword onto a stick. That is the owner's call.
///   </description></item>
/// </list>
/// </summary>
public static class ContentValidator
{
    /// <summary>
    /// The one code that is informational rather than a failure: <c>previous</c> was null, so the
    /// publish-only checks did not run and a clean report from a boot is not a clean report from a publish.
    /// </summary>
    public const string InformationalCode = "KEC0000";

    /// <summary>A per-type validator's finding, or its throw, carried back under the engine's own code.</summary>
    public const string TypeValidatorCode = "KEC0040";

    /// <summary>The lowest code of Scope B's reserved band, which pass 6 emits into and nothing else does.</summary>
    public const int InstanceBandFirstCode = 100;

    /// <summary>The highest code of Scope B's reserved band.</summary>
    public const int InstanceBandLastCode = 199;

    const string PublishOnlyMessage =
        "previous is null, so the publish-only checks did not run: KEC0003 (a key changed on a published row), "
        + "KEC0029 (a type id, type key or chunk_slots changed after its first publish) and the item-instances "
        + "tier-ordinal check. A clean report from a boot is not a clean report from a publish.";

    /// <summary>
    /// Sweeps one candidate and reports every defect it carries.
    /// </summary>
    /// <param name="candidate">The complete candidate, which a publish builds and a boot decodes.</param>
    /// <param name="previous">
    /// The snapshot of the version the candidate is based on, or null. It is null at boot, null for a first
    /// publish and null in almost every test, and the three change-shaped checks are SKIPPED when it is,
    /// which is a property of the argument rather than a mode flag.
    /// </param>
    /// <param name="rules">The full ordered remap rule set, contracts 8.1.</param>
    /// <param name="registry">The registry the candidate's types are declared in.</param>
    /// <exception cref="ArgumentNullException">
    /// An argument other than <paramref name="previous"/> is null, which is a programming error rather than
    /// a content defect and so is the one thing here that throws.
    /// </exception>
    public static ContentValidationReport Validate(
        ContentSnapshot candidate,
        ContentSnapshot? previous,
        IReadOnlyList<RemapRule> rules,
        ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(registry);

        var run = new ContentValidationRun(candidate, previous, rules, registry);
        if (previous is null)
        {
            run.Add(default, 0, InformationalCode, PublishOnlyMessage);
        }

        ContentKeyChecks.Run(run);
        ContentSchemaChecks.Run(run);
        ContentReferenceChecks.Run(run);
        ContentVisibilityChecks.Run(run);
        ContentRemapChecks.Run(run);
        RunInstanceBand(run);
        RunTypeValidators(run);

        bool valid = true;
        foreach (ContentFinding finding in run.Findings)
        {
            if (!string.Equals(finding.Code, InformationalCode, StringComparison.Ordinal))
            {
                valid = false;
                break;
            }
        }

        return new ContentValidationReport(valid, run.Findings);
    }

    /// <summary>
    /// Pass 6, Scope B's band, which is INSIDE the sweep rather than in the per-type slot (spec 5.3). Its
    /// types are engine code in the engine's own 256 to 1023 band, so their checks are the engine's checks:
    /// they emit <c>KEC0100</c> to <c>KEC0199</c> directly and a throw from one is a bug rather than
    /// something to swallow.
    /// <para>
    /// Phase 1 registers no type in that band, so this is the hook and the skip. The band being empty is
    /// every boot of a game that does not use instances, and the skip is what keeps its cost at nothing.
    /// </para>
    /// </summary>
    static void RunInstanceBand(ContentValidationRun run)
    {
        bool any = false;
        foreach (ContentTypeRegistration registration in run.Registry.ByTypeId)
        {
            if (registration.Type.IsInstances)
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return;
        }

        // Scope B ships its own checks here, against the rows of its own band's types. Until it does, a
        // registered instances type is swept by passes 1 to 5 like any other and adds nothing of its own.
    }

    /// <summary>
    /// The per-type validators (contracts 4.4), LAST, after pass 6, one per registered type. Each is handed
    /// its own type id and a read-only view of the whole candidate, and each may only ADD a constraint.
    /// <para>
    /// A validator is UNTRUSTED code, so a throw out of one becomes a single <c>KEC0040</c> carrying the
    /// exception message. One bad validator must not take a publish down with a stack trace where a finding
    /// was expected.
    /// </para>
    /// <para>
    /// The finding comes back under <c>KEC0040</c> with the game's own message, prefixed by its type key, so
    /// an operator can key a runbook on a code that never changes and still see which type produced it.
    /// Spec 5.2 words this as the code carrying the prefix, which would make the token unstable, so the
    /// prefix goes on the message and the code stays exactly <c>KEC0040</c>.
    /// </para>
    /// </summary>
    static void RunTypeValidators(ContentValidationRun run)
    {
        foreach (ContentTypeRegistration registration in run.Registry.ByTypeId)
        {
            IContentValidator? validator = registration.Validator;
            if (validator is null)
            {
                continue;
            }

            var own = new List<ContentFinding>();
            try
            {
                validator.Validate(registration.Type, run.Candidate, own);
            }
            catch (Exception ex)
            {
                run.Add(
                    registration.Type,
                    0,
                    TypeValidatorCode,
                    FormattableString.Invariant(
                        $"{registration.TypeKey}: the type's own validator threw {ex.GetType().Name}, '{ex.Message}'. A validator is untrusted code, so its throw is a finding rather than a failed publish."));
                continue;
            }

            foreach (ContentFinding finding in own)
            {
                run.Add(
                    finding.Type,
                    finding.Id,
                    TypeValidatorCode,
                    FormattableString.Invariant($"{registration.TypeKey}: {finding.Code} {finding.Message}"));
            }
        }
    }
}

/// <summary>
/// One sweep's world and its accumulating finding list, handed to each pass in turn. It exists so
/// <see cref="ContentValidator"/> holds the sweep and nothing else, and so adding a check grows a pass file
/// rather than the sweep (spec 2.7).
/// </summary>
internal sealed class ContentValidationRun
{
    internal ContentValidationRun(
        ContentSnapshot candidate,
        ContentSnapshot? previous,
        IReadOnlyList<RemapRule> rules,
        ContentTypeRegistry registry)
    {
        Candidate = candidate;
        Previous = previous;
        Rules = rules;
        Registry = registry;
        Findings = [];
    }

    /// <summary>The candidate under sweep.</summary>
    internal ContentSnapshot Candidate { get; }

    /// <summary>The previous published snapshot, or null, which is what the change-shaped checks need.</summary>
    internal ContentSnapshot? Previous { get; }

    /// <summary>The full ordered rule set, which pass 5 walks.</summary>
    internal IReadOnlyList<RemapRule> Rules { get; }

    /// <summary>The registry every type declaration is read from.</summary>
    internal ContentTypeRegistry Registry { get; }

    /// <summary>The findings so far, in emit order.</summary>
    internal List<ContentFinding> Findings { get; }

    /// <summary>Records one finding. There is no early exit anywhere: a pass runs to its end.</summary>
    internal void Add(ContentTypeId type, int id, string code, string message)
        => Findings.Add(new ContentFinding(type, id, code, message));

    /// <summary>
    /// One type's registration by its stable key, which is how the handful of ENGINE-SPECIFIC checks find
    /// their type. By key rather than by id, so a check cannot fire against a foreign type that happens to
    /// hold the engine's number.
    /// </summary>
    internal bool TryGetEngineType(string typeKey, [MaybeNullWhen(false)] out ContentTypeRegistration registration)
        => Registry.TryGetByKey(typeKey, out registration);

    /// <summary>The index of a schema field by name, which is its index in every row, or -1.</summary>
    internal static int FieldIndex(ContentFieldSchema schema, string name)
    {
        IReadOnlyList<ContentFieldEntry> fields = schema.Fields;
        for (int i = 0; i < fields.Count; i++)
        {
            if (string.Equals(fields[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// One field's value on a row, ABSENT when the row is shorter than the schema. A short row is a
    /// <c>KEC0005</c> for every required field it dropped, reported by the schema pass, and every other
    /// check reads it as absent rather than walking off the end.
    /// </summary>
    internal static ContentFieldValue Value(ContentRow row, int index, ContentFieldKind kind)
        => index >= 0 && index < row.Fields.Count ? row.Fields[index] : ContentFieldValue.Absent(kind);
}
