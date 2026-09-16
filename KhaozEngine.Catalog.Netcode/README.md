# KhaozEngine.Catalog.Netcode

The CONTENT layer of the connect door. A server serves exactly one published content version, and a client
that holds another one is refused at the door with a token that carries both sides, so the client knows what
to fetch before it tries again.

It is its own small package rather than a type inside `KhaozEngine.Catalog` because `Catalog` is a
`Foundation` package and `Foundation` cannot reference a `Server`-side one. Its two project references are
`KhaozEngine.Catalog`, for the version identity and the one definition of a content address, and
`KhaozEngine.Netcode`, for the handshake layer codec and the authenticator seam.

## The layer

A connect token is a nest of labelled layers, outermost first, each one peeled by the gate that owns it.
The content layer's value is the decimal version number and the 64 character lower hex CLIENT manifest hash
joined by a pipe:

```
47|3f9c...  (64 hex)
47|3f9c...|4118        the optional third field, the client's own build ordinal
```

Both fields, because the number alone cannot detect a client that rebuilt a pack wrongly and the hash alone
cannot tell an operator which side is behind. The third field is optional: a client that states no build is
making no statement rather than a false one.

```csharp
byte[] token = ConnectionGate.BuildToken(
    protocolVersion,
    worldHash,
    ContentIdentityLayer.Wrap(new ContentVersionIdentity(47, clientManifestHash), authToken, clientBuild: 4118));
```

`ContentIdentityLayer.TryParse` is STRICT, and that is what keeps a refusal parseable: a version number is
plain invariant decimal digits and a hash is exactly a content address. A layer value that fails the shape
reads as no layer at all, so nothing a client sent can be echoed back into a token a client then splits on
colons.

## The gate

```csharp
IConnectionAuthenticator auth = isBanned is null ? tokenAuth : new BanGateAuthenticator(tokenAuth, isBanned);
IConnectionAuthenticator content = new ContentIdentityGateAuthenticator(
    new ContentVersionIdentity(runtime.VersionNumber, clientManifestHash),
    auth,
    minimumClientBuild: manifest.MinimumClientBuild,
    log: Console.Out.WriteLine);

IConnectionAuthenticator door = ConnectionGate.Wrap(content, protocolVersion, worldHash);
```

Layer ORDER in the nest, outermost first: protocol version, world, CONTENT, the game's token auth, the ban
check. Content sits inside world and outside auth because a disagreement about content is a cheaper and more
specific refusal than a failed credential, and the ban check stays innermost because it needs the subject the
token produced.

The gate is `WorldIdentityGateAuthenticator`'s shape in every particular: unwrap ONE layer, compare ORDINAL,
refuse with a stable wire token carrying both sides, otherwise delegate inward. It implements
`IConnectionDisplayName` and `IConnectionPersistenceKey` the same way, each unwrapping its layer and
delegating.

The identity it gates on is the version number and the CLIENT manifest hash, never the server manifest hash,
which no client ever holds.

## The two refusals

```
ke:content-mismatch:<serverVersion>|<serverHash>|<clientVersion>|<clientHash>
ke:content-client-too-old:<minimumClientBuild>
```

The PIPE separates the fields inside the payload and the COLON separates the token's own fields, matching
`ke:world-mismatch:<serverHash>|<clientHash>`. These are STABLE WIRE TOKENS a client matches and renders as
its own localized string, never display text, so `ContentRefusal.TryParseMismatch` and
`TryParseClientTooOld` are the client's half and the strings themselves never move.

- A client presenting no layer, or one that does not parse, is refused with EMPTY client fields.
- A client that STATES a build below the version's `MinimumClientBuild` is told to update instead, ahead of
  the identity check, because a mismatch it cannot clear by refetching is the wrong thing to show a player.
  A client that states no build is judged on its content identity alone: the client half of the build check
  is the fetch loop's, against the manifest it just fetched.
- A client that is behind on content is REFUSED, not admitted read-only while it fetches. The refusal
  carries the server's version number and manifest hash, which is the whole input the fetch loop needs.
- The refusal carries NO URL. A URL in a refusal token is a redirect an unauthenticated party controls, so
  the client is configured with its pack base address the way it is configured with its server address.
