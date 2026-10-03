# Grimhollow Delta Reliability Implementation Plan

**Status:** Complete. All tasks are implemented, reviewed and verified, and the work is staged for engine 20.20.0 with no tag. The Outcome section at the end records every task's commits and counts, every execution ruling and the final verification. Option A and its policies were approved before execution, and the self-review and review fix rounds 1 and 2 remain recorded before the Outcome.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct reliable delta reversion and add opt-in, bounded unreliable delta reconstruction and recovery before Grimhollow P8.

**Architecture:** Legacy writers diff against their exact last sent viewer projection. Format 2 writers diff against a retained acknowledged projection, and clients reconstruct an immutable projection before publishing. Replication owns bytes and ECS publication, Netcode owns generic transport and reliable fragments, and NetWorld owns negotiation, envelopes, cadence and lifecycle.

**Tech Stack:** C#, .NET, ECS, xUnit in `KhaozEngine.Server.Tests`, in-memory transports, optional LiteNetLib 2.1.2 backend.

**Spec:** [Approved delta reliability specification](../../design/GRIMHOLLOW-DELTA-RELIABILITY-DESIGN-2026-10-02.md). Read it before each task. Option A and all its bounded policies are approved. This plan settles implementation signatures, not a new delivery policy.

## Global Constraints

- Start from current `origin/main` merged with this planning branch, after the root verifies the execution branch and concurrent work. The planning branch is `feature/grimhollow-delta-plan`. At review fix round 1 it contained `origin/main` `d0aaabcdc`, and no Replication, Netcode, NetWorld, LiteNetLib, their tests, Sharding tests or `Directory.Packages.props` changed since the approval commit `56f0df78d`. Main's newer commits touched `docs/INDEX.md` and `docs/USING-KHAOZENGINE.md`, which Task 10 edits. Re-run that diff at execution preflight and re-check every source row below if it is non-empty.
- Release target: this work rides the next staged engine version and cannot ride `20.18.0`, which is tagged and released as `v20.18.0`. At review fix round 1 `Directory.Build.props` still read `20.18.0` with no newer version staged. If no newer version is staged when this lands, root stages one. Re-read the version, tags and `CHANGELOG.md` at execution. Root selects the version, writes the changelog entry in the same commit as any bump, and owns pack proof. The work adds public API, so root decides patch or minor under the engine release rules. No version is reserved by this plan.
- KESIZE: every touched existing file stays under the 800-line cap with no baseline entry. Headroom at self-review was `WorldClient.cs` 38 lines, `WorldServer.cs` 69, `ShardedWorldServer.cs` 63 and `MoveProtocol.cs` 148. New behavior goes into the new partials and types named below. Edits to those four files are limited to dispatch calls, config properties, enum members and test seams. Each task runs `sh scripts/check-file-size.sh --tree` with its GREEN gate and stops for root on a cap breach rather than growing a baseline.
- Legacy reliable delivery remains the default. Both new server opt-ins and the new client opt-in default false and require the existing delta switch.
- Keep `MoveProtocol.WireProtocolVersion` at 13. New format 2 frames are capability gated. Changing an existing payload or built-in codec requires a new owner decision.
- Every 18-byte client payload receives ordinary MOVE decode and validation, before any new marker inspection. New controls never claim length 18. Repair is 19 bytes.
- Replication references Ecs only. Netcode has no Replication or backend dependency. NetWorld adapts the two. LiteNetLib remains optional. No csproj dependency changes are needed. Replication and NetWorld grant `InternalsVisibleTo` only to `KhaozEngine.Server.Tests`, and Netcode grants none. NetWorld therefore calls only public Replication members, and Netcode tests use only public Netcode members. No new `InternalsVisibleTo` edge is added.
- Retained projections default 32, minimum 4. Retained reachable component backing bytes default 2 MiB, counted as the full `Length` of each distinct backing array reachable from any retained projection, by reference identity. These include every pin and the unsent or repair candidate. Client presentation buffers hold independent copies and are outside this budget.
- Complete keyframe objects default 64 KiB, including the 12-byte local-net-id and movement-ack envelope. Projection limits are 1,024 entities and 16,384 component frames, including tags and opaque extensions.
- Transport payload cap defaults 512 bytes, including SessionFrame and NetWorld envelopes. Query actual per-connection limits. Zero means unknown and selects reliable fallback with a surfaced reason.
- One routine state send and four reliable repair chunks per viewer per replication tick. Keep only the newest unsent state. One repair per viewer, no mandatory periodic keyframe.
- No-ack window defaults 31 committed new sends. Repair requests repeat every 30 replication ticks. Negotiation and repair expire after 90 replication ticks with typed failure and disconnect. Capacity failure disconnects.
- Replication cadence uses configured `TickSeconds` and elapsed host time. Input production has a separate accumulator. Opted-in clients supply elapsed `Poll(dt)`, with zero-time drain calls allowed.
- Capture once per distinct world per opened capture tick, across legacy and v2 viewers. Filter entity visibility and component ownership before retaining viewer state or freezing repair.
- Preserve registered built-in and extension payloads, 7-bit extension lengths, legacy application methods, non-replicated game components, local interpolation exclusion and teleport cuts.
- Legacy exhaustion ends every affected connection, including v2 sessions sharing the writer, before global reset. `Forget` never resets counters. The host epoch allocator never resets with the writer.
- No gameplay, journal, renderer, ground movement, physics or consumer switch changes. Leave `WorldClient.Frame.cs` and the separate #1233 work to their owner. `LocalPredictedState` already exists at the starting commit and is read only here.
- No local stress, random loss, sleeps, polling-until-success or repeated test loops. Use deterministic finite schedules. A hosted stress workflow needs new explicit permission.
- Root owns integration, final checks, living-doc reconciliation, version selection, packaging and any later release action. No release tag is authorized by this plan.

## Review Focus

1. Signed MOVE sequences aliasing C5/A1, C5/A2 and C5/A3 must still move and advance movement acknowledgement. Task 6 decoder tests and Task 7a real-host tests cover them.
2. Presentation changes live ECS between accepts. An empty rebuilt delta must reinstall authoritative values and remove departed interpolation histories. Task 4 covers this independently of endpoint position.
3. A tiny or shrinking backend limit must never emit a too-large datagram or change fragment width during an assembly. Tasks 5, 7a, 7b and 9 cover fallback, renegotiation and boundaries.
4. Shared captures can accidentally retain hidden owner bytes in a small viewer slice. Tasks 2 and 3 charge complete backing allocations and assert viewer-only compact buffers.
5. Slot reuse, a delayed reliable keyframe and global signed sequence exhaustion can revive retired state. Tasks 4, 7b, 7c and 9 prove epoch retirement and fresh receiver lifecycle.

## Source ground and discriminator reservations

The following are source facts at the starting commit, not assumed future behavior.

| Source | Current seam and required disposition |
| --- | --- |
| `KhaozEngine.Replication/ServerReplicator.cs` | `Capture(World)`, `WriteFor(int,long?)`, global signed `currentSeq`, shared history and owner reprojection. Replace only the legacy diff basis and add reset APIs in Task 1. |
| `KhaozEngine.Replication/AoiDeltaReplicator.cs` | `BeginTick()`, lazy `CaptureFor(World)`, indexed `Project`, pending ack state and presence records. Preserve capture sharing and insertion order. Tasks 1 and 3 own this file sequentially. |
| `KhaozEngine.Replication/CapturedComponents.cs` | Component segments retain a whole `CaptureBuffer`, even after `PublicView` filters its index. V2 retention must compact visible payloads, not charge slice lengths while retaining the raw world buffer. |
| `KhaozEngine.Replication/ReplicationRegistry.cs` | `ComponentCodec` is in this file. `Register<T>` supplies typed read, write, removal and sampling delegates. Task 2 adds an internal typed copy delegate without changing registration signatures. |
| `KhaozEngine.Replication/ClientReplicationView.cs` | Live entity map and sampled-byte buffers, legacy readers, interpolation history. Add a complete publication path beside legacy application. Do not use these buffers as v2 baselines. |
| `KhaozEngine.Netcode/INetTransport.cs` | The optional packet query belongs here, not in Abstractions. Existing optional Stats and disconnect defaults establish the additive seam pattern. |
| `KhaozEngine.Netcode/{NetClient,NetServer}.cs` | Session facades know connection identities. `NetClient.Send` and `NetServer.SendTo` are void and silently skip a missing connection. `INetTransport.Send` is void, and both LiteNetLib transports also skip an unknown peer silently. Add success-reporting send variants for the sent-history boundary. Success means handed to the transport for a known connection, never delivery. |
| `KhaozEngine.Netcode/{MessageFragmenter,MessageReassembler}.cs` | Header is 5 bytes, maximum 255 chunks, reliable order, agreed fixed width, default four partial assemblies. Add bounded reassembler construction and keep the existing constructor and wire unchanged. |
| `KhaozEngine.Netcode.LiteNetLib/*Transport.cs` | `peersById` maps connection value minus one to a `NetPeer`. `ChannelSplitter.ToDeliveryMethod` maps the engine reliability. Cached 2.1.2 XML documents `LiteNetPeer.GetMaxSinglePacketSize(DeliveryMethod)` as the maximum unfragmented packet size, and `NetPeer` is documented separately. Both transport constructors bind a UDP socket, so backend tests are `LiveSocket` category. |
| `KhaozEngine.NetWorld/MoveProtocol.cs` | Controls 1 and 2, C5 marker, A0 legacy ack, B0 game message, server kinds 0 through 3, generation 13, MOVE size 18. `TryDecodeMove` accepts length at least 18 today. New recognized malformed controls must claim non-18 lengths before this permissive fallback. |
| `KhaozEngine.NetWorld/WorldServer.cs` | Also declares `WorldServerConfig`. Reliable serving is inside `Tick`, control/move routing is `HandleData`, and visibility runs before `WriteFor`. Keep source-local ownership filtering. |
| `KhaozEngine.NetWorld/{ShardedWorldServer,ShardedWorldServerConfig}.cs` | Sharded `Tick` serves even when no simulation sub-tick ran. V2 cadence must be elapsed-time based and independent of `movementRan`. |
| `KhaozEngine.NetWorld/WorldClient.cs` | `Poll` currently treats any Data as liveness. `OnDelta` applies, ingests and reliably acks. Add v2 acceptance-only ingest and valid-state liveness. Reconnect creates a new world and view. |
| `docs/superpowers/plans/2026-10-02-round2-d-movement-drivers.md` | D owns the read-only absolute `LocalPredictedState` surface, now in `WorldClient.Frame.cs`. Tests consume it. No second prediction API is proposed. |
| `KhaozEngine.NetWorld/{DisconnectReason,ConnectRefusal,ReconnectBackoff}.cs` | `ConnectRefusal.Read` maps a reject token to a reason and retry rule. An unknown token becomes `RejectedToken` with `WhenRetryOnReject`, which is not a backoff retry. `ReconnectBackoff.MaxAttempts` defaults to 0, meaning unlimited spaced attempts chosen by the consumer. |
| `KhaozEngine.Replication/ClientReplicationView.cs` sampling | `RecordInterpolationSample` stores the same `byte[]` reference held in `currentBytes`, without copying, in a history capped at 600 samples per component. Legacy readers allocate a fresh array per component, so legacy samples never alias a capture. |
| In-repo standalone writer callers | `MmoServerSample/MmoServer.cs` sends every AOI `WriteFor` once over `ReliableOrdered` and calls `Forget` on leave. `KhaozEngine.Server.AotProbe/Program.cs` applies every `ServerReplicator` payload in order. Both already satisfy the last-sent send commitment. `KhaozEngine.Benchmarks/ReplicationTickBenchmark.cs` discards payloads as a measurement harness. `KhaozEngine.Sharding/CellSim.cs` exposes a `ServerReplicator` but never calls `WriteFor`. `TileWorldClient` does not use the delta writers. |

Reserve capability 3, server kinds RebuildDelta 4, RebuildKeyframeChunk 5 and ReplicationMode 6, and control sub-markers A1, A2 and A3. Re-scan `MoveProtocol` at execution preflight. If concurrently assigned, stop for root reconciliation rather than choose an unreviewed wire shape.

All new multibyte fields use `BinaryPrimitives` little-endian methods. V2 body header is 18 bytes. Complete empty state datagram is 40 bytes. Keyframe chunk transport overhead is 23 bytes. Mode offer is 36 bytes including the server-kind byte, 37 including SessionFrame. No existing encoder body changes.

## Ownership and execution order

Use at most three child lanes under root. A lane owns its listed files exclusively. Tasks in one lane are sequential and never overlap writes to their shared files.

| Lane | Tasks | Exclusive source and test ownership |
| --- | --- | --- |
| Replication | 1, 2, 3, 4 | All Replication sources listed below and their named Replication test files. Task 3 follows Task 1 in the two writer files. Task 4 follows Tasks 2 and 3, because it decodes Task 3's packet layout and extends Task 2's presentation view. |
| Netcode | 5 | Transport seam, session facades, reliable reassembler, optional backend and its named Netcode tests. |
| NetWorld | 6, 7a, 7b, 7c, 8 | New NetWorld stream types, protocol enum additions, config additions, host and client integration, and the named NetWorld unit/integration tests. |
| Root acceptance and docs | 9, 10 | New fault-schedule and acceptance fixtures, characterization and living docs. Starts after child gates. Root reconciles shared living docs with #1233 before edits. |

Dependencies: `1 -> 3`, `2 -> 3`, `2 + 3 -> 4`, `2 -> 6`, `3 + 5 + 6 -> 7a -> 7b -> 7c`, `4 + 5 + 6 + 7b -> 8`, `7c + 8 -> 9 -> 10`. Task 8's peer tests need a real server that negotiates and also completes the keyframe barrier and steady serve, which only 7b provides. Tasks 1, 2 and 5 have no source dependency on each other. Tasks 6, 7a, 7b, 7c and 8 remain one sequential lane to avoid `MoveProtocol`, host and client file contention.

Root grants one CPU command at a time across the shared Mac. Child lanes prepare edits and request the root's synchronous build/test slot. Never start background work. The commands below are execution instructions, none were run during planning. Set `DELTA_WORKTREE` to the absolute worktree named in the execution brief and prefix every command with `cd "$DELTA_WORKTREE" &&`.

For each task, declaration RED means introducing compiling public/internal declarations with minimal throwing bodies, plus compiling tests that reach the new surface. Missing symbols and a failing build are setup failures, not behavioral RED evidence. Run the targeted build, run the behavioral test to an expected assertion or `NotImplementedException` failure, implement, rebuild and run the same filter to GREEN. Do not commit stubs. Existing-only Task 1 reversion assertions can fail without a new declaration.

The narrow declaration and post-edit build command for every task is:

```sh
cd "$DELTA_WORKTREE" && dotnet build KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release -m:1 /p:BuildInParallel=false
```

Every task's commit uses explicit paths from its Files block and `area(scope): summary`. Root chooses whether to commit directly or integrate a delegated task commit. Do not stage shared docs from a child lane. Stop on a hook block without bypass. Root records exit codes and failure tails.

## Test helper and oracle contracts

These helpers are test code under `KhaozEngine.Tests.*` namespaces. A helper never calls the production code it checks. Each is created by the task named in its row and listed in that task's Files block. Internal seams are reachable because Replication and NetWorld already grant `InternalsVisibleTo` to `KhaozEngine.Server.Tests`.

