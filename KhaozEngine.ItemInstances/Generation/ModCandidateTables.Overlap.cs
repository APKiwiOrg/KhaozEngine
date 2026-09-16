using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The OVERLAP pass of spec 9.2 item 3, run once per (tag signature, kind, band) at boot. It is the k-way
/// merge a roll used to run, kept here and used to record only what the merge DISCARDS: the entries a later
/// tag of the same signature repeats, which the first-tag-wins rule of spec 8.3 drops.
/// <para>
/// <b>The union is never written down, at a roll or at boot.</b> A roll subtracts the suppressed count and
/// weight from the bucket's own, which gives the union's count and total without the union, and skips the
/// listed entries inside the draw. Spec 9.2's measured table is why: the union materialised is 148 MB, a
/// memoized merge is 11 us per roll at 86 percent of rolls, and this is 5.2 MB and zero.
/// </para>
/// <para>
/// <b>The merged count and weight this pass computes on the way are checked against the same numbers the
/// suppression scalars imply</b>, because the lists ARE the merge and a wrong list is a plausible wrong
/// weight rather than a crash. A disagreement is counted into <see cref="ConsistencyFailures"/>, and the
/// measured count over a whole key space is zero.
/// </para>
/// <para>
/// <b>That self check is a SMOKE ALARM rather than proof, and it is important not to read it as proof.</b>
/// Both sides of the comparison come out of this one pass: the merge counts what it kept, and the scalars
/// are the bucket totals less what the same walk recorded as suppressed. A list that is wrong in a way the
/// walk is CONSISTENTLY wrong about, a cursor advanced twice or a position skipped, moves both sides
/// together and reports zero. What it does catch is a divergence between the merge and the recording of it,
/// which is the mistake an edit to one and not the other makes. The INDEPENDENT check is
/// <c>ModCandidateTablesTests.ReferenceMerge</c>, a second merge written in the test file rather than shared
/// with this code, run over every (signature, kind, band) key.
/// </para>
/// </summary>
public sealed partial class ModCandidateTables
{
    /// <summary>
    /// Walks every key once. The cursors are per tag POSITION rather than a fixed four, because a signature
    /// carries up to <see cref="MaxGenerationTagPositions"/> of them, and the loop bound is the signature's
    /// own tag count so a two tag base pays for two.
    /// </summary>
    void BuildOverlap()
    {
        int positions = TagPositionCount;
        if (positions == 0 || _kinds.Length == 0 || Signatures.Count == 0)
        {
            return;
        }

        int widest = 0;
        for (int bucket = 0; bucket < _bucketLength.Length; bucket++)
        {
            if (_bucketLength[bucket] > widest)
            {
                widest = _bucketLength[bucket];
            }
        }

        var scratch = new ushort[positions][];
        for (int position = 0; position < positions; position++)
        {
            scratch[position] = new ushort[widest];
        }

        var scratchCount = new int[positions];
        var scratchWeight = new int[positions];
        Span<int> buckets = stackalloc int[positions];
        Span<int> cursor = stackalloc int[positions];
        Span<int> end = stackalloc int[positions];
        Span<int> key = stackalloc int[positions];

        for (int signature = 0; signature < Signatures.Count; signature++)
        {
            ReadOnlySpan<int> tags = Signatures.TagsOf(signature);
            for (int band = 0; band < BandCount; band++)
            {
                for (int kind = 0; kind < _kinds.Length; kind++)
                {
                    for (int position = 0; position < tags.Length; position++)
                    {
                        buckets[position] = BucketOf(tags[position], kind, band);
                    }

                    Scan(buckets[..tags.Length], cursor, end, key, scratch, scratchCount, scratchWeight,
                        out long mergedCount, out long mergedWeight);
                    Record(signature, kind, band, buckets[..tags.Length], scratch, scratchCount, scratchWeight,
                        mergedCount, mergedWeight);
                }
            }
        }

        if (_suppressUsed == _suppressIndex.Length)
        {
            return;
        }

        var trimmed = new ushort[_suppressUsed];
        Array.Copy(_suppressIndex, trimmed, _suppressUsed);
        _suppressIndex = trimmed;
    }

