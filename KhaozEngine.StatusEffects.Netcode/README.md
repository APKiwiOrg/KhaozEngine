# KhaozEngine.StatusEffects.Netcode

The Server umbrella adapter for transient status effects. `StatusEffectProjection` publishes changed
actors' shown entries as immutable `ActiveStatusEffects` snapshots. `ShardHostStatusEffectTarget`
resolves the actor's current owning cell. The projection forgets actors when publishing fails.

Register `StatusEffectsReplication` with the same extension type id on the server and every client.
The component uses Replicate and Migrate without Persist. Call `Publish` after the book's `Advance`
and gameplay applies each tick. Clients derive remaining time from the replicated end tick and their
estimated server tick through `StatusEffectBar`.