| Helper or seam | Owner task | Exact contract |
| --- | --- | --- |
| `Replication/LegacyWriterFixture.cs` | 1 | `internal abstract class LegacyWriterFixture` with sealed `WholeWorld` and `Aoi` subclasses. Members: `World ServerWorld`, `World ClientWorld`, `ClientReplicationView View`, `int CaptureNext()`, `byte[] Serve(int slot, IReadOnlySet<long>? interest = null, long? owner = null)`, `void Apply(byte[] payload)`, `void Ack(int slot, int sequence)`, `void Forget(int slot)`, `void SeedSequence(int sequence)`, `bool Exhausted`, `int CurrentSeq` and `void ResetAfterExhaustion()`. `CaptureNext` calls `Capture(ServerWorld)` or `BeginTick()`. `Serve` ignores `interest` for the whole-world writer and requires it for AOI. `Apply` calls the unchanged legacy `ApplyDelta`. |
| `Replication/LegacyDeltaWire.cs` | 1 | `static LegacyDeltaHeader ReadHeader(ReadOnlySpan<byte> payload)` returning `readonly record struct LegacyDeltaHeader(int Baseline, int Snapshot, long[] RemovedNetIds, int ChangedCount)`. It reads signed baseline, signed snapshot, removed count, Int64 removed ids and changed count with `BinaryPrimitives` little-endian reads. The same Int32 removed-id bug exists in three private helpers, `ReplicationDeltaTests.Header`, `AoiDeltaReplicatorTests.Header` (lines 28 to 37) and `ClientReplicationViewHealTests.ReadHeader` (lines 126 to 135), and in one inline reader in `ReplicationChannelsTests.cs` (lines 416 to 422), which is latent because it asserts zero removals. Task 1 replaces all four with `LegacyDeltaWire.ReadHeader`. |
| `Replication/RebuildWire.cs` | 3 | `static RebuildWireHeader ReadHeader(ReadOnlySpan<byte> body)` returning `readonly record struct RebuildWireHeader(byte Format, byte Flags, ulong Epoch, uint Snapshot, uint Baseline, int RemovedCount, int ChangedCount)`. It reads the 18-byte v2 header, the removed count, skips the removed Int64 ids, then reads the changed count. It is independent of `RebuildDeltaReader`. |
| `Replication/ProjectionDump.cs` | 2 | `static IReadOnlyList<ProjectionEntry> Of(ReplicationProjection projection)` with `readonly record struct ProjectionEntry(long NetId, ushort TypeId, byte[] Payload)`, ordered by net id then type id. `static void AssertEqual(IReadOnlyList<ProjectionEntry> expected, IReadOnlyList<ProjectionEntry> actual)` compares membership, type ids and payload bytes, including zero-byte payloads and opaque extensions. `static int DistinctBackingBytes(IEnumerable<ReplicationProjection> projections)` sums `Length` over the distinct `BackingArraysForTest()` arrays by reference identity, giving the exact expected retained-byte figure. `RetainedOf` in snippets is a test-local loop over `TryGetRetainedProjectionForTest`. Production seam: internal `IEnumerable<(long NetId, ushort TypeId, ReadOnlyMemory<byte> Payload)> ReplicationProjection.EntriesForTest()` in that order. |
| Backing identity seams | 2 | Internal `IEnumerable<byte[]> ReplicationProjection.BackingArraysForTest()`, `IEnumerable<byte[]> ProjectionRetention.BackingArraysForTest()` and `IEnumerable<byte[]> ClientReplicationView.PresentationArraysForTest()`. The last enumerates every array referenced by `currentBytes`, `previousBytes` and `sampleHistory`. Tests compare these sets with `ReferenceEqualityComparer.Instance`. |
| Retained projection seams | 3, 4 | Writers: internal `bool TryGetRetainedProjectionForTest(int slot, ReplicationPacketId id, out ReplicationProjection projection)`. `ClientDeltaRebuild`: internal `bool TryGetRetainedForTest(ReplicationPacketId id, out ReplicationProjection projection)`, `int RetainedCountForTest`, `int RetainedBytesForTest`, `int PublicationCountForTest` and `IReadOnlySet<ReplicationPacketId> PinnedIdsForTest`. `serverProjectionBytes` and `retainedProjectionBytes` in snippets are `ProjectionDump.Of` over these. |
| `Replication/CountingCodec.cs` | 4 | `internal sealed class CountingCodec` that builds a fresh `ReplicationRegistry` and registers an unframed `CountedBuiltin` at type id 9 and a framed `CountedExtension` at `ReplicationRegistry.FirstExtensionTypeId + 1`. Their read delegates increment per-instance `BuiltinReads` and `ExtensionReads`. The oracle is exact: for each accepted packet the two counters together advance by exactly the number of known components in the reconstructed projection, which is received known replacements plus known components carried over from the baseline, each decoded once into staging. Publication adds zero reads. A rejected packet may read at most its received known components and publishes nothing. Staging is rebuilt per packet and is not reused across packets, so carried-over baseline components are decoded again, which is the counted cost. |
| Host serve observation | 7a | Internal `Action<int, World, IReadOnlySet<long>, long>? ServeObservedForTest` on both hosts, invoked once per served slot after `InterestVisibility.Filter` and before either the legacy or v2 writer, with slot, served world, filtered interest and owner net id. The callback copies the set and never retains it. |
| `NetWorld/DeltaReliabilityReference.cs` expected projection | 9 | `static IReadOnlyList<ProjectionEntry> ExpectedProjection(World served, ReplicationRegistry registry, IReadOnlySet<long> filteredInterest, long ownerNetId)`. It walks net-id entities in the filtered set and serializes each registered `Replicate` component through internal `ComponentCodec.TrySerialize`, including `OwnerOnly` components only on the owner. It never calls a capture, projection or writer type. Membership is cross-checked against `ExpectedVisible`, computed by the rig from its own positions, `InterestRadius` and the schedule's visibility table. |
| Local presentation oracle | 9 | `LocalOracle` replays the rig's recorded submitted `MoveCommand`s from the authoritative spawn basis through a separate `PlayerMoveSimulator` with the same flat ground, tuning and `TickSeconds`. The server rig ticks with exactly `TickSeconds`. Expected predicted position follows each submission. Expected rendered position at a presentation event is `Vector2.Lerp(previousExpected, currentExpected, MathF.Min(1f, secondsSinceSubmit / TickSeconds))`. Assert `LocalPredictedState` within the position tolerance, and the local `Snapshot()` position within tolerance plus `PredictionSettings.CorrectionDeadZone`. Every recorded reconcile error outside the teleport ingest must be within the position tolerance, otherwise the flat case fails. At the first presentation event after the accepted teleport ingest the rendered position equals the destination and `LocalTeleportEpoch` advanced exactly once. The oracle checks netcode wiring, phases and acks, not the movement kernel. |
| Remote presentation oracle | 9 | `RemoteOracle` stamps each accepted ingest for the observed remote with the rig's own presentation clock, a `double` accumulated from the same float frame steps the rig passes to `AdvancePresentation`. A stamp at or before the previous one overwrites it. Render time is clock minus `InterpolationDelayTicks * TickSeconds`, computed as a float product as `WorldClient` does. The lower bracket is the last sample at or before render time. Before the oldest it clamps to the oldest. At or past the newest it holds the newest, and `held` is true when render time exceeds the newest stamp by more than `1e-9`. Otherwise position lerps by the clamped fraction, and heading quantum and movement flags take the lower sample when the fraction is at most 0.5. A teleport epoch advance at ingest drops every older sample. Position compares within tolerance. Heading, flags and `held` compare exactly. |
| Pinned hold frames | 9 | Each named schedule stores its expected held presentation-frame indices as constants derived by hand from the schedule table and the remote oracle rules, reviewed before GREEN. They are never copied from an observed run. A test asserts actual, oracle and pinned sets are equal. |

### Task 1: Correct the legacy last-sent contract and signed exhaustion

**Files:** Modify `KhaozEngine.Replication/{ServerReplicator,AoiDeltaReplicator}.cs`. Create `KhaozEngine.Replication/LegacyDeltaSlot.cs`. Delete `KhaozEngine.Replication/AoiPresenceRecord.cs` together with AOI's `presenceBySlot`, `IsWholeEntry`, `PresenceFor` and the internal `RemovalRecordCount` and `WholeRecordCount` seams, because the last-sent diff makes presence records dead and v2 never uses them. Modify `KhaozEngine.Server.Tests/Replication/{ReplicationDeltaTests,AoiDeltaReplicatorTests,AoiDeltaReplicatorWireParityGoldenTests,AoiDeltaReplicatorProjectionParityTests,ClientReplicationViewHealTests,ReplicationChannelsTests}.cs`. Create `KhaozEngine.Server.Tests/Replication/{LegacyReliableDeltaTests,LegacySequenceExhaustionTests,LegacyWriterFixture,LegacyDeltaWire}.cs`. Modify the existing tests in the regression filter below only where they assert ack-relative bases, never to weaken a convergence assertion. Leave `DeltaEncoding.cs` and legacy reader byte/application shapes unchanged. `ServerReplicator` keeps its public `historyDepth` constructor parameter and its validation for source compatibility, but the legacy path retains only the current capture plus one last-sent projection per slot. Its 32-deep capture history therefore shrinks to the current capture, `LegacyHistoryCount` is at most 1 after any capture, and `Acknowledge(int,int)` becomes sequence-only diagnostics that never selects a basis.

**Interfaces:** Consume `CapturedComponents`, `CaptureProjection.OwnerScope` and `DeltaEncoding.WriteChangedEntity`. Preserve `Capture(World)`, `BeginTick()`, `CurrentSeq`, both `WriteFor` signatures, and `Acknowledge(int,int)`. Produce `public void ServerReplicator.Forget(int slot)`, and on both writers `public bool LegacySequenceExhausted { get; }` and `public void ResetAfterLegacySequenceExhaustion()`. Add internal `void SeedLegacySequenceForTest(int sequence)` and `int LegacySlotCount` to both, and internal `int LegacyHistoryCount` for global history/capture assertions. Task 3 extends the reset implementation to its v2 state.

- [ ] Add compiling declarations and fresh fixtures for both writer shapes. `LegacyDeltaSlot` stores `(sequence, exact viewer projection)` from the last returned payload. Acknowledge bookkeeping cannot select that projection.
- [ ] Add `LegacyReliableDeltaTests.ReversionWithoutAckRestoresOriginalValue` for both writers, with an unchanged legacy `ClientReplicationView`. Serve and apply S with value 1, then acknowledge S. Change to 2, serve and apply S+1, and do not acknowledge it. Change back to 1 and serve and apply S+2. Assert the S+2 header names S+1 as its baseline and the client reads 1. Against the current ack-relative writer S+2 diffs from S, carries no change and the client stays at 2. That stale 2 and the S baseline in the header are the expected RED. Add flags, zero-byte tag on/off, component present/remove/re-add with original bytes, component absent/add/remove, entity absent/enter/leave and present/leave/re-enter. Whole-world entity cases use spawn/despawn. AOI cases change interest.

```csharp
Assert.Equal(secondSentSequence, LegacyDeltaWire.ReadHeader(thirdPayload).Baseline);
Assert.Equal(1, clientWorld.Get<Value>(clientEntity).Number);
Assert.False(clientWorld.TryGet<Tag>(clientEntity, out _));
Assert.False(view.TryGetEntity(intermediateOnlyNetId, out _));
```

- [ ] Add `StoredOwnerProjectionIsNotReprojectedAfterOwnerChange` and `ForgetStartsFullOnlyWithFreshReceiver`. Assert private bytes go only to their current owner, a changed owner produces explicit absence/removal relative to the stored old projection, and a fresh receiver after Forget gets baseline -1. Document that same-world receiver reuse is unsupported.
- [ ] Build, then run behavioral RED with the command below. The reversion assertion must fail against the current ack-relative writer, with no compiler failure.

```sh
cd "$DELTA_WORKTREE" && dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~LegacyReliableDeltaTests|FullyQualifiedName~LegacySequenceExhaustionTests'
```

- [ ] Diff from the stored last-sent viewer projection and replace it only after the payload has been completely built. Retire presence records as reliable diff inputs. Preserve capture order and legacy framing. A returned payload is a send commitment, with send failure/discard requiring a new receiver connection and per-slot reset.
- [ ] Remove AOI's retained acknowledged/pending payload projections from the legacy path. Keep one last-sent payload projection per slot, with at most bounded sequence-only diagnostic bookkeeping. Rewrite `A_readd_after_the_removal_is_acked_diffs_normally_and_the_record_is_pruned`, `Forget_clears_the_removal_and_whole_entry_records`, `An_entity_that_arrives_and_leaves_inside_the_ack_window_is_removed_until_acked` and `An_entity_that_arrives_leaves_and_returns_inside_the_ack_window_is_held_whole` around delivered predecessors and actual ECS convergence. Do not retain old presence-map count expectations or repeated-until-ack wire expectations.
- [ ] Add `FinalSignedSequenceStopsCaptureAndForgetCannotReset`, `ResetBeforeExhaustionIsRejected` and `GlobalResetClearsEverySlotAndSharedState` for each writer. First populate two slots with ordinary low-sequence captures and served payloads, so slot state and retained history exist. Then seed the counter to `int.MaxValue - 1`, capture once to reach `int.MaxValue` and serve both slots at `int.MaxValue`, then assert the following. No long counter loop.

```csharp
Assert.Equal(int.MaxValue, fixture.CurrentSeq);
Assert.True(fixture.Exhausted);
Assert.Throws<InvalidOperationException>(() => fixture.CaptureNext());
fixture.Forget(0);
Assert.True(fixture.Exhausted);
Assert.Equal(int.MaxValue, fixture.CurrentSeq);
```

- [ ] Guard before scanning or incrementing. Global reset rejects non-exhausted use and clears the counter to zero, global history/order, shared captures, and all slot state. Its documented lifecycle precondition is no attached old connection. Replication cannot verify or disconnect transports. Assert first fresh capture 1 and first fresh legacy baseline -1.
- [ ] Revise `Skipped_ack_keeps_diffing_from_the_last_acked_baseline` into a last-sent contract test. Change the independent `ReferenceAoiDelta` in `AoiDeltaReplicatorProjectionParityTests.cs` (lines 90 to 218) to the last-sent contract first, keeping it independent of the production writer. Then derive every changed `AoiDeltaReplicatorWireParityGoldenTests` frame from that revised reference or by hand from the documented field layout. Never copy expected bytes from the new writer's output, because that would make the golden test the writer's echo. Retain the existing field framing. `NoChange_ProducesEmptyDelta` must actually send/apply the first delta before acknowledging it. Replace the four Int32 removed-id header readers named in the helper table with `LegacyDeltaWire.ReadHeader`. Keep the hand-built legacy-reader compatibility cases in `ClientReplicationViewHealTests`, without claiming they prove loss-safe overlay.
- [ ] Rebuild and run the RED filter to GREEN, then the regression filter below once. `WorldServerConfig.DeltaReplication` and `WorldClientConfig.RequestDeltaReplication` both default true, so every NetWorld test with a `WorldClient` exercises the AOI writer. The filter therefore runs all Replication, NetWorld and Sharding namespaces, the replication benchmark facts and `AdminHttpServerTests`, which builds a real `WorldServer` in the `KhaozEngine.Tests.ServerAdminEndpoint` namespace, excluding `LiveSocket`:

```sh
cd "$DELTA_WORKTREE" && dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter '(FullyQualifiedName~KhaozEngine.Tests.Replication|FullyQualifiedName~KhaozEngine.Tests.NetWorld|FullyQualifiedName~KhaozEngine.Tests.Sharding|FullyQualifiedName~ReplicationTickBenchmarkTests|FullyQualifiedName~AdminHttpServerTests)&Category!=LiveSocket'
```

- [ ] Leave `MmoServerSample`, `KhaozEngine.Server.AotProbe`, `KhaozEngine.Benchmarks` and `KhaozEngine.Sharding` sources unchanged. The first two already meet the send commitment, and the other two never send. The solution build in root's final gate compiles them.

- [ ] Commit only this task's files with `fix(replication): diff reliable state from last sent projection` and request its review gate.

### Task 2: Define immutable packet, projection, limits and publication seams

**Files:** Create `KhaozEngine.Replication/{ReplicationPacketId,ReplicationSequence,DeltaRebuildResult,DeltaRebuildOptions,ReplicationDeltaPacket,DeltaRebuildFailure,DeltaRebuildException,ReplicationProjection,ProjectionRetention,ProjectionStaging,ProjectionPublication}.cs`. Modify `ReplicationRegistry.cs` and make `ClientReplicationView.cs` partial. Create `ClientReplicationView.Publication.cs`. Create `KhaozEngine.Server.Tests/Replication/{DeltaRebuildContractTests,ProjectionRetentionTests,ProjectionPublicationTests,ProjectionDump}.cs`.

**Interfaces:** Produce the following public contract. `ReplicationDeltaPacket` construction stays internal and owns an immutable copied body. `EnvelopeBytes` is an uninterpreted byte charge, default zero for standalone users and set to 12 by NetWorld, so Replication enforces the complete object budget without knowing a transport or envelope format.

```csharp
public readonly record struct ReplicationPacketId(ulong Epoch, uint Sequence);
public enum DeltaRebuildResult { Accepted, DuplicateOrStale, MissingBaseline, Invalid }
public enum DeltaRebuildFailure { None, MalformedPacket, CapacityExceeded, RetentionPressure, SequenceAmbiguous }
public sealed class DeltaRebuildException : InvalidOperationException
{
    public DeltaRebuildFailure Failure { get; }
    public DeltaRebuildException(DeltaRebuildFailure failure, string message);
}
public sealed class DeltaRebuildOptions
{
    public int MaxRetainedProjections { get; init; } = 32;
    public int MaxRetainedPayloadBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxKeyframeBytes { get; init; } = 64 * 1024;
    public int MaxEntities { get; init; } = 1024;
    public int MaxComponents { get; init; } = 16384;
    public int EnvelopeBytes { get; init; }
    public int NoAckSendWindow { get; init; } = 31;
    public void Validate();
    public DeltaRebuildOptions WithEnvelopeBytes(int envelopeBytes);
}
public sealed class ReplicationDeltaPacket
{
    public ReplicationPacketId Id { get; }
    public ReplicationPacketId? Baseline { get; }
    public bool IsKeyframe { get; }
    public ReadOnlyMemory<byte> Bytes { get; }
}
```

`Validate()` throws `ArgumentOutOfRangeException` naming the first invalid property. `WithEnvelopeBytes` returns a validated copy with only the envelope charge replaced. Both are public because NetWorld must call them without an `InternalsVisibleTo` edge. `NoAckSendWindow` must be at least 1, and the effective window is `min(NoAckSendWindow, MaxRetainedProjections - 1)`. `ReplicationProjection`, `ProjectionRetention`, `ProjectionStaging` and `ProjectionPublication` are internal sealed classes, never public surface. Internal contracts are `bool ReplicationSequence.IsNewer(uint candidate,uint current)` and `bool IsAmbiguous(uint candidate,uint current)`, and `ReplicationProjection ReplicationProjection.Compact(Dictionary<long,CapturedComponents> source,DeltaRebuildOptions options)`. `ProjectionRetention(DeltaRebuildOptions options)` provides `bool TryGet(ReplicationPacketId id,out ReplicationProjection projection)`, `bool TryRetain(ReplicationPacketId id,ReplicationProjection projection,IReadOnlySet<ReplicationPacketId> prospectivePins,out DeltaRebuildFailure failure)` and `void Clear()`. TryRetain commits insertion, pins and pruning atomically. A failed retain leaves the previous cache/pins unchanged. Prospective pins are at most three ids and all are charged in the same table. Expose internal count, payload-byte, entity and component diagnostics for tests. Metadata must not use payload length as its only bound.