    /// <summary>
    /// One cursor a tag position, one advance per cursor holding the winning key, and the EARLIEST cursor
    /// holding it is the winner spec 8.3 keeps. Every other cursor on that key records the entry as
    /// suppressed, which includes a table repeating a key of its own: the key is already spent when the walk
    /// reaches it a second time.
    /// </summary>
    void Scan(
        ReadOnlySpan<int> buckets,
        Span<int> cursor,
        Span<int> end,
        Span<int> key,
        ushort[][] scratch,
        int[] scratchCount,
        int[] scratchWeight,
        out long mergedCount,
        out long mergedWeight)
    {
        for (int position = 0; position < scratchCount.Length; position++)
        {
            scratchCount[position] = 0;
            scratchWeight[position] = 0;
        }

        for (int position = 0; position < buckets.Length; position++)
        {
            int bucket = buckets[position];
            int start = bucket < 0 ? 0 : _bucketStart[bucket];
            cursor[position] = start;
            end[position] = start + BucketLength(bucket);
            key[position] = cursor[position] < end[position] ? _entryPacked[cursor[position]] : int.MaxValue;
        }

        mergedCount = 0;
        mergedWeight = 0;

        // No packed key is negative, because a mod id is at least 1 and the ordinal sits in the low bits, so
        // -1 is a first pass that can never look like a repeat.
        int previous = -1;
        while (true)
        {
            int best = int.MaxValue;
            for (int position = 0; position < buckets.Length; position++)
            {
                if (key[position] < best)
                {
                    best = key[position];
                }
            }

            if (best == int.MaxValue)
            {
                return;
            }

            bool wanted = best != previous;
            previous = best;
            for (int position = 0; position < buckets.Length; position++)
            {
                if (key[position] != best)
                {
                    continue;
                }

                int index = cursor[position];
                int start = end[position] - BucketLength(buckets[position]);
                int weight = _entryCumulative[index] - (index == start ? 0 : _entryCumulative[index - 1]);
                if (wanted)
                {
                    wanted = false;
                    mergedCount++;
                    mergedWeight += weight;
                }
                else
                {
                    scratch[position][scratchCount[position]++] = (ushort)(index - start);
                    scratchWeight[position] += weight;
                }

                cursor[position] = index + 1;
                key[position] = cursor[position] < end[position] ? _entryPacked[cursor[position]] : int.MaxValue;
            }
        }
    }

    /// <summary>
    /// Writes one key's headers and runs the self check. The live count and weight the scalars imply are
    /// compared against what the merge actually saw, and a disagreement is COUNTED rather than thrown,
    /// because the number belongs beside budget 9 and a red test is what a non-zero one is for.
    /// </summary>
    void Record(
        int signature,
        int kindPosition,
        int band,
        ReadOnlySpan<int> buckets,
        ushort[][] scratch,
        int[] scratchCount,
        int[] scratchWeight,
        long mergedCount,
        long mergedWeight)
    {
        long liveCount = 0;
        long liveWeight = 0;
        for (int position = 0; position < buckets.Length; position++)
        {
            int header = HeaderOf(signature, kindPosition, band, position);
            _suppressStart[header] = _suppressUsed;
            _suppressCount[header] = scratchCount[position];
            _suppressWeight[header] = scratchWeight[position];
            Append(scratch[position], scratchCount[position]);
            liveCount += BucketLength(buckets[position]) - scratchCount[position];
            liveWeight += BucketTotal(buckets[position]) - scratchWeight[position];
        }

        if (liveCount != mergedCount || liveWeight != mergedWeight)
        {
            ConsistencyFailures++;
        }

        // The UNION over a signature's tag positions is the bound a roll's mod draw is taken over, and
        // NextInt takes an int. KEC0113 bounds one TAG's rows, which is a subset of the union rather than
        // the union, so a base whose tags each sit under the ceiling can still sum past it. A saturating or
        // wrapping bound there is a silently wrong distribution on every roll of that base, so the build
        // refuses it and the boot fails closed.
        if (liveWeight > int.MaxValue)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"The live pool for tag signature {signature}, mod kind {_kinds[kindPosition]} and band {band} sums to {liveWeight} over its {buckets.Length} tag positions, past the int ceiling of {int.MaxValue}. A roll draws over that union with one bounded draw, so a wider one cannot be drawn at all."));
        }
    }

    /// <summary>Appends one position's suppressed indices to the shared flat list, growing it by doubling.</summary>
    void Append(ushort[] entries, int count)
    {
        if (count == 0)
        {
            return;
        }

        if (_suppressUsed + count > _suppressIndex.Length)
        {
            int grown = Math.Max(Math.Max(_suppressIndex.Length * 2, 64), _suppressUsed + count);
            var replacement = new ushort[grown];
            Array.Copy(_suppressIndex, replacement, _suppressUsed);
            _suppressIndex = replacement;
        }

        Array.Copy(entries, 0, _suppressIndex, _suppressUsed, count);
        _suppressUsed += count;
    }
}
