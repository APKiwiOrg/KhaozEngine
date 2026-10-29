# KhaozEngine.StatusEffects.Netcode

The Server umbrella adapter for transient status effects. `StatusEffectProjection` publishes changed
actors' shown entries as immutable `ActiveStatusEffects` snapshots. Targets report `Published`,
`Deferred` or `Gone` through `StatusEffectPublishResult`. The projection merges changed actors with
deferred actors and publishes their current shown entries in ascending actor order. It retries
`Deferred` actors on the next call and forgets only actors reported as `Gone`.

`ShardHostStatusEffectTarget` writes only to the actor's current owning entity. When ownership is
temporarily unavailable, `ShardHost.IsInHandoff` distinguishes a live frozen source from a despawn.
Deferred admission and a delayed cell link preserve the book and pending publication. After adoption,
the new owner receives current entries, including expiries during the gap. Ghosts and migrating
copies are never written by the target.

Register `StatusEffectsReplication` with the same extension type id on the server and every client.
The component uses Replicate and Migrate without Persist. Call `Publish` after the book's `Advance`
and gameplay applies each tick. Clients derive remaining time from the replicated end tick and their
estimated server tick through `StatusEffectBar`.
