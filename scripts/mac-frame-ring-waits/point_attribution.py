"""Validate exact consumed point-shadow inputs and classify observed changes."""
from collections import Counter
import struct
from inputs import _require as require, _object as obj, _integer as integer, _words as words

PURPOSE = 'point-shadow-attribution-release-21.21.0'
MAXIMA = dict(callbacks=4096, requests=128, rows=131072, casters=131072, references=2097152,
              spans=32768, contributors=16384, inputs=32768)
U64 = (1 << 64)-1
I32 = (1 << 31)-1
ROW_INTS = ('mode lightIndex ordinal slot slotOutcome priorLastRenderedFrame signatureStatus span '
            'selectedOrdinal transientSlot transientSelectedOrdinal rigidDraws foliageDraws '
            'dynamicSkinnedDraws transientSkinnedDraws input').split()
ROW_FLAGS = 'priorDirty mismatch dirtyAfter completed transientCompleted'.split()
ROW_LONGS = 'key oldSignature newSignature rigidInstances foliageInstances'.split()
FRAME_FIELDS = ('epoch sequence sceneFrame rowStart rowCount casterCount spanCount referenceCount inputCount '
                'availability complete').split()


def mix(hash_value, word):
    value = (hash_value + word * 0xc2b2ae3d27d4eb4f) & U64
    return (((value << 31) | (value >> 33)) * 0x9e3779b185ebca87) & U64


def token(caster):
    return (caster['kind'], caster['meshIndex'], caster['meshGeneration'], caster['castKind'],
            caster['dissolveWord'], *caster['matrixWords'])


def reconstruct(header, casters):
    value = 0x27d4eb2f165667c5
    for word in header:
        value = mix(value, word)
    for c in casters:
        value = mix(value, c['meshIndex'] | c['meshGeneration'] << 32)
        if c['kind'] == 0:
            value = mix(value, c['dissolveWord'] | c['castKind'] << 32)
        for i in range(0, 16, 2):
            value = mix(value, c['matrixWords'][i] | c['matrixWords'][i+1] << 32)
    return value


def position(words):
    return [struct.unpack('<f', struct.pack('<I', word))[0] for word in words]


def evidence(c):
    return dict(c, positionWords=c['matrixWords'][12:15], position=position(c['matrixWords'][12:15]))


def changes(previous, current):
    labels, keyed, added, removed = [], [], [], []
    if previous['input']['headerWords'] != current['input']['headerWords']:
        labels.append('header')
    for kind in (0, 1):
        left = [c for c in previous['casters'] if c['kind'] == kind]
        right = [c for c in current['casters'] if c['kind'] == kind]
        lt, rt = [token(c) for c in left], [token(c) for c in right]
        if lt == rt:
            continue
        same_multiset = Counter(lt) == Counter(rt)
        local_keyed = []
        if kind == 0:
            lk, rk = Counter(c['motionKey'] for c in left), Counter(c['motionKey'] for c in right)
            for key in sorted((set(lk) & set(rk))-{0}):
                if lk[key] != 1 or rk[key] != 1:
                    continue
                before = next(c for c in left if c['motionKey'] == key)
                after = next(c for c in right if c['motionKey'] == key)
                fields = [label for field, label in (('matrixWords', 'transform'), ('castKind', 'cast'),
                                                      ('dissolveWord', 'dissolve')) if before[field] != after[field]]
                if any(before[field] != after[field] for field in ('meshIndex', 'meshGeneration')):
                    fields.append('mesh')
                if fields:
                    local_keyed.append(dict(motionKey=key, fields=fields, before=evidence(before), after=evidence(after)))
        # A keyed position exchange can preserve the exact multiset while proving motion.
        if same_multiset and not local_keyed:
            labels.append('order-only')
            continue
        for rows, counts, target in ((left, Counter(lt)-Counter(rt), removed),
                                      (right, Counter(rt)-Counter(lt), added)):
            for c in rows:
                t = token(c)
                if counts[t]:
                    target.append(evidence(c)); counts[t] -= 1
        if kind == 1:
            labels.append('foliage-membership')
            continue
        duplicate_keys = {key for key, count in (*lk.items(), *rk.items()) if key and count > 1}
        ambiguous_left = Counter((c['motionKey'], token(c)) for c in left if c['motionKey'] in duplicate_keys)
        ambiguous_right = Counter((c['motionKey'], token(c)) for c in right if c['motionKey'] in duplicate_keys)
        if ambiguous_left != ambiguous_right:
            labels.append('ambiguous-duplicate-motion-key')
        if Counter(token(c) for c in left if not c['motionKey']) != Counter(token(c) for c in right if not c['motionKey']):
            labels.append('ambiguous-unkeyed')
        if set(lk)-{0} != set(rk)-{0} or len(left) != len(right):
            labels.append('rigid-membership')
        for change in local_keyed:
            labels.extend('keyed-'+field for field in change['fields'])
        keyed.extend(local_keyed)
    return dict(classifications=list(dict.fromkeys(labels)) or ['unchanged'], keyedChanges=keyed, added=added, removed=removed)