`ProjectionStaging` owns one private ECS World and net-id map. Produce internal `ProjectionPublication.Publish(World target,ClientReplicationView view,ReplicationProjection projection,ProjectionStaging staged)` and internal `void ClientReplicationView.PublishProjection(World target,ReplicationProjection projection,ProjectionStaging staged)` delegating to it. In `ComponentCodec`, add `Action<World,Entity,World,Entity> CopyComponent` capturing `T` at registration. Argument order is source world/entity, destination world/entity. Existing public registration signatures stay unchanged. Publication reconciles against the live view, not only the previously published projection: it despawns every net id in `view.Entities` absent from the new projection, and on each surviving entity removes every registered codec's component absent from the new set. A first keyframe over a world built by the legacy path therefore removes legacy-only entities and components. Unregistered game-local components are never touched.

- [ ] Add declarations and compile. Add `LimitsRejectTooFewPinsAndInvalidEnvelope`, `SequenceWrapIsNewerButHalfRangeIsInvalid` and `PacketOwnsItsInputBytes`. Validate positive byte/count limits, retained count at least 4, nonnegative envelope charge below the keyframe cap and nonzero epoch. Reject epoch zero at public stream setup, not at record construction.

```csharp
Assert.True(ReplicationSequence.IsNewer(0u, uint.MaxValue));
Assert.False(ReplicationSequence.IsNewer(9u, 9u));
Assert.True(ReplicationSequence.IsAmbiguous(0x80000000u, 0u));
Assert.Equal(32, new DeltaRebuildOptions().MaxRetainedProjections);
Assert.Equal(65536, new DeltaRebuildOptions().MaxKeyframeBytes);
```

- [ ] Add `ViewerCompactionDoesNotRetainHiddenBacking`, `OpaqueAndZeroByteFramesCountTowardLimits`, `PinsCountWithinBudget`, `SharedBackingIsChargedOnceAtItsCompleteSize` and `OldestUnpinnedInsertionIsPruned`. Use independent compact buffers, count backing allocations by reference identity, and charge full allocations if shared. The retained-byte diagnostic is exactly the sum of `Length` over the distinct arrays from `ProjectionRetention.BackingArraysForTest()`, and a projection sharing a baseline array adds nothing for it. Assert the viewer cannot reach another owner's raw backing bytes through retained segments.
- [ ] Add `PublicationReinstallsUnchangedAuthoritativeComponents`, `PublicationRemovesOnlyRegisteredAbsentComponents`, `SurvivingNetIdPreservesEntityHandle`, `RemovedSampleHistoryCannotResurrectComponent` and `FirstKeyframeOverLegacyWorldRemovesLegacyOnlyState`. The last builds a live world through legacy `ApplyDelta` with an entity absent from the keyframe and a registered component absent from a surviving entity, adds an unregistered game-local component to that survivor, publishes a keyframe projection, and asserts the absent entity is despawned, the absent registered component is removed and the game-local component remains. Interpolate or manually change live registered values before republishing. Preserve an unregistered game-local component on a surviving entity. A replacement removes every registered component absent in its new set, but not that game-local component.
- [ ] Build, then run behavioral RED:

```sh
cd "$DELTA_WORKTREE" && dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~DeltaRebuildContractTests|FullyQualifiedName~ProjectionRetentionTests|FullyQualifiedName~ProjectionPublicationTests'
```

- [ ] Implement immutable raw maps, compact viewer payload storage and insertion-ordinal pruning. Charge pinned ids within the same limits, including a candidate. If pins prevent a new server retention, report RetentionPressure. An impossible single projection reports CapacityExceeded. Do not enlarge limits, retain rejected packet maps or pin an entire hidden capture behind charged visible slices.
- [ ] Implement typed publication through staged component copies, preserving entity identity. Shift previous/current sample buffers once per publication, install all current sampled raw bytes, remove departed entity/component histories, and leave timestamp sampling to `IngestServerState`. Neither publication nor reconstruction independently calls `RecordInterpolationSample`. Publication copies each sampled payload into a fresh exact-length array before `SetCurrent`, matching the legacy readers. As an allowed allocation optimization it may keep the existing `currentBytes` array when the new payload is byte-equal, since that array is already a presentation copy. The ECS install still writes every authoritative component, because the spec rules out installing only changed bytes while presentation can have changed the live ECS. `currentBytes`, `previousBytes` and `sampleHistory` therefore never reference a retained projection backing array, so retention eviction always frees the backing, and presentation buffers are not charged to the retained-byte budget. Presentation history stays bounded by the existing `InterpolateAt` pruning and 600-sample cap, as the spec keeps interpolation samples separate from retained baselines. Publication working storage is separately capped at one projection.
- [ ] Add `PublicationSamplesNeverAliasRetainedBacking`. Publish two projections, record interpolation samples, then evict the first projection from retention. Assert by reference identity that `PresentationArraysForTest()` shares no array with `ProjectionRetention.BackingArraysForTest()` or the evicted projection's `BackingArraysForTest()`, and that the retained-byte diagnostic equals the sum of distinct retained backing array lengths.
- [ ] Rebuild and run the same filter to GREEN. Commit `feat(replication): add bounded immutable projection contracts` and review.

### Task 3: Build and commit acknowledged v2 sends in both writers

**Files:** Modify `KhaozEngine.Replication/{ServerReplicator,AoiDeltaReplicator}.cs` after Task 1. Create `RebuildDeltaWriter.cs`, `RebuildDeltaSlot.cs`, `RebuildDeltaEncoding.cs` and `RebuildUsage.cs`. Create `KhaozEngine.Server.Tests/Replication/{RebuildDeltaWriterTests,RebuildSentHistoryTests,RebuildCaptureScopeTests,RebuildWire}.cs`. Extend `LegacySequenceExhaustionTests.cs` with v2 reset assertions.

**Interfaces:** Consume Task 2 types and existing capture/project helpers. Produce on both writers `StartRebuild(int slot,ulong epoch,DeltaRebuildOptions options)`, `RecordRebuildSent(int slot,ReplicationPacketId id)` and `AcknowledgeRebuild(int slot,ReplicationPacketId id)`. AOI produces `ReplicationDeltaPacket BuildRebuildFor(int slot,World world,IReadOnlySet<long> interestSet,long? ownerNetId = null,bool keyframe = false)`. Whole-world produces `ReplicationDeltaPacket BuildRebuildFor(int slot,long? ownerNetId = null,bool keyframe = false)` using its current capture. Both expose `public bool RebuildNeedsRepair(int slot)`, using the effective no-ack window from the options passed to `StartRebuild`, and internal `RebuildUsage RebuildUsageForTest(int slot)`, internal `bool TryGetRetainedProjectionForTest(int slot,ReplicationPacketId id,out ReplicationProjection projection)` and internal `void SeedRebuildSequenceForTest(int slot,uint sequence)`, with `RebuildUsage` recording retained count/bytes, pending candidate id, acknowledged id and new sent count.

`RebuildUsage` is an internal readonly record struct with `int RetainedCount`, `int RetainedBytes`, `ReplicationPacketId? CandidateId`, `ReplicationPacketId? AcknowledgedId`, `int NewSentCount`, `int Entities` and `int Components`. The three non-packet methods above return void. Writer exceptions use Task 2's typed failure, never a transport type. NetWorld reads only `RebuildNeedsRepair` and `DeltaRebuildException.Failure`, never `RebuildUsageForTest`.

Repair contract. Every v2 recovery trigger has exactly one signal and one stream action. "Keyframe repair" means: allocate a new epoch, call `StartRebuild` with it, build `keyframe: true`, and run the Task 7b barrier. A failed `BuildRebuildFor` leaves the writer slot unchanged.

| Trigger | Detected by | Signal | Stream action |
| --- | --- | --- | --- |
| No-ack window consumed, effective window committed sends without a newer acknowledgement | Writer | `RebuildNeedsRepair(slot)` true, checked before every build | Keyframe repair |
| Pins would block retaining the next candidate | Writer | `RebuildNeedsRepair(slot)` true when predictable, otherwise `DeltaRebuildException` with `RetentionPressure` | Keyframe repair |
| Next sequence or baseline is half-range ambiguous | Writer | `DeltaRebuildException` with `SequenceAmbiguous` | Keyframe repair |
| Projection exceeds `MaxKeyframeBytes` with envelope, `MaxEntities` or `MaxComponents` | Writer | `DeltaRebuildException` with `CapacityExceeded`, delta or keyframe | Typed disconnect `ke:replication-capacity-exceeded` |
| Complete framed routine delta exceeds the selected cap | Stream | Framed length check before send, candidate never recorded | Keyframe repair |
| Client repair request A3 for the current epoch | Stream | Valid decoded control | Keyframe repair, coalesced into an active barrier |
| Actual limit drops below the epoch's selected cap but stays feasible | Stream | Limit query before each send | New mode offer, new epoch and width, then keyframe |
| Actual limit drops below the feasible minimum of 281 for a 64 KiB keyframe, or becomes 0 on a live v2 stream | Stream | Limit query before each send | Disconnect with `ke:replication-restart` per ruling O2.15, so the reconnect gets mode 0 reason 1. Never an in-place legacy downgrade |
| Negotiation or barrier exceeds `RecoveryDeadlineTicks` | Stream | Cadence tick count | Typed disconnect `ke:replication-recovery-failed` |
| `TrySendTo` false or a thrown send | Stream | Send result or `Faulted` flag | Disconnect with `ke:replication-restart`, per ruling O2.14 |
| Malformed client body or unknown built-in | Client | `DeltaRebuildResult.Invalid`, `LastFailure` `MalformedPacket` | Existing incompatible-decode disconnect |
| Client cannot retain within limits | Client | `LastFailure` `CapacityExceeded` | Typed disconnect `ke:replication-capacity-exceeded` |
| Replacement offer outside client limits or with an infeasible width | Client | Offer validation | Typed disconnect `ReplicationPolicyRefused`, no retry |
| Valid replacement offer with a greater epoch | Client | Offer validation | Adopt new width and epoch, send A1, keep the published world until the new keyframe publishes |

- [ ] Add compiling writer declarations and tests for both adapter shapes. `BuildRebuildFor` requires capture/BeginTick, a previously started nonzero epoch and a stable owner id for that epoch. StartRebuild rejects non-increasing replacement epochs for a retained slot. It clears retired slot v2 state, not the writer's global signed counter.
- [ ] Add `EmptyAckRelativeReversionPacketNamesOriginalBaseline`, `UnsentCandidateCannotBeAcknowledged`, `OnlyNewestUnsentCandidateIsRetained` and `AckMustBeSentToThisSlotAndStillRetained`. Acknowledgement of another slot's shared capture is invalid. Include stale, duplicate, future, pruned and retired-epoch acks.

```csharp
// initial: keyframe with value 1, committed and acknowledged.
// intermediate: value 2, committed, its ack withheld.
// reverted: value 1, built and never recorded as sent.
Assert.Equal(initial.Id, reverted.Baseline);
Assert.Equal(0, RebuildWire.ReadHeader(reverted.Bytes.Span).ChangedCount);
writer.AcknowledgeRebuild(slot, reverted.Id);
RebuildUsage usage = writer.RebuildUsageForTest(slot);
Assert.Equal(initial.Id, usage.AcknowledgedId);
Assert.Equal(reverted.Id, usage.CandidateId);
Assert.Equal(3, usage.RetainedCount);
Assert.Equal(1, usage.NewSentCount);
Assert.Equal(ProjectionDump.DistinctBackingBytes(RetainedOf(writer, slot, initial.Id, intermediate.Id, reverted.Id)), usage.RetainedBytes);
```

- [ ] Add `AckOrderCannotRegressAcrossUnsignedWrap`, `PinnedBaselineStartsRepairBeforeSend32`, `PayloadPressureRequestsRepairBeforeCapacity`, `KeyframeBudgetIncludesEnvelopeBytes`, `CaptureIsSharedAcrossLegacyAndV2Viewers` and `TwoWorldsEachScanOnce`. Seed unsigned sequence through `SeedRebuildSequenceForTest`, not a large loop. Assert persisted viewer storage is owner compact and Persist/Migrate-only bytes never enter a baseline.
- [ ] Build, then run behavioral RED:

```sh
cd "$DELTA_WORKTREE" && dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~RebuildDeltaWriterTests|FullyQualifiedName~RebuildSentHistoryTests|FullyQualifiedName~RebuildCaptureScopeTests|FullyQualifiedName~LegacySequenceExhaustionTests'
```

- [ ] Encode format 2, flags 0 or 1, nonzero epoch, uint snapshot/baseline and unchanged entity/component sections. First packet or explicit keyframe starts from empty, has baseline field zero, zero removed entities, full entries and zero component removals. Preserve capture/registration ordering, zero-byte tags and raw captured bytes. No last-sent presence patch participates in v2.
- [ ] Keep one unsent candidate. Snapshot sequence is an independent per-slot uint serve identity, starts at 1 and wraps safely. `RecordRebuildSent` commits the exact candidate after successful transport handoff only. It throws on a different id, never manufactures a sent state and pins a keyframe until its exact ack. Acknowledgements promote only a strictly newer retained committed id. Use insertion order for eviction, not numeric uint sort.
- [ ] Enforce complete keyframe encoded size plus `EnvelopeBytes` before retaining a projection. Distinguish impossible projection CapacityExceeded from retention pressure. A retained-count limit smaller than 32 shortens the effective no-ack window to `min(configuredWindow, MaxRetainedProjections - 1)`. Start repair before building the next send when that window has been consumed. Half-range ambiguity reports SequenceAmbiguous and requires a new epoch.
- [ ] Extend Forget and the global exhaustion reset to clear all candidate, acknowledged, pinned repair and committed history state. A reset does not itself remember or allocate a fresh v2 epoch. Task 7c's server-lifetime allocator proves the owner supplies a greater epoch after reset, without an unbounded retired-slot table inside Replication.
- [ ] Rebuild and run the same filter to GREEN, then `FullyQualifiedName~AoiDeltaReplicatorSharedCaptureTests|FullyQualifiedName~ReplicationCaptureAllocationTests` once to catch capture regressions. Commit `feat(replication): add acknowledged rebuild writers` and review.

### Task 4: Reconstruct, validate, retain and publish v2 client projections

**Files:** Create `KhaozEngine.Replication/{ClientDeltaRebuild,RebuildDeltaReader}.cs`. Extend Task 2 `ProjectionStaging.cs` and `ClientReplicationView.Publication.cs` only within the Replication lane. Create `KhaozEngine.Server.Tests/Replication/{ClientDeltaRebuildTests,RebuildMalformedFrameTests,RebuildClientRetentionTests,CountingCodec}.cs`.

**Interfaces:** Consume Tasks 2 and 3 packet layouts. Produce:

```csharp
public sealed class ClientDeltaRebuild
{
    public ClientDeltaRebuild(ReplicationRegistry registry, ClientReplicationView view, DeltaRebuildOptions options);
    public ReplicationPacketId? LatestAcceptedId { get; }
    public ReplicationPacketId? AckTarget { get; }
    public DeltaRebuildFailure LastFailure { get; }
    public void ExpectEpoch(ulong epoch);
    public DeltaRebuildResult TryApply(World world, ReadOnlyMemory<byte> packet,
        out ReplicationPacketId acceptedId, out ReplicationPacketId? missingBaseline, out string? error);
    public void Reset();
}
```

`AckTarget` is the retained id that routine acks advertise, null before the first accept. Because the latest publication is pinned, it equals `LatestAcceptedId` after every Accepted result. A packet that cannot be retained within limits is not accepted and sets `LastFailure` to `CapacityExceeded`. `ExpectEpoch` authorizes an epoch learned from a reliable mode offer or reliable chunk header. It retires older datagrams while retaining the last published live state until a validated keyframe arrives. It rejects zero and non-increasing replacement epochs, and allows repeated expectation of the same pending initial offer. An unknown-epoch datagram cannot call it or establish a stream. The constructor calls `options.Validate()`. Internal read diagnostics are exactly the `ClientDeltaRebuild` seams in the helper contract table. NetWorld never reads them, because it has no `InternalsVisibleTo` access to Replication.

- [ ] Add compiling declarations. Add `EmptyDeltaRestoresExactNamedBaseline` with value 1, accepted intermediate 2 and empty delta from the retained baseline. Add `RebuildPresenceMatrix` covering all five rows in the spec, with both delivered and dropped intermediates. Include unsampled values, zero-byte tags and opaque unknown extensions in retained equality.

```csharp
Assert.Equal(DeltaRebuildResult.Accepted, result);
Assert.Equal(1, live.Get<Value>(entity).Number);
Assert.Equal(serverProjectionBytes, retainedProjectionBytes);
Assert.Equal(expectedNetIds, view.Entities.Keys.Order());
```

