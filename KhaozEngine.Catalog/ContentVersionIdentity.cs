namespace KhaozEngine.Catalog;

/// <summary>
/// A content version's identity, contracts 7.1: the monotonic NUMBER that orders it and the manifest HASH
/// that names it. Both travel together everywhere a version is named, because they answer different
/// questions and neither substitutes for the other.
/// <para>
/// The number is what a durable page stamps (contracts 7.2), because a remap rule applies to any page whose
/// stamp is OLDER than the rule's version and a digest has no order. The hash is what the connect door and
/// the manifest carry, because it detects a page stamped by a different publish line and the number cannot.
/// </para>
/// <para>
/// A candidate that has not been published yet carries number 0 and an empty hash: the publish assigns the
/// number at its commit and computes the hash from the bytes it just wrote, so neither exists while the
/// candidate is still being built.
/// </para>
/// </summary>
/// <param name="Number">The version number, monotonic from 1, never reused and never skipped.</param>
/// <param name="ManifestHash">The manifest digest, lower hex, 64 characters, or empty on a candidate.</param>
public readonly record struct ContentVersionIdentity(int Number, string ManifestHash);