def _tables(arena, nonfinite):
    obj(arena, ('epoch limits allocatedBytes overflow overflowSequence matrixOrder frames rows casters spans references inputs').split(), 'pointShadowAttribution')
    from analyze import guid
    guid(arena['epoch'])
    obj(arena['limits'], MAXIMA, 'limits')
    for key, high in MAXIMA.items(): integer(arena['limits'][key], 1, high, 'limits.'+key)
    integer(arena['allocatedBytes'], 1, 64*1024*1024-1, 'allocatedBytes')
    integer(arena['overflow'], 0, 9, 'overflow'); integer(arena['overflowSequence'], 0, (1 << 63)-1, 'overflowSequence')
    require((arena['overflow'] == 0) == (arena['overflowSequence'] == 0), 'overflow metadata differs')
    require(arena['matrixOrder'] == 'row-major-M11-M44', 'matrixOrder differs')
    for name, limit in (('frames', 'callbacks'), ('rows', 'rows'), ('casters', 'casters'), ('spans', 'spans'),
                        ('references', 'references'), ('inputs', 'inputs')):
        require(type(arena[name]) is list and len(arena[name]) <= arena['limits'][limit], name+' exceeds its capacity')
    seen = set()
    for c in arena['casters']:
        obj(c, ('kind meshIndex meshGeneration castKind dissolveWord motionKey matrixWords').split(), 'caster')
        integer(c['kind'], 0, 1, 'caster.kind'); integer(c['meshIndex'], 0, I32, 'meshIndex')
        integer(c['meshGeneration'], 0, I32, 'meshGeneration'); integer(c['castKind'], 0, 3, 'castKind')
        integer(c['dissolveWord'], 0, 784, 'dissolveWord'); integer(c['motionKey'], 0, U64, 'motionKey')
        require(c['dissolveWord'] & 255 <= 16 and c['dissolveWord'] & ~0x31f == 0, 'dissolve flags differ')
        if c['kind'] == 1: require(c['castKind'] == c['dissolveWord'] == c['motionKey'] == 0, 'foliage metadata differs')
        else: require(c['castKind'] > 0, 'rigid cast kind is not casting')
        words(c['matrixWords'], 16, 'matrixWords', nonfinite)
        identity = (*token(c), c['motionKey'])
        require(identity not in seen, 'duplicate interned caster'); seen.add(identity)
    seen = set()
    for value in arena['inputs']:
        obj(value, ('headerWords', 'rawWords'), 'input')
        require(type(value['headerWords']) is list and len(value['headerWords']) == 12, 'header length differs')
        for word in value['headerWords']: integer(word, 0, U64, 'header word')
        words(value['rawWords'], 11, 'rawWords', nonfinite)
        identity = (*value['headerWords'], *value['rawWords'])
        require(identity not in seen, 'duplicate interned input'); seen.add(identity)
    for ref in arena['references']:
        obj(ref, ('caster', 'locator0', 'locator1'), 'reference')
        integer(ref['caster'], 0, len(arena['casters'])-1, 'reference.caster')
        for key in ('locator0', 'locator1'): integer(ref[key], 0, I32, key)
    next_ref, seen = 0, set()
    for span in arena['spans']:
        obj(span, ('start', 'count', 'rigidCount'), 'span')
        require(integer(span['start'], 0, len(arena['references']), 'span.start') == next_ref, 'span inventory is not contiguous')
        integer(span['count'], 0, arena['limits']['contributors'], 'span.count')
        integer(span['rigidCount'], 0, span['count'], 'span.rigidCount')
        next_ref += span['count']; require(next_ref <= len(arena['references']), 'span references exceed inventory')
        refs = arena['references'][span['start']:next_ref]
        require(all(arena['casters'][ref['caster']]['kind'] == (0 if i < span['rigidCount'] else 1)
                    for i, ref in enumerate(refs)), 'rigid/foliage order differs')
        identity = (span['rigidCount'], tuple((ref['caster'], ref['locator0'], ref['locator1']) for ref in refs))
        require(identity not in seen, 'duplicate interned span'); seen.add(identity)
    require(next_ref == len(arena['references']), 'lost reference inventory')