- [ ] Add `FullEntryReplacesEntireRegisteredSet`, `InterpolatedLiveWorldIsNeverTheBaseline`, `MissingBaselineLeavesEverythingUnchanged`, `StaleBodyIsNotDecoded`, `OldEpochCannotRestartPublication`, `WrapAcceptsZeroAndRejectsHalfRange` and `UnknownEpochDatagramCannotEstablishStream`. MissingBaseline supplies the exact missing id but changes no ECS, samples, pins or accepted id. Duplicate/stale classification occurs after a valid fixed header and before body codecs. A malformed current/new body is Invalid.
- [ ] Add malformed theories for negative/excessive counts, duplicate removed or changed net ids, overlap between removed and changed ids, duplicate component ids/removals, overlap between removed and replacement type ids, bad flags/revision, unknown built-in, malformed/overlong 7-bit length, framed overread or underread, missing entity terminator and trailing bytes. Keyframe theories reject partial entries and nonempty entity/component removal sections. Add `ReadCountsMatchReconstructedProjection` using `CountingCodec` and its exact oracle: per accepted packet the read counters advance by the number of known components in the reconstructed projection, and publication adds zero.
- [ ] Build, then run behavioral RED:

```sh
cd "$DELTA_WORKTREE" && dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ClientDeltaRebuildTests|FullyQualifiedName~RebuildMalformedFrameTests|FullyQualifiedName~RebuildClientRetentionTests'
```

- [ ] Reconstruct privately from the exact immutable baseline, or empty for a keyframe. Apply removals, full entity replacement and component replacements to a copied/shared raw map. Decode received known replacement components once into one staging world to establish builtin boundaries and validate framed values. After constructing the final map, decode only its remaining known baseline components into staging. Staging is rebuilt per packet, so each known component in the reconstructed projection is read exactly once per accepted packet. Publication uses typed copies, not a second wire read. Unknown extensions retain exact opaque payloads. No reader writes the live world before every count, id, length, terminator and complete-consumption check passes.
- [ ] Validate counts before allocating and cap raw payload, map metadata, staging and complete keyframe charge. On an invalid body, clear staged frame references and leave publication unchanged. `LastFailure` differentiates malformed bytes from capacity exhaustion. Legacy reader behavior stays available independently.
- [ ] Retain before publication/ack. Pin latest publication, newest retained ack target and newest confirmed server baseline from accepted packets within the same count/byte limit. Prune other ids by insertion ordinal. A later use of a pruned server baseline is a recoverable miss. Refresh previous/current sample buffers exactly once on publication, and let NetWorld stamp one sample after acceptance.
- [ ] Rebuild and run the same filter to GREEN. Commit `feat(replication): reconstruct projections before publication` and review.

### Task 5: Add generic packet queries, send commitments and bounded reliable assembly

**Files:** Modify `KhaozEngine.Netcode/{INetTransport,NetClient,NetServer,MessageReassembler}.cs`, `KhaozEngine.Netcode.LiteNetLib/{LiteNetLibClientTransport,LiteNetLibServerTransport}.cs`. Create `KhaozEngine.Server.Tests/Netcode/{TransportPayloadLimitTests,SessionSendCommitTests,MessageReassemblerLimitTests,LiteNetLibPayloadLimitTests}.cs`. The last is `[Trait("Category", "LiveSocket")]` and uses `LiveSocketSupport`. No new Replication or backend edge in Netcode. Leave `MessageFragmenter` wire and `ChannelSplitter` mapping unchanged.

**Interfaces:** Add the default `int INetTransport.MaxUnfragmentedPayloadBytes(NetConnectionId connection,NetChannelReliability reliability) => 0`. Facades expose `int NetClient.MaxUnfragmentedPayloadBytes(NetChannelReliability reliability)` and `int NetServer.MaxUnfragmentedPayloadBytes(int slot,NetChannelReliability reliability)`. Add `bool NetClient.TrySend(ReadOnlySpan<byte> payload,NetChannelReliability reliability)`, `bool NetServer.TrySendTo(int slot,ReadOnlySpan<byte> payload,NetChannelReliability reliability)` and `void NetClient.Disconnect()`. Existing void sends forward to the new variants and preserve their absent-connection no-op behavior. `TrySend` and `TrySendTo` return false only for an absent connection. Otherwise they call `INetTransport.Send` once and return true when it returns. They never catch, so a thrown exception propagates unchanged and is never a commitment. True means handed to the transport, not delivered, because both backends also drop an unknown peer silently. `NetClient.Disconnect()` calls `INetTransport.Disconnect` for a valid server connection, then sets that connection to none so a later `TrySend` returns false. The transport's later Disconnected event still surfaces as today.

Add `MessageReassembler(int slot,int chunkPayloadBytes,int maxAssembledBytes,int maxPartialAssemblies)` beside the unchanged two-argument constructor. Default construction still uses `MaxPayloadBytes(width)` and four partial assemblies. Add public `MaxAssembledBytes`, `PartialAssemblyLimit` and refusal token `PayloadLimitExceeded = "ke:fragment-payload-limit"`. No wire-header change or unreliable assembly support. Update the `MessageReassembler` XML docs: the class remarks currently say the partial bound "is a hard constant with no constructor knob", which becomes false. Redocument `MaxPartialAssemblies` as the default partial-assembly limit used by the two-argument constructor, and document the four-argument constructor's bounds and refusal token.

- [ ] Add compiling declarations and fake transport tests. Query unknown/absent connection as zero, forward real renumbered connection identity and reliability, and report send success only after `INetTransport.Send` returns. A thrown send is not a successful commitment. Disconnect targets the actual server peer and clears its local connection send eligibility.
- [ ] Add `DefaultTransportLimitIsUnknown`, `FacadeQueriesActualConnectionAndChannel`, `MissingSlotDoesNotCommitSend`, `SendExceptionCannotCommit`, `BoundedAssemblyRejectsBeforeExcessCopy` and `SingleAssemblyLimitPreservesDefaultConstructor`. Pin unchanged chunk golden bytes and drop/reset behavior.

```csharp
Assert.Equal(0, externalTransport.MaxUnfragmentedPayloadBytes(connection, reliability));
Assert.False(server.TrySendTo(missingSlot, payload, reliability));
Assert.Equal(65536, bounded.MaxAssembledBytes);
Assert.Equal(1, bounded.PartialAssemblyLimit);
Assert.Equal("ke:fragment-payload-limit", reason);
```

- [ ] Build, then run behavioral RED:

```sh
cd "$DELTA_WORKTREE" && dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~TransportPayloadLimitTests|FullyQualifiedName~SessionSendCommitTests|FullyQualifiedName~MessageReassemblerLimitTests'
```

- [ ] Implement forwarding and backend queries against the `NetPeer` from `peersById` with `GetMaxSinglePacketSize(ChannelSplitter.ToDeliveryMethod(reliability))`. If the pinned `NetPeer` does not expose the documented `LiteNetPeer` member, stop for root rather than adding a backend type to the seam. Missing peers return zero. `LiteNetLibPayloadLimitTests` asserts a connected peer reports a positive limit for both reliabilities and an unknown connection reports zero. It is excluded from the default suite, and root runs it once with `--filter 'Category=LiveSocket&FullyQualifiedName~LiteNetLibPayloadLimitTests'` or reports backend forwarding as unverified. Read the pinned XML before compiling. Do not add a socket probe, package upgrade, guessed MTU or backend DTO to the seam. In-memory transport remains unknown by default, while Task 9's decorator explicitly supplies a test limit.
- [ ] Bound reassembler backing growth to `maxAssembledBytes`, including spare capacity, and check needed length before copying. A declared chunk count cannot allocate its full advertised size. Maintain sequential reliable chunk checks, cap partial count by the constructor limit, and clear on connection teardown. Task 8 will validate the outer total length before calling it.
- [ ] Rebuild and run the same filter to GREEN, then `FullyQualifiedName~MessageFragmenterTests|FullyQualifiedName~NetSessionTests|FullyQualifiedName~RejectDeliveryTests|FullyQualifiedName~ChannelSplitterTests` once. Existing reassembler cases live in MessageFragmenterTests. Commit `feat(netcode): expose payload limits and bounded reliable assembly` and review.

### Task 6: Define NetWorld format 2 controls, options and typed state

**Files:** Modify `KhaozEngine.NetWorld/MoveProtocol.cs` only for additive enum members. Create `KhaozEngine.NetWorld/{RebuildProtocol,ReplicationStreamOptions,ReplicationSelection,ReplicationFailure,ReplicationCadence,ReplicationEpochAllocator,ReplicationAdmissionGate}.cs`. Modify `DisconnectReason.cs` and `ConnectRefusal.cs`. Config additions in `WorldServer.cs`, `ShardedWorldServerConfig.cs` and `WorldClientConfig.cs` remain in this lane. Create `KhaozEngine.Server.Tests/NetWorld/{RebuildProtocolTests,ReplicationStreamOptionsTests,ReplicationCadenceTests,ReplicationFailureTests}.cs`.

**Interfaces:** Add `AllowUnreliableDeltaReplication` to both server configs and `RequestUnreliableDeltaReplication` to client config, all false. Add `ReplicationStreamOptions ReplicationStream { get; init; } = new()` to all three configs. Its exact members are `DeltaRebuildOptions Limits = new()`, `int MaxTransportPayloadBytes = 512`, `int MaxChunksPerTick = 4`, `int RepairRequestIntervalTicks = 30` and `int RecoveryDeadlineTicks = 90`, all init properties. The no-ack window lives in `Limits.NoAckSendWindow`, default 31, and reaches the writer through `StartRebuild`. NetWorld requires `Limits.EnvelopeBytes` to be 0 or 12 and derives `Limits.WithEnvelopeBytes(12)`. Any other value is rejected by config validation, never silently replaced. Reliable-only config validation does not force v2 budgets on a legacy caller.

Public selection types are `ReplicationDeliveryMode { LegacyReliable = 0, AcknowledgedUnreliable = 1 }`, `ReplicationSelectionReason { Unnegotiated, Selected, UnavailableTransportLimit, DisabledServerPolicy }` and `readonly record struct ReplicationSelection(ReplicationDeliveryMode Mode,ReplicationSelectionReason Reason,ulong Epoch)`. Add `DisconnectReason.ReplicationPolicyRefused`, `ReplicationRecoveryFailed`, `ReplicationCapacityExceeded` and `ReplicationRestart`, appended after the existing members. Stable tokens are `ke:replication-policy-refused`, `ke:replication-recovery-failed`, `ke:replication-capacity-exceeded` and `ke:replication-restart`. `ConnectRefusal.Read` maps the first three exactly to their reasons with `RetryRule.Never` and empty detail, and maps `ke:replication-restart` to `ReplicationRestart` with `RetryRule.Backoff`, the rule `AlreadySignedIn` uses. Without that mapping an unknown token becomes `RejectedToken` with `WhenRetryOnReject`, and a client with retry-on-reject off would stop instead of reconnecting through its fresh-view path. The restart retry then follows the consumer's `ReconnectBackoff`, whose default `MaxAttempts = 0` means unlimited spaced attempts. The engine adds no retry loop of its own. A client-detected failure sets the same reason locally and never retries the terminal three.

`ReplicationFailure.cs` defines internal `readonly record struct ReplicationFailure(DisconnectReason Reason,string Detail)` and constants for those stable tokens. Mode-offer wire reasons map explicitly from 0/1/2 to Selected/UnavailableTransportLimit/DisabledServerPolicy. Do not serialize the public Unnegotiated enum ordinal as the wire reason.

Internal protocol records are `ReplicationModeOffer(byte Format,ReplicationDeliveryMode Mode,byte Reason,ushort HistoryCount,uint HistoryPayloadBytes,uint MaxKeyframeBytes,uint EntityLimit,uint ComponentLimit,uint PacketCap,ushort ChunksPerTick,ulong Epoch)` and `RebuildClientControl(RebuildControlKind Kind,ulong Epoch,uint SnapshotSequence,uint MissingBaselineSequence)`. `RebuildControlKind` is Accept A1, Acknowledge A2, Repair A3. `ControlReadResult` is Unclaimed, Valid, Malformed.

`RebuildProtocol` provides `byte[] EncodeModeOffer(in ReplicationModeOffer offer)`, `bool TryDecodeModeOffer(ReadOnlySpan<byte> body,out ReplicationModeOffer offer)`, `byte[] EncodeAcceptance(ulong epoch)`, `byte[] EncodeAck(ReplicationPacketId id)`, `byte[] EncodeRepair(ulong epoch,uint lastAccepted,uint missingBaseline)`, and `ControlReadResult DecodeClientControl(ReadOnlySpan<byte> data,out RebuildClientControl control)`. Frame helpers provide `byte[] EncodeDelta(long localNetId,int movementAck,ReplicationDeltaPacket packet)` and `byte[] EncodeKeyframeChunk(ReplicationPacketId id,uint totalBytes,ReadOnlySpan<byte> genericChunk)`. Returned server frames include the kind byte but not SessionFrame.

Internal `ReplicationCadence.Advance(float elapsedSeconds)` returns bool for at most one scheduled allowance per host call, increments `long Tick`, and keeps fractional elapsed time. It accounts for skipped elapsed cadence boundaries in Tick/deadlines without manufacturing a burst of sends. `ReplicationEpochAllocator.TryNext(out ulong epoch)` returns a checked nonzero server-lifetime ulong, never wraps and is not touched by a writer reset. It exposes `ulong HighWater` and internal `void SeedForTest(ulong lastIssued)`, and once `ulong.MaxValue` has been issued `TryNext(out ulong epoch)` returns false, so the host keeps admission closed. `ReplicationAdmissionGate` wraps the result of `WireGenerationAuthenticator.Install(authenticator)` and implements `IConnectionDisplayName` and `IConnectionPersistenceKey` by forwarding `inner is IConnectionDisplayName named ? named.ReadDisplayName(token) : string.Empty` and the matching `IConnectionPersistenceKey` expression, as `WireGenerationAuthenticator` does. While closed, `TryAuthenticate` rejects with `ke:replication-restart` without calling the inner authenticator.

- [ ] Add declarations and compile. Add protocol golden round trips for the spec field order, sizes and zero-limit mode 0. Reject unknown format, mode/reason, zero v2 epoch, overflowed/invalid limits and trailing bytes.
- [ ] Add `EveryLength18ControlAliasIsUnclaimed` for valid MOVE sequences A1C5, A2C5 and A3C5, and sender-intended truncated controls with the same valid MOVE fields. Add `RecognizedMalformedNon18ControlNeverFallsThrough` for all three families, including 19-byte bad-revision repair and overlong A2 ack. Assert valid 11-byte accept, 14-byte ack and 19-byte repair have exactly one decoded family.

```csharp
Assert.Equal(ControlReadResult.Unclaimed, RebuildProtocol.DecodeClientControl(move18, out _));
Assert.Equal(ControlReadResult.Malformed, RebuildProtocol.DecodeClientControl(badRepair19, out _));
Assert.Equal(11, RebuildProtocol.EncodeAcceptance(1).Length);
Assert.Equal(14, RebuildProtocol.EncodeAck(new(1, 1)).Length);
Assert.Equal(19, RebuildProtocol.EncodeRepair(1, 1, 0).Length);
Assert.Equal(13, MoveProtocol.WireProtocolVersion);
```

- [ ] Add options tests for minimum four states, all concrete defaults, configured uint/ushort field fit, positive finite TickSeconds when v2 enabled, and keyframe feasibility `ceil(MaxKeyframeBytes / (PacketCap - 23)) <= 255`. Default cap 512 gives width 489, 135 chunks and 34 scheduled ticks. For a 64 KiB cap, transport cap 281 is the smallest feasible cap, with width 258 and 255 chunks. A smaller negotiated cap cannot select v2. No silent increase.
- [ ] Add `ShortFramesAccumulateWithoutExtraAllowances`, `LongFrameSkipsBacklogButAdvancesDeadlines`, `ZeroTimeDrainConsumesNoBudget`, `TypedRefusalsAreTerminal` and `AdmissionWrapperPreservesVerifiedClaims`. Test the allocator at `ulong.MaxValue` through `SeedForTest`, refusing the next epoch rather than reusing zero. Add `ReplicationTokensMapToTypedReasons` asserting the four `ConnectRefusal.Read` mappings and retry rules.
- [ ] Build, then run behavioral RED:

```sh
cd "$DELTA_WORKTREE" && dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~RebuildProtocolTests|FullyQualifiedName~ReplicationStreamOptionsTests|FullyQualifiedName~ReplicationCadenceTests|FullyQualifiedName~ReplicationFailureTests'
```

- [ ] Implement length-first tri-state control decoding. Exclude length 18 before marker reads even in malformed-recognized paths. A recognized A1/A2/A3 on another length claims and validates its exact length and revision where present. Do not change legacy A0, B0 or MOVE encodings. Update stale source comments that equate ack-relative overlay with reliable recovery only when owned by a task touching that method.
- [ ] Implement option validation, exact format 2 encoders and typed refusal mapping. Mode 0 has epoch/limits zero, wire reason 1 for unknown/unusable transport limit or 2 for server policy. Silence from an older server preserves reliable mode with Unnegotiated reason. Mode 1 offers the server-selected limits and requires acceptance, not client-supplied counters.
- [ ] Rebuild and run the same filter to GREEN. Commit `feat(networld): define negotiated rebuild protocol and policy` and review.