def _row(row, frame, arena):
    obj(row, [*ROW_INTS, *ROW_FLAGS, *ROW_LONGS], 'row')
    integer(row['key'], -(1 << 63), (1 << 63)-1, 'key')
    for name in ROW_FLAGS: require(type(row[name]) is bool, name+' must be Boolean')
    for name in ROW_INTS: integer(row[name], -(1 << 31), I32, name)
    for name in ('oldSignature', 'newSignature'): integer(row[name], 0, U64, name)
    for name in ('rigidInstances', 'foliageInstances'): integer(row[name], 0, (1 << 63)-1, name)
    for name in ('lightIndex', 'ordinal', 'rigidDraws', 'foliageDraws', 'dynamicSkinnedDraws', 'transientSkinnedDraws'):
        integer(row[name], 0, I32, name)
    integer(row['mode'], 1, 2, 'mode'); integer(row['slotOutcome'], 0, 3, 'slotOutcome')
    integer(row['signatureStatus'], 0, 3, 'signatureStatus')
    for name in ('slot', 'span', 'selectedOrdinal', 'transientSlot', 'transientSelectedOrdinal'): integer(row[name], -1, I32, name)
    integer(row['input'], 0, frame['inputCount']-1, 'row.input')
    if frame['availability'] == 1:
        require(row['signatureStatus'] == (0 if row['mode'] == 2 else 1 if row['slot'] >= 0 else 2), 'signature applicability differs')
    elif frame['availability'] == 3:
        require(row['slot'] == -1 and row['slotOutcome'] == 0 and row['signatureStatus'] == (3 if row['mode'] == 1 else 0), 'missing-atlas request differs')
    else:
        require(False, 'disabled or empty callback contains requests')
    evaluated = row['signatureStatus'] == 1
    require(not evaluated or row['mode'] == 1 and row['slot'] >= 0, 'inapplicable signature')
    require((row['slotOutcome'] in (1, 2)) == (row['slot'] >= 0), 'slot outcome differs')
    slot_dirty = any(other['slot'] == row['slot'] and other['dirtyAfter'] is True
                     for other in arena['rows'][frame['rowStart']:frame['rowStart']+frame['rowCount']])
    require(row['selectedOrdinal'] < 0 or row['slot'] >= 0 and slot_dirty, 'selected clean or invalid row')
    require(row['completed'] == (row['selectedOrdinal'] >= 0), 'selection/completion differs')
    require(row['transientCompleted'] == (row['transientSelectedOrdinal'] >= 0), 'transient completion differs')
    require((row['transientSlot'] >= 0) == row['transientCompleted'], 'transient slot differs')
    if not row['completed'] and not row['transientCompleted']:
        require(all(row[k] == 0 for k in ('rigidDraws', 'foliageDraws', 'dynamicSkinnedDraws', 'transientSkinnedDraws',
                                          'rigidInstances', 'foliageInstances')), 'unselected work exists')
    casters = []
    if evaluated:
        integer(row['span'], 0, frame['spanCount']-1, 'row.span')
        span = arena['spans'][row['span']]
        require(span['start']+span['count'] <= frame['referenceCount'], 'forward contributor references')
        refs = arena['references'][span['start']:span['start']+span['count']]
        require(all(ref['caster'] < frame['casterCount'] for ref in refs), 'forward caster reference')
        casters = [arena['casters'][ref['caster']] for ref in refs]
        require(reconstruct(arena['inputs'][row['input']]['headerWords'], casters) == row['newSignature'], 'computed signature differs')
        require(row['mismatch'] == (row['oldSignature'] != row['newSignature']), 'mismatch flag differs')
        require(row['dirtyAfter'] == (row['priorDirty'] or row['mismatch']), 'dirty decision differs')
    else:
        require(row['span'] == -1 and row['oldSignature'] == row['newSignature'] == 0 and not row['mismatch'], 'unevaluated signature has data')
        require(not any(arena['inputs'][row['input']]['headerWords']), 'unevaluated header is not canonical')
    return casters


def _account(rows, counts):
    checks = dict(staticRequests=sum(r['mode'] == 1 for r in rows), dynamicRequests=sum(r['mode'] == 2 for r in rows),
                  signatures=sum(r['signatureStatus'] == 1 for r in rows), signatureMismatches=sum(r['mismatch'] for r in rows),
                  selectedStaticRows=sum(r['mode'] == 1 and r['selectedOrdinal'] >= 0 for r in rows),
                  selectedDynamicRows=sum(r['mode'] == 2 and r['selectedOrdinal'] >= 0 for r in rows),
                  selectedTransientRows=sum(r['transientCompleted'] for r in rows),
                  completedStaticRebuilds=sum(r['mode'] == 1 and r['completed'] for r in rows),
                  completedDynamicRenders=sum(r['mode'] == 2 and r['completed'] for r in rows),
                  pointFaceDrawCalls=sum(r['rigidDraws']+r['foliageDraws']+r['dynamicSkinnedDraws']+r['transientSkinnedDraws'] for r in rows),
                  pointFoliageFaceDrawCalls=sum(r['foliageDraws'] for r in rows),
                  pointDynamicSkinnedFaceDrawCalls=sum(r['dynamicSkinnedDraws'] for r in rows),
                  pointTransientSkinnedFaceDrawCalls=sum(r['transientSkinnedDraws'] for r in rows),
                  pointRigidIssuedInstances=sum(r['rigidInstances'] for r in rows), pointFoliageIssuedInstances=sum(r['foliageInstances'] for r in rows))
    for key, value in checks.items():
        require(type(counts[key]) is int and counts[key] == value, 'aggregate accounting differs: '+key)
    selected = [r['selectedOrdinal'] for r in rows if r['selectedOrdinal'] >= 0]
    transient = [r['transientSelectedOrdinal'] for r in rows if r['transientSelectedOrdinal'] >= 0]
    require(sorted(selected+transient) == list(range(len(selected)+len(transient))), 'selected order is not unique and contiguous')