### Task 7a: Route controls, negotiate, offer and accept

**Files:** Modify `KhaozEngine.NetWorld/{WorldServer,ShardedWorldServer,WorldServer.Sessions,ShardedWorldServer.Sessions}.cs` for dispatch calls, slot cleanup and test seams only. Create `KhaozEngine.NetWorld/{WorldServer.Replication,ShardedWorldServer.Replication,RebuildServerStream}.cs`. Create `KhaozEngine.Server.Tests/NetWorld/{WorldServerRebuildTests,ShardedWorldServerRebuildTests,ReplicationControlAliasTests,ReplicationCapabilityMatrixTests}.cs`. The two host files hold per-host offer, acceptance and selection cases. Task 7a stops at routing, aliases, negotiation, the offer and its acceptance. It serves no v2 state, because steady state needs Task 7b's keyframe barrier. Existing source-local `InterestVisibility` and shard owner resolution remain unchanged.

**Interfaces:** Consume Tasks 1, 3, 5 and 6. Internal `RebuildServerStream` has constructor `(NetServer net,int slot,AoiDeltaReplicator writer,ReplicationStreamOptions options,ReplicationEpochAllocator epochs)`, `void OnRebuildCapability(long replicationTick)`, which selects mode 0 or 1 and hands the reliable mode offer to `TrySendTo`, `void HandleControl(in RebuildClientControl control,NetChannelReliability reliability,long replicationTick)`, `void Serve(World world,IReadOnlySet<long> interest,long ownerNetId,int movementAck,long replicationTick)`, `void Forget()` and read-only `ReplicationSelection Selection`. It owns one viewer's selected limits, frozen candidate and chunk cursor, not movement or visibility policy. Both servers expose `public bool TryGetReplicationSelection(int slot,out ReplicationSelection selection)`. Each new host partial supplies internal `AoiDeltaReplicator? DeltaReplicatorForTest`, `ulong ReplicationEpochHighWaterForTest` and the `ServeObservedForTest` callback from the helper contract table.

- [ ] Add declarations and compile. Add a fake limit-providing transport with `TrySendTo` success/failure evidence.
- [ ] Add `ReplicationCapabilityMatrixTests` for the spec's four-way matrix on each host, driven by raw client controls: server default with client default, server opt-in with client default, server default with client opt-in, and both opt-in, all at the current builtin generation. Expected selections are legacy reliable with no offer, legacy reliable with no offer, mode 0 reason 2 `DisabledServerPolicy`, and mode 1 `Selected`. Add known and unknown transport limits, where unknown gives mode 0 reason 1 `UnavailableTransportLimit`, and accepting and refusing clients. The server stops legacy sends once the mode 1 offer is handed to reliable transport and does not resume them while awaiting acceptance and the keyframe ack. Builtin-generation skew keeps its existing authenticator rejection. Task 8 repeats the matrix with real `WorldClient`s.
- [ ] Add real-host alias theory in `ReplicationControlAliasTests` with fresh sessions for each host and initial MOVE sequence `0x0000A1C5`, `0x0000A2C5`, `0x0000A3C5`. Submit finite nonzero axes/yaw, one scheduled receive/simulation step, and assert position plus movement ack prove `commands.Store` consumption. Test valid length-18 truncated-control bytes as ordinary MOVE, and NaN/Inf variants as malformed MOVE. Non-18 recognized malformed controls change no queue, mode, ack or barrier state. Valid controls must arrive on their specified channel. Existing rate limiting still runs first.
- [ ] Add `AcceptanceStopsLegacyAndAwaitsKeyframe`: after a valid A1 for the offered epoch, the server sends no legacy frame and no routine v2 delta, and the selection reads mode 1 `Selected` with that epoch.
- [ ] Build, then run behavioral RED:

```sh
cd "$DELTA_WORKTREE" && dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~WorldServerRebuildTests|FullyQualifiedName~ShardedWorldServerRebuildTests|FullyQualifiedName~ReplicationControlAliasTests|FullyQualifiedName~ReplicationCapabilityMatrixTests'
```

- [ ] Route length 18 to the existing MOVE validation/store branch immediately after the normal rate limiter. For other lengths, handle tri-state new controls before MOVE fallback, then retain legacy control/ack/game-message handling. Invalid recognized controls raise MalformedPacket and return. Do not widen game-message capabilities.
- [ ] Add the elapsed replication cadence object to both hosts, advanced once per host `Tick`, while preserving current reliable-only simulation, input and hook behavior. It feeds negotiation deadlines here and serve allowances in Task 7b.
- [ ] Negotiate: derive the selected cap as the minimum of the configured cap and the actual limits for unreliable state and reliable chunks at offer time. Unknown queries or infeasible chunk capacity select mode 0 reason 1. Record acceptance of the exact offered epoch and stop legacy serving for that slot. Steady v2 serving is Task 7b's.
- [ ] Rebuild and run the same filter to GREEN, then once `FullyQualifiedName~WorldServerDeltaTests|FullyQualifiedName~ShardedWorldServerDeltaTests|FullyQualifiedName~HighSlotInputTests|FullyQualifiedName~CommandRateAccountingTests|FullyQualifiedName~VersionHandshakeTests`. GREEN here proves routing, aliases, negotiation, offer and acceptance only. Commit `feat(networld): route and negotiate unreliable delta streams` and review.

### Task 7b: Run the keyframe barrier, steady serve, chunk budget, limit changes, deadlines and failures

**Files:** Modify `KhaozEngine.NetWorld/{RebuildServerStream,WorldServer.Replication,ShardedWorldServer.Replication}.cs` after Task 7a. Create `KhaozEngine.Server.Tests/NetWorld/{ReplicationBarrierTests,ReplicationSteadyServeTests,ReplicationLimitChangeTests,ReplicationServerFailureTests}.cs`.

**Interfaces:** Consume Task 3's repair contract table and Task 5's facades. No new public members. The stream's internal `bool Faulted` and `int ChunksSentThisCadence` are friend-test diagnostics.

Send failure rule, per orchestrator ruling O2.14: the stream calls `RecordRebuildSent` only after `TrySendTo` returned true for the routine delta or for every keyframe chunk. A false return disconnects the slot with `ke:replication-restart`, so the client takes the backoff reconnect path with a fresh view, and forgets its stream state. A thrown send is not caught. The stream sets `Faulted` in a `finally` block before the exception leaves, never commits the candidate, and the next serve or `Left` for that slot disconnects with `ke:replication-restart` and forgets it. An uncommitted candidate can never be acknowledged, so the client keeps reconstructing from committed baselines until that disconnect.

Limit-change rule: the stream queries the actual limit before every send. Only a drop below the epoch's selected cap renegotiates, with a new reliable offer, new epoch and new width. A rise, such as MTU discovery growth, never forces a new offer, and the epoch keeps its selected cap. A drop below the feasible minimum, 281 bytes for a 64 KiB keyframe, or to 0 on a live v2 stream, disconnects with the `ke:replication-restart` backoff token, per ruling O2.15. The reconnect then negotiates against the reduced limit and receives the spec's reliable fallback, mode 0 reason 1. A live receiver is never downgraded to legacy in place, because its world was built by v2 publication. `ke:replication-capacity-exceeded` stays reserved for the approved terminal case, an impossible projection size.

- [ ] Add `KeyframeChunksFollowCadenceBudget`, `OversizeDeltaStartsOneFrozenKeyframe`, `NoDeltaBeforeExactReliableKeyframeAck`, `ChangesDuringBarrierCollapseIntoNewestProjection`, `RequestFloodDoesNotRestartActiveBarrier`, `NeverSentKeyframeAckCannotPromote`, `RepairContractTriggersMapToOneAction` and `RepairWithUnchangedLimitsSendsNoOffer`. `RepairContractTriggersMapToOneAction` covers only the writer and stream rows of the repair contract table. The client rows get Task 8's `ClientRepairRowsMapToOneAction`. Inspect complete transport payload lengths. A mode offer waits for acceptance before chunks.

```csharp
// Single-viewer fixture: a 135-chunk keyframe at cap 512, four chunks per cadence tick.
// lastChunkBytes is the object length minus 134 * 489.
Assert.Equal(135, keyframeChunks.Count);
Assert.Equal(Enumerable.Repeat(4, 33).Append(3), chunksPerCadenceTick);
Assert.All(keyframeChunks, c => Assert.Equal(c.IsLast ? 23 + lastChunkBytes : 512, c.Payload.Length));
Assert.Empty(deltasWhileBarrierActive);
Assert.Equal(oldEpoch + 1, repairEpoch);
```

- [ ] Add `SteadyServeSendsOneStatePerCadenceTick`, `ShortShardedFramesSpendNoExtraAllowance` and `MixedLegacyAndV2ViewersShareOneCapture` in `ReplicationSteadyServeTests`, moved here from 7a by ruling because they need the barrier. After the keyframe ack, over three cadence ticks with changing state, assert exactly three routine unreliable state sends per v2 viewer and exactly one `WorldScanCount` increment per world per capture tick. Short sharded frames with no simulation step spend no extra allowance.
- [ ] Add `LimitGrowthKeepsEpochAndCap`, `LimitDropBelowCapRenegotiatesWithNewWidth`, `LimitDropBelowFeasibleDisconnectsWithRestart` and `LiveStreamNeverDowngradesToLegacy`. For a drop from 512 to 400, assert one new offer with packet cap 400, chunk width 377 and a new epoch. For growth from 512 to 1400, assert zero new offers and every later payload at most 512. For a drop to 280, assert the slot disconnects with `ke:replication-restart` and receives no legacy frame, and that a fresh join against the 280 limit receives mode 0 reason 1.
- [ ] Add `SendFalseDisconnectsWithRestart`, `ThrownSendFaultsAndDisconnectsWithRestartNextServe`, `CapacityExceededDisconnectsTyped` and `RecoveryDeadlineDisconnectsAtTick90`. The deadline case asserts no disconnect at cadence tick 89 and `ke:replication-recovery-failed` at tick 90.
- [ ] Build, then run behavioral RED:

```sh
cd "$DELTA_WORKTREE" && dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ReplicationBarrierTests|FullyQualifiedName~ReplicationSteadyServeTests|FullyQualifiedName~ReplicationLimitChangeTests|FullyQualifiedName~ReplicationServerFailureTests'
```

- [ ] Serve on the cadence: open one writer capture tick for the host serve pass and share it among mixed legacy and v2 viewers. V2 routine sends and chunks spend allowance only at a cadence boundary, even on short sharded frames with no simulation step. Current world state is captured at the serve boundary, so unsent changes collapse without a per-tick queue. Serve steady routine deltas after the keyframe ack, calling `RecordRebuildSent` only after `TrySendTo` returned true.
- [ ] Freeze the authorized current projection and matching movement ack on every keyframe-repair trigger in the repair contract table, including the initial keyframe after acceptance. Build its complete object before fragmenting, reserve internal stream id 0 inside kind 5 only, use low 16 sequence bits for generic fragment consistency, and send at most `MaxChunksPerTick` chunks each cadence. Call `RecordRebuildSent` only after every chunk was successfully handed off. Require the matching full epoch and sequence reliable ack before promotion and resumed newest state. Delayed movement input, notices and reliable game messages continue through a barrier.
- [ ] Before every send, check the complete framing length against the current actual limit and apply the limit-change rule. Never fragment unreliable deltas, truncate membership or silently skip a component.
- [ ] Compare owner id per stream. Changed owner/session needs a fresh epoch. Handoff preserving owner and net id keeps the stream. Visibility remains outside this helper and is evaluated in the existing host before each capture. A hide after freeze appears in the first resumed projection, even if old authorized bytes completed the keyframe.
- [ ] Rebuild and run the same filter to GREEN, then the Task 7a filter once. Commit `feat(networld): bound unreliable repair barriers and failures` and review.

### Task 7c: Restart the legacy writer after signed exhaustion

**Files:** Modify `KhaozEngine.NetWorld/{WorldServer.Replication,ShardedWorldServer.Replication}.cs` after Task 7b, plus the admission-gate wiring at the two host constructors in `WorldServer.cs` and `ShardedWorldServer.cs`. Also modify `WorldServer.Sessions.cs` and `ShardedWorldServer.Sessions.cs`: slot cleanup runs in their `OnLeave`, which already calls `deltaReplicator?.Forget(slot)`, so each gets one call that removes the slot from the pending-restart set and forgets its stream. The reset waits on that set. Create `KhaozEngine.Server.Tests/NetWorld/ReplicationLegacyRestartTests.cs`.

**Interfaces:** Consume Task 1's exhaustion API, Task 3's v2 reset, and Task 6's `ReplicationAdmissionGate` and `ReplicationEpochAllocator`. No new public members.

- [ ] Add `ExhaustionDisconnectsEveryAffectedSessionBeforeReset`, `JoinsAreRejectedWithRestartWhileClosed`, `ResetWaitsForEveryLeft`, `FreshClientsGetCaptureOneAndBaselineMinusOne`, `FreshV2EpochsExceedOldHighWater` and `EpochAllocatorExhaustionKeepsAdmissionClosed` for each host. Use `InMemoryTransportHub`, not `LoopbackTransport`, because `LoopbackTransport` never raises a server-side Disconnected event and the reset must wait for real Left events. Seed the real writer through `DeltaReplicatorForTest.SeedLegacySequenceForTest` and the allocator through `SeedForTest`.
- [ ] Build, then run behavioral RED:

```sh
cd "$DELTA_WORKTREE" && dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~ReplicationLegacyRestartTests'
```

- [ ] Implement exhaustion before `BeginTick`, including checking before Poll admission at the next boundary. Close the admission gate, snapshot all affected joined sessions, disconnect each with `NetServer.Disconnect(slot, "ke:replication-restart")` so clients take the backoff reconnect path, and continue draining Left events through the ordinary leave lifecycle. Do not reset until every affected connection has ended and slot cleanup is complete. Reject joins while closed using the restart token. Clear the writer globally only then and reopen admission. New clients create fresh receivers, capture 1/full legacy baseline -1, and fresh v2 epochs above the old allocator high-water. Do not call `OnLeave` early as a substitute for an ended connection. Epoch allocator exhaustion keeps admission closed for that server lifetime.
- [ ] Rebuild and run the same filter to GREEN, then the Task 7a and 7b filters once. Commit `feat(networld): restart delta writers after sequence exhaustion` and review.

### Task 8: Integrate client reconstruction, acks, prediction and valid-state liveness

**Files:** Modify `KhaozEngine.NetWorld/WorldClient.cs`. Create `WorldClient.Replication.cs` and `RebuildClientStream.cs`. Create `KhaozEngine.Server.Tests/NetWorld/{WorldClientRebuildTests,WorldClientRebuildAckTests,WorldClientRebuildRecoveryTests}.cs`. Do not edit `WorldClient.Frame.cs`, prediction implementation or movement codecs.

**Interfaces:** Consume Tasks 4, 5, 6 and 7a. Add `public ReplicationSelection WorldClient.ReplicationSelection { get; }`. Internal `RebuildClientStream` exposes constructor `(NetClient net,ReplicationRegistry registry,ClientReplicationView view,ReplicationStreamOptions options)`, `DeltaRebuildResult ReceiveDelta(World world,ReadOnlyMemory<byte> frame,NetChannelReliability reliability,out long localNetId,out int movementAck,out string? error)`, `DeltaRebuildResult ReceiveChunk(World world,ReadOnlyMemory<byte> frame,NetChannelReliability reliability,out long localNetId,out int movementAck,out string? error)`, `bool ReceiveOffer(ReadOnlySpan<byte> body,NetChannelReliability reliability,out string? error)`, `void Advance(float dt)`, `void AfterReceiveDrain()`, `void Reset()` and `ReplicationSelection Selection`. Internal diagnostics in the new WorldClient partial expose `PendingPredictionCommands => prediction.PendingCommandCount`, accepted/ingest counts, last accepted movement ack and the largest transport payload sent. Cache counts and bytes are read by tests through internal `ClientDeltaRebuild? DeltaRebuildForTest` and the Replication seams, because NetWorld cannot read Replication internals. These are friend-test diagnostics, not new predicted-state APIs.

The stream also exposes `ReplicationFailure? Failure`, set on malformed/capacity/refusal or deadline failure and cleared on Reset. Partial valid chunks return DuplicateOrStale with no accepted output, no ingest and no ack. Pending assembly is tracked separately from that result. WorldClient's new partial defines internal `readonly record struct WorldClientRebuildDiagnostics(int PendingPredictionCommands,int IngestCount,int AcceptedCount,int LastMovementAck,int MaxTransportPayloadBytes)` and `WorldClientRebuildDiagnostics RebuildDiagnosticsForTest`. No diagnostics write prediction state.