def audit_attribution(report, proof):
    errors, lights, availability, nonfinite = [], {}, Counter(), []
    overflow, first_overflow, observed = 0, 0, 0
    try:
        from inputs import validate_contract, _request, _counter_schema
        require(type(proof) is dict and proof.get('purpose') == PURPOSE, 'attribution purpose differs')
        contract = validate_contract(proof['inputContract'])
        require(contract['schemaVersion'] == integer(report['schemaVersion'], 6, 6, 'schemaVersion'), 'schema/purpose differs')
        arena = report['pointShadowAttribution']; _tables(arena, nonfinite)
        overflow, first_overflow = arena['overflow'], arena['overflowSequence']
        samples = report['samples']; integer(report['sampleCount'], 1, 4096, 'sampleCount')
        require(type(samples) is list and len(samples) == report['sampleCount'], 'sample inventory differs')
        frames = {}
        row_end, prior_counts = 0, [0, 0, 0, 0]
        for frame in arena['frames']:
            obj(frame, FRAME_FIELDS, 'frame')
            require(frame['epoch'] == arena['epoch'], 'frame epoch differs')
            sequence = integer(frame['sequence'], 1, len(samples), 'frame.sequence')
            require(sequence not in frames and (not frames or sequence > max(frames)), 'duplicate or reordered callback')
            integer(frame['sceneFrame'], 1, (1 << 63)-1, 'sceneFrame')
            require(integer(frame['rowStart'], 0, len(arena['rows']), 'rowStart') == row_end, 'lost row ownership')
            row_end += integer(frame['rowCount'], 0, arena['limits']['requests'], 'rowCount')
            require(row_end <= len(arena['rows']), 'row range exceeds inventory')
            for i, (field, table) in enumerate((('casterCount', 'casters'), ('spanCount', 'spans'), ('referenceCount', 'references'), ('inputCount', 'inputs'))):
                prior_counts[i] = integer(frame[field], prior_counts[i], len(arena[table]), field)
            integer(frame['availability'], 0, 6, 'availability')
            require(type(frame['complete']) is bool, 'complete must be Boolean')
            require(frame['complete'] == (frame['availability'] in (1, 2, 3, 4)), 'frame availability/completion differs')
            if frame['availability'] == 6: require(overflow and sequence >= first_overflow, 'false overflow frame')
            frames[sequence] = frame
        require(row_end == len(arena['rows']), 'lost rows')
        require(prior_counts == [len(arena[k]) for k in ('casters', 'spans', 'references', 'inputs')], 'unowned table suffix')
        for sequence, sample in enumerate(samples, 1):
            stage = sample['shadowStage']; request = stage['request']
            expected = dict(epoch=arena['epoch'], sequence=sequence, mode=1)
            _request(request, expected)
            integer(stage['status'], 1, 1, 'stage.status')
            callback = stage['callback']
            _request(callback['request'], expected)
            require(type(callback['completed']) is bool and callback['completed'], 'callback is incomplete')
            integer(callback['producerCount'], 1, 1, 'producerCount')
            snapshot = callback['snapshot']
            _request(snapshot['request'], expected)
            integer(snapshot['status'], 1, 1, 'snapshot.status')
            _counter_schema(snapshot['counters'])
            frame = frames.get(sequence)
            if frame is None:
                require(overflow and sequence >= first_overflow and snapshot.get('pointAttribution') is None, 'missing frame range')
                availability['overflow'] += 1; continue
            require(snapshot.get('pointAttribution') == frame, 'snapshot frame range ownership differs')
            availability[str(frame['availability'])] += 1
            if not frame['complete']:
                continue
            require(frame['sceneFrame'] == snapshot['inputs']['sceneFrameIndex'], 'scene-frame ownership differs')
            rows = arena['rows'][frame['rowStart']:frame['rowStart']+frame['rowCount']]
            require([r['ordinal'] for r in rows] == list(range(len(rows))), 'request ordinal inventory differs')
            require(len({r['lightIndex'] for r in rows}) == len(rows), 'duplicate uploaded light index')
            if frame['availability'] in (2, 4): require(not rows, 'disabled or empty callback contains requests')
            _account(rows, snapshot['counters'])
            keys = Counter(r['key'] for r in rows if r['mode'] == 1)
            observed += 1
            for row in rows:
                casters = _row(row, frame, arena)
                if row['mode'] != 1: continue
                light = lights.setdefault(row['key'], dict(key=row['key'], mismatches=0, rebuilds=0, firstChange=None, history=[]))
                light['mismatches'] += row['mismatch']; light['rebuilds'] += row['completed']
                current = dict(sequence=sequence, sceneFrame=frame['sceneFrame'], timeSeconds=sample['startTimestamp']/report['stopwatchFrequency'],
                               slot=row['slot'], selectedOrdinal=row['selectedOrdinal'], rebuilt=row['completed'], mismatch=row['mismatch'],
                               priorDirty=row['priorDirty'], dirtyAfter=row['dirtyAfter'], signatureStatus=row['signatureStatus'],
                               worldPosition=position(arena['inputs'][row['input']]['rawWords'][:3]), row=dict(row),
                               input=arena['inputs'][row['input']], casters=casters, duplicateKey=keys[row['key']] > 1,
                               continuity='baseline-only', classifications=[], keyedChanges=[], added=[], removed=[])
                previous = light['history'][-1] if light['history'] else None
                if current['duplicateKey'] or previous and previous['duplicateKey']:
                    current['continuity'] = 'ambiguous-static-key'
                elif previous is not None:
                    contiguous = previous['sequence'] == sequence-1
                    current['continuity'] = 'continuous' if contiguous else 'returned-after-gap'
                    if row['signatureStatus'] == previous['signatureStatus'] == 1:
                        if contiguous and row['slotOutcome'] == 1 and row['slot'] == previous['slot']:
                            require(row['oldSignature'] == previous['row']['newSignature'], 'false prior runtime signature')
                        current.update(changes(previous, current))
                        if current['classifications'] != ['unchanged'] and light['firstChange'] is None:
                            light['firstChange'] = dict(sequence=sequence, timeSeconds=current['timeSeconds'])
                        if row['completed'] and row['priorDirty'] and not row['mismatch']:
                            current['classifications'].append('carried-dirty-work')
                light['history'].append(current)
        for light in lights.values():
            sequences = {h['sequence'] for h in light['history']}
            light['absentSequences'] = [i for i in range(min(sequences), len(samples)+1) if i not in sequences]
    except (KeyError, ValueError, TypeError, IndexError, AttributeError, OverflowError) as error:
        errors.append(str(error))
    state = ('overflow' if overflow else 'unusable' if nonfinite else 'complete'
             if observed == report.get('sampleCount') and not any(availability.get(str(i), 0) for i in (0, 2, 3, 5)) else 'unavailable')
    return dict(integrity=dict(status='invalid' if errors else 'valid', errors=errors),
                availability=dict(status=state, overflowCapacity=overflow, firstOverflowSequence=first_overflow,
                                  completedFrames=observed, observations=dict(availability), nonfiniteFields=nonfinite),
                lights=list(lights.values()), limits=['The first observed light has no observed prior contributor list.',
                    'Motion keys attribute only unique keys in both observations. Slots and source ordinals are callback-local.',
                    'Foliage records identify exact rest inputs, not persistent game entities.',
                    'Required content motion and invalidation are observations, not a cache bug or speedup proof.',
                    'CPU waits are not GPU execution or physical presentation.'])