- [ ] Add declarations and compile. On each joined opt-in connection send capability 2 and 3 reliably. Add `RealClientCapabilityMatrix` for the spec's four-way matrix on each host with real `WorldClient`s at the current builtin generation. Expected client selections: server default with client default is legacy reliable `Unnegotiated` with no capability 3 sent, server opt-in with client default is legacy reliable `Unnegotiated`, server default with client opt-in is legacy reliable `DisabledServerPolicy` from a mode 0 offer, and both opt-in is `AcknowledgedUnreliable` `Selected` with a nonzero epoch. Also test older-server silence, mode 0 reason 1 fallback, limits refusal with `ReplicationPolicyRefused`, bad-format incompatibility and builtin generation skew. Never seed format 2 from the old legacy world.
- [ ] Add `AcceptedFrameIngestsExactlyOnceWithItsMovementAck`, `StaleFrameCannotPrunePredictionOrAddSamples`, `MissingBaselineKeepsPublishedStateAndRequestsRepair`, `AckCoalescesAfterDrainAndRepeatsWhenIdle`, `KeyframeAckIsReliable`, `OldChunkCannotRestartAssembly`, `IgnoredDatagramsDoNotRefreshValidStateDeadline` and `RestartRejectReconnectsWithFreshView`. The last proves a `ke:replication-restart` reject takes the backoff path with retry-on-reject off and the next attempt uses a fresh world, view and `ClientDeltaRebuild`. Test unsigned wrap/epoch retirement through finite seeded cases. Record public component reads and existing LocalPredictedState alongside raw projection bytes.

```csharp
Assert.Equal(beforeIngest, client.RebuildDiagnosticsForTest.IngestCount);
Assert.Equal(beforePending, client.RebuildDiagnosticsForTest.PendingPredictionCommands);
Assert.Equal(beforeMovementAck, client.RebuildDiagnosticsForTest.LastMovementAck);
Assert.Single(routineAcksInScheduledTick);
Assert.Equal(NetChannelReliability.ReliableOrdered, keyframeAck.Reliability);
```

- [ ] Add chunk-boundary theories for total length above 64 KiB, below header minimum, wrong epoch/sequence/stream, inconsistent total length, excessive chunk count, mismatched low sequence, a chunk width that does not match the currently accepted offer, and truncated final object. A width that matches a newer accepted replacement offer is valid. Declare outer total before any copy and require assembled length to match it exactly. One assembly with a 64 KiB backing cap, plus one bounded staging projection. Partial state never publishes.
- [ ] Build, then run behavioral RED:

```sh
cd "$DELTA_WORKTREE" && dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~WorldClientRebuildTests|FullyQualifiedName~WorldClientRebuildAckTests|FullyQualifiedName~WorldClientRebuildRecoveryTests'
```

- [ ] In `WorldClientRebuildRecoveryTests`, add `ReplacementOfferAdoptsNewWidthAndKeepsPublishedWorld`, `ReplacementOfferBeyondLimitsRefusesWithPolicy`, `NonIncreasingReplacementEpochIsIncompatible` and `ClientRepairRowsMapToOneAction`. The first runs against a real Task 7b server whose limit drops from 512 to 400 on a live v2 stream. Assert one A1 for the new epoch on the reliable channel, a reassembly width of 377, the previously published entities and component values unchanged through every presentation event until the new keyframe publishes, then exact equality with the new keyframe projection. The last covers the client rows of the repair contract table.
- [ ] Implement the replacement-offer contract for a stream that already selected v2. A mode 1 offer on the reliable channel with an epoch numerically greater than the established one is validated exactly like an initial offer: format 2, limits within the client's configured limits and a feasible chunk width, `ceil(MaxKeyframeBytes / (PacketCap - 23)) <= 255`. On success the client calls `ExpectEpoch` with the new epoch, which retires older-epoch datagrams and chunks, discards any partial assembly, replaces the reassembler with one built for width `PacketCap - 23`, updates the selection epoch, sends A1 for the new epoch reliably and starts the recovery deadline. It keeps the published world, presentation and bounded prediction until the new epoch's keyframe validates and publishes. An offer outside the client's limits or with an infeasible width is refused with the typed `ReplicationPolicyRefused` disconnect and no retry. An exact duplicate of the current offer is ignored. A lower or equal different epoch, or a mode 0 offer on a live v2 stream, takes the existing incompatible-decode disconnect.
- [ ] Pass session reliability into the server-frame dispatcher. Mode offers and chunks require reliable order. Unknown/retired-epoch datagrams cannot establish a stream. The first valid reliable chunk header for a numerically newer epoch calls `ExpectEpoch`, retires old deltas and preserves the last published world until completion. A reliable initial offer authorizes only its exact epoch. The stream binds one `NetClient` and view, so `WorldClient` constructs a new `RebuildClientStream`, with its own `ClientDeltaRebuild`, wherever it constructs a new `NetClient` and view, in its constructor and `StartAttempt`. `Reset()` clears assembly, pins, ack target, deadline and selection for a Joined edge on the same connection.
- [ ] For Accepted only, run `IngestServerState(localNetId,movementAck)` once. It remains the sole prediction reconcile, ingest-count, timestamp sample and teleport-cut path. For DuplicateOrStale, touch none of those. For MissingBaseline, retain presentation and bounded prediction, coalesce repair and repeat the latest valid ack. For malformed input call the existing incompatible decode failure path. Capacity and recovery expiry use the new typed disconnect reasons and `NetClient.Disconnect`, without automatic retry of the terminal failures. A server `ke:replication-restart` disconnect, including a server send fault, takes the backoff path.
- [ ] Schedule v2 control work from elapsed Poll time after receive draining. At most one routine unreliable ack per cadence, advertising the latest retained accepted id, repeated even when no new state arrived. Send accepted keyframe ack reliably after retention/publication. Missing-baseline repair uses current epoch plus last accepted and exact missing sequences, at most one reliable request per 30 ticks. Do not reset the active 90-tick deadline on duplicate misses or ignored traffic.
- [ ] Separate valid-state liveness from arbitrary Data arrival for active v2 streams. Only accepted projections refresh it. Initial negotiation/active repair gets its explicit 90-tick typed deadline before the generic snapshot timeout path can hide that reason. Zero dt drains process receive but consume no time allowance. Malformed/stale traffic and notices cannot indefinitely mask lost authoritative state.
- [ ] Rebuild and run the same filter to GREEN, then once `FullyQualifiedName~WorldClientDeltaTests|FullyQualifiedName~WorldClientReconnectTests|FullyQualifiedName~WorldClientTimeoutTests|FullyQualifiedName~LocalInterpolationBasisTests|FullyQualifiedName~TeleportEpochTests`. Commit `feat(networld): accept rebuilt state before reconciliation` and review.

### Task 9: Prove finite loss, reorder, visibility and phase acceptance

**Files:** Create `KhaozEngine.Server.Tests/NetWorld/{DeltaFaultSchedule,DeltaFaultTransport,DeltaReliabilityRig,DeltaReliabilityTrace,DeltaReliabilityReference,DeltaReliabilityAcceptanceTests,DeltaVisibilityAcceptanceTests,DeltaPredictionPhaseTests,DeltaRemotePresentationTests,DeltaPacketBudgetAcceptanceTests,DeltaReliableMessagesTests}.cs`. No production changes in this task. A required fix goes back to the owning lane with a specific failing finite case.

**Interfaces:** Test-only `DeltaFault` is `(int Ordinal,FaultDirection Direction,byte FrameKind,FaultAction Action,int ReleaseSubtick)` with explicit direction, kind and Drop/Delay/Duplicate action. `DeltaFaultTransport(INetTransport inner,FaultDirection direction,IReadOnlyList<DeltaFault> faults,int maxPayloadBytes)` decorates the existing endpoints, forwards Stats/disconnects/limit query and preserves connection/terminal events. Its `AdvanceTo(int subtick)` releases named packets, and it keeps at most 64 queued frames with ordinary unreliable delay at most eight subticks. Use 120 Hz integer time, so one server tick is four subticks and one presentation frame two. Reliable delayed events hold subsequent reliable Data in order and are eventually released, never truly dropped.

Client-to-server fault kinds use the complete, length-validated control family, with A2 for routine acks. Never classify an 18-byte MOVE as an ack by its prefix. Ordinals are per direction and recognized family, so the finite tables do not depend on Hello, MOVE or notice traffic consuming a state's ordinal.

`DeltaReliabilityRig(bool sharded,int phaseSubticks,MoveTuning tuning,IReadOnlyList<DeltaFault> faults)` composes two real WorldClients and either real server. `Run(int serverTicks = 180)` walks a precomputed finite event list, not until success. Input production owns its own 30 Hz accumulator. Trace rows include timestamp/phases, requested/selected mode, epoch/baseline/accepted ids, movement ack, pending input count, authoritative and rendered position, heading, movement flags, holds and cache/packet maxima. Reference checks inspect expected viewer membership/component payloads and an independent timestamped presentation oracle, not another real client's result. `DeltaReliabilityReference.cs` holds `ExpectedProjection`, `ExpectedVisible`, `LocalOracle` and `RemoteOracle` exactly as defined in the helper contract table. At every accepted id the rig asserts `ProjectionDump.AssertEqual` across the reference projection, the server writer's retained projection and the client's retained projection.

- [ ] Add compiling test infrastructure declarations. Test the decorator itself with exact ordinals, preserved reliable order, duplicated unreliable Data, bounded release and terminal delivery. Assert 65 queued frames and ordinary delay beyond two server ticks are rejected as fixture errors. A consumer-selected unreliable game frame may suppress an ordinary sequenced delta/ack in the finite table, matching the backend's shared sequenced channel behavior.
- [ ] Build, then run declaration/behavioral RED with the acceptance filter below. Missing traces, input or presentation events are failures, not skipped tests.
- [ ] Implement these named finite schedules once for both server heads. Fault actions end by server tick 60 and the fixed end is tick 180. Every presence row has delivered-intermediate and dropped-intermediate cases.

| Test | Named actions and mandatory assertion |
| --- | --- |
| `ReliableDefaultDelayedAckRestoresAllEdges` | Initial projection, hold legacy ack through changes at ticks 10, 11 and 12, then release. Values 1/2/1, flags, tags, remove/re-add and AOI edges converge with legacy reader and last-sent headers. |
| `AckRelativeEmptyDeltaRestoresAllEdges` | Accept initial id, deliver intermediate at tick 10, drop its A2 ack, revert at 11. Empty acknowledged-baseline body still restores exact membership and bytes, including owner-only fields. |
| `Ordinal4After5AndDuplicate5NeverReingests` | Delay state ordinal 4 by two ticks, deliver and duplicate 5, then release 4. Accepted ids remain monotonic, no extra ingest/sample/reconcile or stale movement-ack pruning. Repeat the same schedule with the slot's sequence seeded through `DeltaReplicatorForTest.SeedRebuildSequenceForTest` so ordinals 4 and 5 carry `uint.MaxValue` and 0. Then deliver one hand-built datagram at a half-range distance and one from the retired epoch, and assert both are rejected without ingest. |
| `LostAndReorderedAcksRemainBounded` | Drop first three routine acks, delay later ack 4 behind 5, then suppress acks for exactly 31 new committed sends. Assert ack promotion, count/byte ceilings, one fresh reliable barrier and bounded recovery. |
| `PrunedClientBaselineIsRecoverable` | Retain only four states, force an old server baseline after pruning. MissingBaseline changes nothing live and requests repair. It never becomes IncompatibleVersion. |
| `OwnerVisibilityHandoffAndSlotReuseRemainScoped` | Two clients, owner-only sentinel and Persist/Migrate-only sentinel, hide at tick 20, show at 25, cross shard boundary at 30, disconnect/reuse at 40, release retired epoch at 42. Inspect all outgoing bytes and viewer buffers. Owner/net id remain stable at handoff. New slot sees no retired state. Visibility table is independent of ghost-local components. |
| `VisibilityHideDuringFrozenKeyframeRemovesOnResume` | Tiny feasible cap, freeze at tick 20, hide after first chunk at 21, show an unrelated entity at 22. Frozen authorized bytes may complete, but first resumed projection removes the now hidden entity and uses newest state. No pre-ack delta. |
| `PacketAtCapAndOneByteOverSelectCorrectPath` | Exact transport payload lengths cap and cap+1. First is unreliable state, second starts one reliable keyframe. Test 512 and minimum feasible 281, unknown query and infeasible 280 fallback, shrinking width during repair, 64 KiB/64 KiB+1 and metadata cap/cap+1. Every actual send fits its cap. |
| `ReliableNoticesAndGameEventsSurviveDeltaFaults` | During ticks 10 through 60, send numbered reliable game messages and notices while delta/ack loss and repair occur. Assert once-only order, intact payload and unchanged channel. Kind 5 never enters the game handler. |

- [ ] Add phase theories for offsets 0, 1 and 3 subticks, meaning 0, 1/4 and 3/4 server tick. Join through a finite eight-tick prefix. Spawn the mover at origin and observer at `(3,0,0)`. Walk along `Vector2.UnitY` with yaw 0 from tick 10, turn yaw to PI/2 with FaceCamera at 20, stop at 30, turn in place to PI with FaceCamera at 35, teleport to `(4,0,-4)` at 40, run along UnitY with yaw 0 from 45 and stop at 55. Input submission occurs at the client's own phase, server simulation at 30 Hz and Snapshot sampling after every 60 Hz AdvancePresentation. At offset zero, input is ordered immediately before the coincident server event. Offset-zero is the control, not the only case.
- [ ] Use consumer-approved starting pace from Grimhollow continuous movement O5, walk 2 m/s and run 5 m/s, with default engine control 6/12 in separate finite rows. Both heads receive the same tuning. Require LocalPredictedState movement at the first post-input presentation event before the next server tick for nonzero phases, visible rendered motion, correct heading and movement flags, stop and teleport cut. Inject a stale older movement ack and assert the pending-command count does not change at its delivery.
- [ ] Freeze numeric expectations before GREEN. On flat deterministic motion, authoritative raw payload equality is exact. Let `maxCoordinate = 4f + 6f * tuning.RunSpeed`, bounding the teleport offset plus six seconds at the configured maximum pace. Floating integration position tolerance is `180 * (MathF.BitIncrement(maxCoordinate) - maxCoordinate)`, separately computed for consumer 2/5 and default 6/12 rows. Heading tolerance is at most half `MovementState.FacingYawQuantum` plus one float ulp. Local and remote rendered expectations come only from `LocalOracle` and `RemoteOracle`. The position tolerance never absorbs a missing phase, because the local oracle requires motion at the first post-input presentation event and fails any out-of-tolerance reconcile error. Store each schedule's pinned held-frame indices as the helper contract requires before accepting GREEN. Hold starvation is valid when the schedule leaves no bracket.
- [ ] Add `PredictionWalkTurnStopTeleportAtEachPhase` and `RemoteWalkerAndTurnerRenderAtEachPhase`. The remote case uses spec row 515's named actions on the same walk and turn script: drop the observer's state ordinal sent at tick 22, delay the ordinal sent at tick 23 by eight subticks, and at tick 26 lower the decorator's reported limit from 512 to 400 so the real Task 7b rule renegotiates and runs one keyframe repair. Assert the accepted ids and reconstructed positions at each accept, then the rendered samples, hold flags, heading and movement state at every presentation event against `RemoteOracle` and the pinned hold frames. Do not promise an interpolation bracket while loss starves it. Use the existing approved `LocalPredictedState`, `Snapshot`, `TryGetComponent` and PresentationTrace. Record every presentation event, including frames with no server tick and frames in repair. Do not substitute RawDeltaClient or final position equality for these tests.
- [ ] Build and run the same bounded acceptance filter to GREEN:

```sh
cd "$DELTA_WORKTREE" && dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~DeltaReliabilityAcceptanceTests|FullyQualifiedName~DeltaVisibilityAcceptanceTests|FullyQualifiedName~DeltaPredictionPhaseTests|FullyQualifiedName~DeltaRemotePresentationTests|FullyQualifiedName~DeltaPacketBudgetAcceptanceTests|FullyQualifiedName~DeltaReliableMessagesTests'
```

- [ ] Require exact projection equality at every accepted state, stable survivor ids, monotonic accepted ids, all configured maxima and complete trace rows. Commit `test(networld): prove finite delta reliability and presentation` and review. Do not run it repeatedly as a flake hunt.

### Task 10: Characterize bounded consumer size and publish living contracts

**Files:** Root creates `KhaozEngine.Server.Tests/NetWorld/DeltaConsumerSizeCharacterizationTests.cs` and `docs/DELTA-RELIABILITY-ACCEPTANCE.md`. Root modifies only relevant sections of `KhaozEngine.Replication/README.md`, `KhaozEngine.NetWorld/README.md`, `KhaozEngine.Netcode/README.md`, `KhaozEngine.Netcode.LiteNetLib/README.md`, `docs/USING-KHAOZENGINE.md`, `docs/INDEX.md` and the root `README.md` Replication catalog row, which still says "self-healing acked baseline" and "per-slot acked whole-world baseline/delta", after reconciling other active doc ownership. Several active ground-work branches also edit `docs/INDEX.md` and `docs/USING-KHAOZENGINE.md`. `KhaozEngine.Sharding/README.md` and `KhaozEngine.Benchmarks/README.md` only name the writers and need no change unless the sweep finds a contract claim. Design/index status, release declarations and changelog remain root finishing work, not child edits. No consumer file changes.

**Interfaces:** Consume all earlier public APIs. One `[Fact] ConsumerProjectionSizeCharacterization` serializes a fixed table of viewer projections using NetWorld's actual built-in codecs and source-checked opaque consumer extension payloads. The source manifest references Grimhollow `Grimhollow.Shared/GrimhollowProtocol.Components.cs`, whose current lengths are MonsterKind 1, ActorAction 30, ActorWorn 12, ActorFanfare 2, ActorVitalEffect 4, DurableLootSource 16 and CarcassState 50 bytes. Keep its type ids from `TileProtocol.FirstGameTypeId`, not invented built-in ids. The consumer currently builds a tile registry, so this characterizes the proposed NetWorld adaptation plus its current extension bytes. It is not a production P8 capture or an approval of an entity count.

- [ ] Add compiling characterization fixture declarations, then behavioral RED that requires four named rows and output fields. The fixed rows use 1, 32, 128 and 256 visible entities, one local owner, 32-byte UTF-8 player names at most, and the source manifest's full actor/carcass extension mixes. Record component payload totals, entity/frame counts, full keyframe object bytes, changing-state datagram bytes, retained reachable bytes after the bounded 31-send window, oversize decisions and reliable chunk/tick count. Do not add a benchmark, randomized AOI sweep or throughput claim.
- [ ] Before copying any fixture bytes, recheck source field widths and UTF-8 framing. An independent length calculation must match actual encoded lengths. Each row's `IndependentCapacityDecision` is computed from those independent lengths and the approved caps as one of `FitsDelta`, `OversizeDeltaKeyframe` or `CapacityExceeded`, and must equal the decision the real writer and stream make, which stay the enforcement oracle. Include a metadata-only tag case at exactly 16,384 frames, plus one over. A characterization exceeding an approved cap is a recorded capacity result and consumer adoption constraint, never permission to raise a limit.

```csharp
Assert.Equal(4, characterizedRows.Count);
Assert.All(characterizedRows, row => Assert.Equal(row.IndependentEncodedBytes, row.ActualEncodedBytes));
Assert.All(characterizedRows, row => Assert.Equal(row.IndependentCapacityDecision, row.ActualCapacityDecision));
Assert.Equal(new[] { 1, 32, 128, 256 }, characterizedRows.Select(r => r.VisibleEntities));
```

- [ ] Build and run this command once to GREEN after implementation. Save its bounded result table with command, commit and configuration in the acceptance document:

```sh
cd "$DELTA_WORKTREE" && dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~DeltaConsumerSizeCharacterizationTests' --logger 'console;verbosity=detailed'
```

- [ ] Publish last-sent reliable obligations, fresh receiver on discarded/failed send, per-slot Forget and global exhaustion lifecycle. Remove claims that older-baseline legacy overlays are loss-safe reconstruction. Preserve exact legacy wire shape and distinguish observable header/bandwidth differences from a body-format migration.
- [ ] Publish opt-ins, mode selection/reasons, capability gating at generation 13, validated options/defaults, complete payload cap accounting, backend query/fallback, immutable projection acknowledgement, uint/epoch rules, finite retention and baseline miss, one reliable frozen barrier, chunk width/rate, exact ack promotion, valid-state liveness, elapsed Poll/cadence requirement and typed capacity/recovery/policy disconnects. State that visibility revocation appears on resumed state and cannot recall bytes already sent while authorized.
- [ ] Update generic Netcode docs for the additive query/send/assembly APIs, including unchanged external-transport defaults and default four-assembly constructor. Update backend docs without adding LiteNetLib concepts to Netcode. Show two distinct standalone usage examples and reset/connection ownership obligations. Do not move design history into API reference or approve P8 gameplay appearance.
- [ ] Sweep all Markdown for `ServerReplicator`, `AoiDeltaReplicator`, `ApplyDelta`, `last acknowledged`, `self-heal`, new type/flag names and fragment cap claims. Root reconciles overlapping living-doc changes from other programs, then runs repository prose/doc guards. Existing unrelated findings stay outside this scope.
- [ ] Commit `docs(netcode): document delta contracts and bounded acceptance` and request whole-program review. Root runs final Release solution build, headless non-LiveSocket suite and repository-required guards once after current-main reconciliation. Root also runs the `LiveSocket` tests that exercise the changed NetWorld paths exactly once, never in a loop, and reports each result: `--filter 'Category=LiveSocket&(FullyQualifiedName~WorldRoundTripTests|FullyQualifiedName~WorldClientLiveReconnectTests|FullyQualifiedName~MmoServerEndToEndTests|FullyQualifiedName~LiteNetLibPayloadLimitTests)'`. Root owns version/changelog selection and any authorized package proof. No release tags, consumer adoption or issue closure are part of this task.

## Specification traceability

| Approved requirement | Implementation and proof |
| --- | --- |
| Option A and standalone legacy compatibility, #1229 | Task 1, last-sent projection and unchanged reader tests, with the NetWorld and Sharding regression run. Task 10 living obligations. |
| Global signed counter exhaustion | Task 1 bounded counter tests, Task 3 clears v2 state, Task 7c all-session lifecycle and monotonic epoch allocator. |
| Capability, mode offer/accept and legacy fallbacks | Task 6 field goldens/options, Task 7a raw four-way matrix, Task 8 real-client four-way matrix, generation skew and silence. |
| Length-18 MOVE aliases, recognized non-18 rejection | Task 6 tri-state parser, Task 7a three fresh initial MOVE sessions per host and malformed boundary theories. |
| Immutable authoritative map, full entity replacement and atomic publication | Tasks 2 and 4, exact reversion/presence/unsampled/tag/opaque equality and staging failure tests. |
| Presentation separation and once-only prediction ingest | Task 4 publication buffers, Task 8 accepted envelope ack, Task 9 independent phase/presentation schedules. |
| Uint wrap, greater replacement epochs, exact sent-id acks | Tasks 2, 3, 4, 7b and 8, seeded finite wrap/half-range/retired cases, and the Task 9 wrap repetition. |
| Server/client pins inside count and backing-byte limits | Tasks 2, 3 and 4, diagnostics including compact viewer storage and rejected/candidate paths. |
| Packet, keyframe, metadata and chunk budgets | Tasks 3, 5, 6, 7a offer caps, 7b chunk cadence and 8, boundary acceptance Task 9 and one characterization Task 10. |
| Shared capture, one unsent state, fixed elapsed cadence | Tasks 3 and 6, Task 7a cadence object, Task 7b serve allowance, chunk cadence and short-frame tests, and phase traces in Task 9. |
| Repeated coalesced unreliable routine acks and reliable barrier ack | Tasks 7a and 8, ack loss/reorder and shared sequenced-channel suppression in Task 9. |
| Negotiation/miss/no-ack/retention/oversize/changed-limit repair | Task 3 repair contract table, Tasks 7b and 8, finite recoveries and typed deadlines in Task 9. |
| One frozen reliable keyframe, no pre-ack resume, no periodic keyframe | Task 7b state machine, Task 8 bounded private assembly, Task 9 visibility and capacity proof. |
| Owner-scoped bytes, source-local visibility, handoff and session replacement | Tasks 1, 2, 3 and 7b, two-viewer leakage/visibility/handoff/slot reuse Task 9. |
| Reliable game messages/notices and package seams | Task 5 dependency-preserving seam, Tasks 7a and 8 routing, Task 9 message preservation and Task 10 reference sweep. |
| Consumer size evidence, engine release and P8 boundary | Task 10 records a bounded source-based characterization. Actual P8 catalog/parity, production AOI capacity and final appearance/playtest remain the consumer owner gate. No release version or tag is reserved. |
| Typed policy, recovery and capacity failures, restart token and no automatic terminal retry | Task 6 reasons, tokens and `ConnectRefusal` mapping, Task 7b server disconnects and send failure rule, Task 7c restart, Task 8 client failures and restart reconnect, Task 9 finite deadlines. |
| Valid-state liveness, elapsed `Poll(dt)` scheduling and zero-time drains | Task 6 cadence, Task 8 liveness and ack scheduling, Task 9 phase traces. |
| Malformed bytes, unknown built-ins and opaque extensions | Task 4 malformed theories and opaque retention, Task 6 control decoding, Task 8 chunk boundaries. |
| Send commitment only after transport handoff | Task 5 `TrySend`/`TrySendTo` contract, Task 3 `RecordRebuildSent`, Task 7b send failure rule. |
| Live limit changes, no legacy downgrade on a live receiver | Task 7b limit-change rule and tests, Task 9 at-cap and remote repair schedules. |
| First keyframe over a legacy-built world | Task 2 publication reconciliation and `FirstKeyframeOverLegacyWorldRemovesLegacyOnlyState`. |
| Backend packet-size query with zero meaning unknown | Task 5 seam, facades and `LiteNetLib` query, Task 7a mode 0 reason 1 fallback, Task 7b live limit-change rule, Task 9 unknown and infeasible caps. |

## Plan self-review and handoff

The self-review checked each item below against the approved spec and the source at `origin/main` `8768294f7`. It ran no build, test, pack or client.

| Check | Result |
| --- | --- |
| Source drift since approval | `origin/main` was merged. No Replication, Netcode, NetWorld, LiteNetLib, their tests, `Directory.Build.props` or `Directory.Packages.props` changed. Every source row was re-read and still holds. The start-commit constraint now names this check instead of a fixed commit. |
| Interpolation backing-byte accounting | Legacy `RecordInterpolationSample` stores the `currentBytes` array reference uncopied. Corrected: publication copies sampled payloads into fresh arrays, presentation buffers never alias retained backing, retained bytes are the distinct reachable backing array lengths, and `PublicationSamplesNeverAliasRetainedBacking` proves it. Presentation history stays under the legacy cap, per the spec's separation of samples from baselines. |
| Test helpers and presentation oracles | Snippets used undefined helpers (`ReadLegacyHeader`, `CaptureNext(writer)`, `ReadV2ChangedCount`, projection byte dumps, a counted reader) and an unspecified local oracle with correction decay. Corrected by the helper contract table, its files in each Files block, and exact `ExpectedProjection`, `LocalOracle`, `RemoteOracle` and pinned hold-frame rules. |
| Success-reporting facade sends | Spec requires commit only after a successful send or full keyframe handoff. Corrected: `TrySend`/`TrySendTo` define success as handed to the transport, do not catch, and the stream faults and disconnects on false or a thrown send without committing. Backend silent drops are named, so success is never read as delivery. |
| Numeric envelope charging | The 12-byte charge matches the spec's complete keyframe object. NetWorld cannot call Replication internals, so `Validate` and `WithEnvelopeBytes` are public. NetWorld rejects an envelope other than 0 or 12 instead of silently overriding it. Cap arithmetic re-checked: 512 gives width 489, 135 chunks and 34 ticks. 281 is the smallest feasible cap with 255 chunks, and 280 needs 256. |
| Wire generation 13, length-18 MOVE, bounded policies | Source still has generation 13, `MoveSize` 18, control 2 bytes, ack 6 bytes, game messages excluding 18, kinds 0 to 3 and free A1 to A3 and kinds 4 to 6. Routing length 18 first is equivalent for existing frames. All approved limits are unchanged. |
| Package boundaries | Replication references Ecs only, Netcode references Abstractions only, LiteNetLib stays optional. Two cross-assembly internal reads were found and corrected: `DeltaRebuildOptions` helpers and WorldClient cache diagnostics. No `InternalsVisibleTo` or csproj edge is added. |
| Restart and terminal tokens | `ConnectRefusal.Read` would have mapped `ke:replication-restart` to `RejectedToken` without backoff, stranding clients with retry-on-reject off. Corrected with `DisconnectReason.ReplicationRestart` and `RetryRule.Backoff`, plus explicit terminal mappings. The claim of no unbounded retry was corrected to the consumer's `ReconnectBackoff` default. |
| Legacy blast radius | Task 1's regression filter missed tests that drive the writers. Corrected in review fix round 1 to the whole Replication, NetWorld and Sharding namespaces. In-repo standalone callers were classified, and none needs a source change. Task 10 now names the root `README.md` Replication row. |
| Signatures across tasks | Fixed a misnamed server capability handler, a `Func<ulong>` epoch source that bypassed allocator exhaustion, a `PublishProjection(...)` placeholder, and a client stream bound to a view replaced on reconnect. No TBD or placeholder remains. |
| KESIZE | No touched file has a baseline entry. `WorldClient.cs` has 38 lines of headroom. A global constraint now confines edits there and requires the file-size guard per task. |
| Task completeness | Every task names files, signatures, RED tests, a focused command and GREEN outcome. The LiteNetLib query is a `LiveSocket` fact that root runs once or reports as unverified. |
| Traceability | Five rows were added for typed failures, liveness, malformed input, send commitment and backend query. Every approved requirement maps to at least one task. |
| Release target | Superseded in review fix round 1: `v20.18.0` is released, so this work rides the next staged version. |

Name review and rulings, settled by the orchestrator in review fix round 1:

1. All public names listed by the self-review are approved: `DeltaRebuildOptions.EnvelopeBytes`, `Validate` and `WithEnvelopeBytes`, `DeltaRebuildFailure`, `DeltaRebuildException`, the four `DisconnectReason` members, `NetClient.TrySend` and `Disconnect`, `NetServer.TrySendTo`, the payload-limit facades and the bounded reassembler constructor, with `MaxPartialAssemblies` redocumented as the default.
2. Added to the review list and kept or adjusted: `RebuildNeedsRepair(int slot)`, adjusted so the no-ack window moves into `DeltaRebuildOptions.NoAckSendWindow` passed at `StartRebuild` rather than per call. `ClientDeltaRebuild.ExpectEpoch`, `LatestAcceptedId` and `LastFailure` kept. `LatestRetainedAckTarget` renamed `AckTarget`. `TryGetReplicationSelection`, `ReplicationSelection`, `ReplicationSelectionReason`, `ReplicationDeliveryMode` and `ReplicationStreamOptions` kept, with `NoAckSendWindow` removed from `ReplicationStreamOptions`.
3. Presentation sample history stays outside the 2 MiB retained budget under the legacy cap.
4. The exhaustion restart uses `DisconnectReason.ReplicationRestart` with backoff retry.
5. Ruling O2.14: a v2 send fault, false or thrown, disconnects with the `ke:replication-restart` backoff token, not the terminal recovery-failed token. A thrown send is still not caught by the host.
6. Ruling O2.15: a live-stream limit drop below 281 or to 0 disconnects with `ke:replication-restart`, so the reconnect receives mode 0 reason 1. `ke:replication-capacity-exceeded` stays only for impossible projection size, the approved terminal case.
7. Ruling on N1: 7a's steady-serve tests move to 7b, because they need the keyframe barrier.

Review fix round 1 applied the independent review's required changes:

| Review item | Correction |
| --- | --- |
| Merge and release | Merged `origin/main` `d0aaabcdc`, resolving one `docs/INDEX.md` row conflict by keeping both rows. `v20.18.0` is released, so the work rides the next staged version. |
| Task 1 RED | `ReversionWithoutAckRestoresOriginalValue` acknowledges S, delivers S+1 unacknowledged, serves S+2, and names the stale 2 and S baseline as RED. |
| Task 1 gate | Runs the full Replication, NetWorld and Sharding namespaces excluding `LiveSocket`, because delta replication defaults on. The coverage claim was removed. All three Int32 header helpers are named. |
| Goldens | `ReferenceAoiDelta` changes to the last-sent contract first, and goldens come from it or by hand, never from the new writer. |
| First keyframe over legacy world | Publication reconciles against `view.Entities` and every registered codec. New test added in Task 2. |
| Limit changes | Only a drop below the selected cap renegotiates. Growth keeps the epoch. A drop below 281 or to 0 was a typed capacity disconnect, superseded in round 2 by ruling O2.15. |
| Repair contract | Task 3 now has a trigger, signal and action table. NetWorld reads only `RebuildNeedsRepair` and the exception's `Failure`. |
| `CountingCodec` oracle | Exact count: one read per known component in the reconstructed projection per accepted packet, zero in publication, with staging rebuilt per packet. |
| Task 7 split | Tasks 7a, 7b and 7c each have files, tests, RED and GREEN filters. Dependencies updated, including `2 + 3 -> 4`. |
| Minor items | Exhaustion test populates slots before seeding. `AoiPresenceRecord.cs` and its seams are deleted. `ServerReplicator` history shrinks to the current capture. 7c requires `InMemoryTransportHub`. Send faults use the restart token. Remote presentation names drop, delay and repair actions. Task 9 repeats reorder across `uint.MaxValue` to 0. The four-way capability matrix is stated in 7a and 8. Vacuous assertions became exact values. `MessageReassembler` XML docs are updated in Task 5. Signature comma fixed. Projection types are internal. Publication may reuse byte-equal presentation arrays while still installing every component. |

Review fix round 2 resolved the re-review findings:

| Finding | Correction |
| --- | --- |
| N1, 7a tests needing the barrier | `SteadyServeSendsOneStatePerCadenceTick`, `ShortShardedFramesSpendNoExtraAllowance` and `MixedLegacyAndV2ViewersShareOneCapture` moved to 7b's new `ReplicationSteadyServeTests` and its filters. 7a stops at routing, aliases, negotiation, offer and acceptance, with a narrowed GREEN claim and commit subject. Cadence serving moved to 7b. |
| N2, mid-stream replacement offer | Task 8 defines validation, adoption of the new epoch and width, A1, keeping the published world, and refusal as `ReplicationPolicyRefused`. New tests include `ReplacementOfferAdoptsNewWidthAndKeepsPublishedWorld`. The chunk theory rejects only a width that differs from the currently accepted offer. Two client rows were added to the repair table. |
| O2.15 | The repair table row, the 7b limit rule and the drop-to-280 test now use `ke:replication-restart` and assert the reconnect gets mode 0 reason 1. |
| Minors | Traceability names 7a and 7b for cadence and chunks. Dependency is `4 + 5 + 6 + 7b -> 8`. The fourth Int32 header reader in `ReplicationChannelsTests.cs` is fixed in Task 1. `RepairContractTriggersMapToOneAction` covers writer and stream rows, and Task 8 adds `ClientRepairRowsMapToOneAction`. 7c modifies both Sessions partials, because slot cleanup runs in `OnLeave`. `AdminHttpServerTests` joins the Task 1 gate. Root runs the NetWorld `LiveSocket` tests once in final verification. |

- Ownership is exclusive by lane, with writer/view edits sequential inside Replication and protocol/host/client edits sequential inside NetWorld. Root owns later docs and acceptance, including overlap reconciliation with the ground-work branches that edit `docs/INDEX.md` and `docs/USING-KHAOZENGINE.md`.
- Consumer pace 2/5 is grounded in approved continuous movement O5, while its current catalog/registry remains tile based. The characterization is explicitly an adaptation estimate. It does not claim measured production freshness, safe final AOI population or an adopted P8 client.
- Final verification, implementation commits, integration, release declaration work and adoption remain root actions. This plan alone claims no implementation, build/test pass, package publication or resolved issue.

Status at handoff (superseded by the Outcome): **Re-review findings resolved, ready for confirmation**. Root confirmed the corrections before execution.

## Outcome

Executed on 2026-10-03 under subagent-driven development with three lanes and a root lane. Integration branch
`feature/grimhollow-delta-reliability`, base `dcdbd3643` (engine main `d0aaabcdc` plus the plan). Lane branches
`feature/grimhollow-delta-repl` (Tasks 1 to 4), `feature/grimhollow-delta-netcode` (Task 5),
`feature/grimhollow-delta-networld` (Tasks 6, 7a, 7b, 7c, 9 and 10) and `feature/grimhollow-delta-client` (Task 8).
Every task passed an independent spec and quality review, with fix rounds where recorded below. Task 10 merged
current `origin/main` (`952407a53`, bringing the 20.20.0 staging of the nav profile bake and catalog text authoring)
without conflicts before final verification. Counts are test results through the shared build slot. RED is the
behavioral failure before implementation, GREEN the same filter after it.

### Tasks

| Task | Commits | RED | GREEN and regression | Review |
| --- | --- | --- | --- | --- |
| 1 Legacy last-sent contract and signed exhaustion | `dcdbd3643..c43c9f675` | 25 failed, 1 passed (headline expected 1, actual 2 on both writers) | 26 of 26. Replication, NetWorld and Sharding gate 1,259 passed. Goldens hand-built and checked by an independent decoder. | Clean. |
| 2 Immutable packet, projection, limits and publication | `c43c9f675..a7d0ac646` | 14 of 14 `NotImplementedException`. Fix 1: 3 of 18. Fix 2: 1 of 18. | 14 of 14, Replication 195 of 195. Fix 1: 18 of 18, Replication 199 of 199. Fix 2: 18 of 18. | Two fix rounds (atomic supersede, then a self-pinned candidate chain), then approved. |
| 3 Acknowledged v2 sends in both writers | `4fef07a85..90ac8f2e2` | 37 `NotImplementedException`. D2.9 follow-ups 5 of 62 each. | 43 of 43, capture 5 of 5, Replication 236 of 236. D2.9 follow-ups `250ace5b9` and `90ac8f2e2`: 62 of 62, Replication 237 of 237. | Clean, D2.9 follow-ups root-verified. |
| 4 Client reconstruction, retention and publication | `963122b81..82032d311` | 88 `NotImplementedException`. Follow-up 1 of 92. | 88 of 88, Replication 325 of 325. Follow-up 92 of 92, Replication 329 of 329. | Clean, then the D2.14 follow-up approved. |
| 5 Packet query, send commitments, bounded assembly | `dcdbd3643..f3ae54bed` | 13 of 13 `NotImplementedException` | 13 of 13, regression 25 plus 7, `LiveSocket` `LiteNetLibPayloadLimitTests` 1 of 1 (limit 1020). Fix: 15 of 15, regression 32. | One fix round (stale `Slot` after `Disconnect`, D2.4), then approved. |
| 6 Format 2 controls, options and typed state | `4fef07a85..ba352f79e` | 103 failed, 4 passed. D2.8: 1 failed. | 121 of 121, regression 205 of 205. D2.8 `ba352f79e`: 122 of 122. | Clean. `CatalogRefusalMappingTests` growing from 12 to 16 accepted outside the Files block. |
| 7a Routing, negotiation, offer and acceptance | `963122b81..fe9cefd8d` | 113 `NotImplementedException` | 118 of 118, plan regression 30 of 30, client routing 244 of 244, build 0 warnings. Fix `fe9cefd8d`: 121 of 121, 30 of 30, 244 of 244. | One fix round (D2.10, D2.11, D2.12), then approved. |
| 7b Barrier, steady serve, chunks, limits, deadlines, failures | `ad614780f..ecbeb98e0` | 65 of 65. D2.15: 3 of 69. | 65 of 65, 7a 121 of 121, regression 30 of 30. D2.15 `ecbeb98e0`: 69 of 69, options 35 of 35. | Clean, D2.15 root-verified. |
| 7c Legacy writer restart after exhaustion | `7f7aac283..44c682c7f` | 12 `NotImplementedException`. Fix 2 of 16. | 12 of 12, 7a plus 7b 190 of 190, host and auth 79 of 79. Fix `44c682c7f`: 16 of 16, 190 of 190. | One fix round (legacy-only restart theory, D2.17), then approved. |
| 8 Client reconstruction, acks, prediction and liveness | `7f7aac283..29057cc53` (squashed to `c0aa86093` and `29057cc53`) | 55 of 61 `NotImplementedException`, and 55 of 61 assertions against the base client. Fix 5 of 67. | 61 of 61, regression 20 of 20, build 0 warnings. Fix: 67 of 67, regression 20 of 20, reject and decode 63 of 63. Integration with 7c at `16d2ad623`: 332 passed, 0 failed, 3 skipped. | One fix round (terminal decode disconnect, D2.18 to D2.20), then approved. |
| 9 Finite loss, reorder, visibility and phase acceptance | `16d2ad623..a968a5008` | Test-only and first run green, so three deliberate breaks proved the oracles: 38, 58 and 52 failures, restored byte-identical. | 64 of 64, build 0 warnings. Fix `a968a5008`: 64 of 64, deliberately broken client 4 of 4 and server 2 of 2 failed on the new checks. | One fix round (D2.21), then approved. |
| 10 Characterization and living contracts | `4f4a87853` (test) and the docs commit that adds this Outcome | `NotImplementedException`, 1 of 1 failed | 1 of 1 (table in `docs/DELTA-RELIABILITY-ACCEPTANCE.md`). Final verification below. | Whole-program review pending. |

### Rulings

| Ruling | Decision | Reason | Cost if wrong |
| --- | --- | --- | --- |
| D2.1 | The plan's root-granted CPU slot is replaced by the shared slot runner, one build or test at a time across the orchestration. | Same serialization without routing every command through root. | A brief overlap with another chat's run after a five-minute wait. |
| D2.2 | Lanes run in isolated worktrees, and root merges each lane after its review. Lane workers push their own branches. | Concurrent lanes cannot share one working tree while building. | Merge work at integration. |
| D2.3 | The plan and design land on main with the implementation, not before. | Engine main carried another chat's post-tag commit that needed a version bump that was not ours. | The plan is visible only on the branch until landing. |
| D2.4 | Both facades clamp a negative transport limit to 0 (unknown). | Tasks 8 and 9 consume the limit, and one clamp at the facade is the single contract point. | None. |
| D2.5 | Publication adds or removes only components whose codec is on the `Replicate` channel. | Removing Persist-only or Migrate-only state would delete client state the stream does not own. | A one-line change in `ProjectionPublication.Publish`. |
| D2.6 | The atomic publication guards fold into Task 2's first fix round. | They protect the atomic-publish and D2.5 contracts Tasks 3 and 4 build on. | Small. |
| D2.7 | Task 3 may add one non-committing fit query to `ProjectionRetention.cs` and nothing else there. | The Files block omitted the file, and exception-only fallback might not meet the before-capacity intent. | One internal member. It went unused. |
| D2.8 | An opt-in without the existing delta switch is a typed configuration error, not inert. | A misconfiguration should fail loudly rather than silently run legacy. | One line and a test. |
| D2.9 | `DeltaRebuildOptions.Validate` requires `MaxRetainedPayloadBytes >= 4 * MaxKeyframeBytes`, amended from 3x. | A client retains a new projection beside up to three pins, so 3x left a repair loop possible. | Slightly tighter options, defaults (32x) unaffected. |
| D2.10 | One helper derives the 12-byte-envelope limits, and Task 6's validation calls it. | Two copies of one derivation drift. | One line outside 7a's Files block. |
| D2.11 | One 90-tick negotiation deadline runs from the offer until the initial keyframe is acknowledged, with a separate repair deadline later. | Negotiation ends when the stream can serve, so an accepted stream that never completes its keyframe must still fail typed. | 7b's 90-tick test aligns with 7a's. |
| D2.12 | One internal host-agnostic holder (`RebuildServerStreams`) replaces two byte-identical host partials. | Confirmed duplication that 7b and 7c would otherwise grow twice. | A refactor inside 7a's fix round, proven by the 7a and regression filters. |
| D2.13 | A half-range `SequenceAmbiguous` datagram is ignored like stale, without disconnect. | An unreliable datagram is not authoritative, and the no-ack window and repair cover genuine divergence. | A hostile repeated half-range sender is ignored, not disconnected. |
| D2.14 | The shared keyframe-size helper, a pre-retain staging completeness check, three edge tests and two nits fold into a Task 4 follow-up. | The duplicated formula was a client and server drift risk. | One more commit and review pass. |
| D2.15 | A 7b follow-up (`ecbeb98e0`) caps `MaxTransportPayloadBytes` at 65,558, extracts the shared `V2ReplicationRig.cs`, proves an owner change starts a fresh epoch and applies the limit rule before repair. | The keyframe chunk width must fit a `ushort`, and a repair and a limit drop on one tick must spend one epoch. The ledger records the follow-up, not a separate reason. | One follow-up commit and a root inspection. |
| D2.16 | 7c and 8 run in parallel worktrees. | Disjoint files, both depending only on 7b. | Merge work and a possible small rig conflict. |
| D2.17 | `EndServe` skips slots pending a restart, with remark and doc fixes, in 7c's fix round. | Prevents a second reject and an early leave in a simultaneous-exhaustion corner. | Small. |
| D2.18 | A kind 4 datagram with a valid format, no unknown flag bits and the keyframe bit set is ignored like stale. | Only reliable chunks establish a keyframe, and one odd datagram must not end a session. | A hostile sender of keyframe-flagged datagrams is ignored, not disconnected. |
| D2.19 | `RebuildClientStream` takes a fifth constructor argument, `tickSeconds`. | The stream owns its cadence from configured `TickSeconds`, which `ReplicationStreamOptions` does not carry. | None. |
| D2.20 | Format and flag validation run before the keyframe-bit ignore, and one client send helper records every payload. Root squashes the work-in-progress checkpoint. | Malformed bytes must stay terminal, and the payload diagnostic must measure every send. | Squash and rerun of the Task 8 filter at integration. |
| D2.21 | Spec row 511 and line 296 bind over the plan's two-tick delay: each stale frame or ack is released alone in its poll, with rollback, reconcile, interpolation and baseline regression asserted there. | Same-poll arrival lets the newer frame hide the regression the schedule exists to catch. | Two schedules diverge from the plan's literal timing. |
| D2.22 | The retained-budget guidance is `(effective NoAckSendWindow + 1)` projections of the largest expected size, not `+ 2`. | A writer slot holds at most the acknowledged baseline plus the window of committed sends, because a superseded candidate is removed in the same atomic step that retains its replacement. The characterization measures 32 at the window, and the defaults hold 32 projections of up to 64 KiB. | A consumer sizing to `+ 1` with an unforeseen extra pin would see an occasional early repair, never a correctness fault. |
| D2.23 | Grimhollow stays on legacy reliable deltas for dense views until [#1261](https://github.com/APKiwiOrg/KhaozEngine/issues/1261) is resolved. | The characterization shows six or more moving entities overflow a 512-byte routine datagram, so a dense view runs a keyframe barrier on most ticks and remote state stops updating during each one. | P8 waits on #1261 or adopts format 2 only for views that fit. |

### Final verification

Run once by root on `feature/grimhollow-delta-networld` after the `origin/main` merge, code at `4f4a87853`, each
command through the slot runner:

| Command | Exit | Result |
| --- | ---: | --- |
| `dotnet build KhaozEngine.slnx -c Release` | 0 | 0 warnings, 0 errors |
| `dotnet test` on each of the 29 test projects, `-c Release --no-build --filter "Category!=LiveSocket"` | 0 each | 24,921 passed, 0 failed, 1,325 skipped (GPU, SQL Server and environment-gated facts, none in the delta suites) |
| `Category=LiveSocket` with `WorldRoundTripTests`, `WorldClientLiveReconnectTests`, `MmoServerEndToEndTests` and `LiteNetLibPayloadLimitTests` | 0 | 6 of 6 passed |
| `check-dashes.sh --tree`, `check-prose.sh --tree`, `check-file-size.sh --tree`, `check-agent-instructions.sh --tree` (7,653 of 16,384 bytes), `bash check-doc-versions.sh` | 0 each | Run on the finished docs. The prose baseline was ratcheted down for the four files whose semicolons the rewrite removed. |

### Carries and deferred minors

| Item | Disposition |
| --- | --- |
| D2.9 note: the 4x floor protects pins only, so unacknowledged committed sends can be pruned at the floor. | Documented in the Replication README. The guidance names `(effective window + 1)` projections, the most a writer slot holds at once (the characterization measures 32), rather than the `+ 2` the carry first stated, because a superseded candidate is removed in the same atomic step that retains its replacement. The defaults satisfy it. |
| Control asymmetry: a valid control on the wrong channel is flagged `MalformedPacket`, a valid control from a slot that never asked is dropped silently. | Documented in the NetWorld README. |
| `ReplicationProjection` rebuilds every `ProjectedEntity` with a `HashSet` per accepted packet. | Deferred performance follow-up, filed as [#1263](https://github.com/APKiwiOrg/KhaozEngine/issues/1263). |
| A `Left` after a thrown send forgets the stream without a restart token. | Documented in the NetWorld README. |
| A host kick counts as a pending slot's leave, and a restart never completes on a transport without a server-side `Disconnected` event. | Documented in the NetWorld README. |
| Task 5: `MessageReassemblerLimitTests.cs` uses raw allocation counters instead of `AllocAssert.NoPerCallAllocation`, and a shift-based `ushort` write. | Deferred test hygiene. |
| Task 1: `LegacyDeltaSlot.AcknowledgedSequence` is never read and its stale, duplicate and unsent ack rule is untested. Golden DUMP comments still say regenerate from writer output. No test proves a refused capture skips the world scan. `Skipped_ack_still_diffs_from_the_last_sent_projection` lost its sequence 3 position change. | Deferred test and comment hygiene. |
| Task 6: `ReplicationStreamOptionsTests.cs` line 170 is arithmetic-only, and no drift test pins `RebuildProtocol`'s copied 0xC5 marker to `MoveProtocol`'s. | Deferred test hygiene. |
| Task 3: the `RetentionPressure` growth path is untested and unreachable for the writer. The `RebuildDeltaWriter` registry parameter is unused, `RequireBuildable` is redundant, and `RetainedCount` and `BuildRebuildFor` null-argument docs are thin. | The pressure path is labeled defensive in code since `90ac8f2e2`. The rest is deferred hygiene. |
| Task 8: legacy decode-failure paths in `WorldClient.cs` still skip `net.Disconnect()`, so the server holds that session until its own timeout. | Deferred, filed as [#1262](https://github.com/APKiwiOrg/KhaozEngine/issues/1262). The format 2 path disconnects locally. |
| Task 9: `DeltaFaultTransport.InjectAt` skips the tick-60 window and delay bound, the prediction observer's ingest rows come partly from the client, and `AssertMaxima` was loosened to the stream-wide maximum. | Deferred rig hygiene. Callers comply today, and a per-send loop covers the maxima. |
| Whole-branch review fix wave: a throwing standalone receiver example, the mover threshold, and stale acked-baseline XML docs and comments. | Fixed in docs and comments only, no logic change. See the Task 10 report. |
| Dense views force a keyframe barrier on most ticks. | Filed as [#1261](https://github.com/APKiwiOrg/KhaozEngine/issues/1261), ruling D2.23. |
| Release: delta rides the staged 20.20.0 beside the nav profile bake and catalog text authoring, with the NPC movement round landing after it. | Delta bullets were added under the existing 20.20.0 entry. No new version and no tag. The owner tags after both rounds (O2.29). |
