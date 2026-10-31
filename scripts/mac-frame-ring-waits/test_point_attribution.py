import copy
import unittest
import point_attribution as point
from analyze import analyse
from test_release_identity import release_fixture, release_contract

PURPOSE = 'point-shadow-attribution-release-21.21.0'
LIMITS = dict(callbacks=4096, requests=128, rows=131072, casters=131072, references=2097152,
              spans=32768, contributors=16384, inputs=32768)


def caster(key=71, x=0):
    return dict(kind=0, meshIndex=1, meshGeneration=2, castKind=1, dissolveWord=0, motionKey=key,
                matrixWords=[1065353216 if i in (0, 5, 10, 15) else x if i == 12 else 0 for i in range(16)])


def signature(header, casters):
    # Independent fixture mixer. Tests also pin hand-checked round literals.
    h, mask = 0x27d4eb2f165667c5, (1 << 64) - 1
    words = header[:]
    for c in casters:
        words.append(c['meshIndex'] | c['meshGeneration'] << 32)
        if c['kind'] == 0:
            words.append(c['dissolveWord'] | c['castKind'] << 32)
        words.extend(c['matrixWords'][i] | c['matrixWords'][i+1] << 32 for i in range(0, 16, 2))
    for word in words:
        h = (h + word * 0xc2b2ae3d27d4eb4f) & mask
        h = ((h << 31) | (h >> 33)) & mask
        h = h * 0x9e3779b185ebca87 & mask
    return h


def fixture(first=None, second=None, *, header_change=False):
    report, proof = release_fixture()
    report['schemaVersion'] = 6
    proof.update(purpose=PURPOSE, inputContract=dict(release_contract(), purpose=PURPOSE, schemaVersion=6))
    proof['helperHashes']['point_attribution.py'] = 'd'*64
    proof['sourceHashes'].append(dict(path='scripts/mac-frame-ring-waits/point_attribution.py', sha256='d'*64))
    epoch = report['samples'][0]['shadowStage']['request']['epoch']
    arena = dict(epoch=epoch, limits=LIMITS.copy(), allocatedBytes=61669376, overflow=0, overflowSequence=0,
                 matrixOrder='row-major-M11-M44', frames=[], rows=[], casters=[], spans=[], references=[], inputs=[])
    report['pointShadowAttribution'] = arena
    previous = 0
    for index, casters in enumerate((first or [caster()], second or [caster(x=1)])):
        header = [0]*12
        header[2], header[4] = 2000, 6000
        if header_change and index: header[1] = 1
        input_row = dict(headerWords=header, rawWords=[0, 1073741824, 0, 1086324736, 0, 0, 0, 0, 0, 0, 0])
        if input_row not in arena['inputs']: arena['inputs'].append(input_row)
        refs = []
        for ordinal, c in enumerate(casters):
            if c not in arena['casters']: arena['casters'].append(c)
            refs.append(dict(caster=arena['casters'].index(c), locator0=ordinal, locator1=0))
        span_id = next((i for i, existing in enumerate(arena['spans']) if existing['rigidCount'] == sum(c['kind'] == 0 for c in casters) and arena['references'][existing['start']:existing['start']+existing['count']] == refs), None)
        if span_id is None:
            span_id = len(arena['spans'])
            span = dict(start=len(arena['references']), count=len(refs), rigidCount=sum(c['kind'] == 0 for c in casters))
            arena['references'].extend(refs)
            arena['spans'].append(span)
        value = signature(header, casters)
        row = dict(key=9, mode=1, lightIndex=0, ordinal=0, slot=1,
                   slotOutcome=2 if not index else 1, priorDirty=not index,
                   priorLastRenderedFrame=-(1 << 31) if not index else 100,
                   signatureStatus=1, oldSignature=previous, newSignature=value, mismatch=previous != value,
                   dirtyAfter=True, span=span_id, selectedOrdinal=0, completed=True, transientSlot=-1,
                   transientSelectedOrdinal=-1, transientCompleted=False, rigidDraws=6, foliageDraws=0,
                   dynamicSkinnedDraws=0, transientSkinnedDraws=0, rigidInstances=6*len(casters), foliageInstances=0,
                   input=arena['inputs'].index(input_row))
        arena['rows'].append(row)
        frame = dict(epoch=epoch, sequence=index+1, sceneFrame=100+index, rowStart=index, rowCount=1,
                     casterCount=len(arena['casters']), spanCount=len(arena['spans']), referenceCount=len(arena['references']),
                     inputCount=len(arena['inputs']), availability=1, complete=True)
        arena['frames'].append(frame)
        snapshot = report['samples'][index]['shadowStage']['callback']['snapshot']
        snapshot['pointAttribution'] = frame.copy()
        snapshot['counters'].update(selectedTransientRows=0, staticRequests=1, dynamicRequests=0, signatures=1, signatureMismatches=int(previous != value),
                                    selectedStaticRows=1, selectedDynamicRows=0, completedStaticRebuilds=1, completedDynamicRenders=0,
                                    pointFaceDrawCalls=6, pointFoliageFaceDrawCalls=0, pointDynamicSkinnedFaceDrawCalls=0,
                                    pointTransientSkinnedFaceDrawCalls=0, pointRigidIssuedInstances=6*len(casters), pointFoliageIssuedInstances=0)
        previous = value
    return report, proof


class PointAttributionTests(unittest.TestCase):
    def test_exact_mixer_literals(self):
        for word, expected in ((0, 0x5eb068eb660a28a1), (1, 0x7bf616f8cf5a8ffa), ((1 << 64)-1, 0x0460a0217cb9c148)):
            self.assertEqual(expected, point.mix(0x27d4eb2f165667c5, word))

    def audit(self, report, proof):
        return point.audit_attribution(report, proof)

    def test_keyed_one_ulp_and_signed_zero_are_exact_changes_with_no_invented_prior(self):
        for word in (1, 0x80000000):
            r, p = fixture(second=[caster(x=word)])
            result = self.audit(r, p)
            self.assertEqual('valid', result['integrity']['status'], result)
            light = result['lights'][0]
            self.assertEqual('baseline-only', light['history'][0]['continuity'])
            self.assertIn('keyed-transform', light['history'][1]['classifications'])
            self.assertEqual(2, light['firstChange']['sequence'])
            self.assertEqual(71, light['history'][1]['keyedChanges'][0]['motionKey'])
            self.assertEqual('valid', analyse(r, 1, 123, p, expected_recording_slots=3)['status'])

    def test_order_only_membership_header_and_foliage_have_separate_evidence(self):
        a, b = caster(), caster(72, 1065353216)
        for second, header_change, expected in (([b, a], False, 'order-only'), ([a], False, 'rigid-membership'),
                                                 ([a, b], True, 'header')):
            r, p = fixture([a, b], second, header_change=header_change)
            result = self.audit(r, p)
            self.assertEqual('valid', result['integrity']['status'], result)
            self.assertIn(expected, result['lights'][0]['history'][1]['classifications'])
        a, b = caster(), caster(x=1)
        for c in (a, b): c.update(kind=1, castKind=0, motionKey=0)
        r, p = fixture([a], [b])
        self.assertIn('foliage-membership', self.audit(r, p)['lights'][0]['history'][1]['classifications'])

    def test_unkeyed_and_duplicate_keys_are_ambiguous_and_show_added_removed_records(self):
        for first, second, expected in (([caster(0)], [caster(0, 1)], 'ambiguous-unkeyed'),
                                        ([caster(), caster()], [caster(), caster(x=1)], 'ambiguous-duplicate-motion-key')):
            r, p = fixture(first, second)
            result = self.audit(r, p)
            history = result['lights'][0]['history'][1]
            self.assertIn(expected, history['classifications'])
            self.assertTrue(history['added']); self.assertTrue(history['removed'])
            self.assertEqual([], history['keyedChanges'])

    def test_malformed_types_references_ownership_hashes_states_and_accounting_are_invalid(self):
        changes = [lambda r: r.update(schemaVersion=5),
                   lambda r: r['pointShadowAttribution']['rows'][0].update(input=True),
                   lambda r: r['pointShadowAttribution']['rows'][0].update(span=9),
                   lambda r: r['pointShadowAttribution']['rows'][0].update(newSignature=1),
                   lambda r: r['pointShadowAttribution']['rows'][0].update(selectedOrdinal=-1),
                   lambda r: r['pointShadowAttribution']['rows'][0].update(rigidDraws=5),
                   lambda r: r['pointShadowAttribution']['rows'][0].update(oldSignature=-1),
                   lambda r: r['pointShadowAttribution']['casters'][0]['matrixWords'].__setitem__(0, 1 << 32),
                   lambda r: r['pointShadowAttribution']['references'][0].update(caster=99),
                   lambda r: r['pointShadowAttribution']['frames'][0].update(sequence=2),
                   lambda r: r['pointShadowAttribution']['frames'][0].update(epoch='00000000-0000-0000-0000-000000000000'),
                   lambda r: r['pointShadowAttribution']['frames'][0].update(casterCount=0),
                   lambda r: r['pointShadowAttribution']['frames'][0].update(rowCount=0),
                   lambda r: r['pointShadowAttribution']['frames'].append(copy.deepcopy(r['pointShadowAttribution']['frames'][0])),
                   lambda r: r['pointShadowAttribution']['rows'][1].update(oldSignature=0)]
        for change in changes:
            r, p = fixture(); change(r)
            with self.subTest(change=change): self.assertEqual('invalid', self.audit(r, p)['integrity']['status'])

    def test_overflow_and_missing_are_explicitly_limited(self):
        r, p = fixture()
        r['pointShadowAttribution'].update(overflow=5, overflowSequence=2)
        frame = r['pointShadowAttribution']['frames'][1]
        frame.update(availability=6, complete=False)
        r['samples'][1]['shadowStage']['callback']['snapshot']['pointAttribution'] = frame.copy()
        result = self.audit(r, p)
        self.assertEqual('valid', result['integrity']['status'], result)
        self.assertEqual('overflow', result['availability']['status'])
        self.assertEqual('limited', analyse(r, 1, 123, p, expected_recording_slots=3)['status'])
        del r['pointShadowAttribution']
        self.assertEqual('invalid', self.audit(r, p)['integrity']['status'])

    def test_callback_ownership_and_boolean_numeric_aliases_are_refused(self):
        edits = [lambda r: r['samples'][0]['shadowStage']['request'].update(sequence=True),
                 lambda r: r['samples'][0]['shadowStage']['callback'].update(producerCount=2),
                 lambda r: r['samples'][0]['shadowStage']['callback']['request'].update(mode=True),
                 lambda r: r['samples'][0]['shadowStage']['callback']['snapshot']['request'].update(sequence=2)]
        for edit in edits:
            r, p = fixture(); edit(r)
            with self.subTest(edit=edit):
                self.assertEqual('invalid', self.audit(r, p)['integrity']['status'])

    def test_keyed_mesh_cast_and_dissolve_evidence_is_field_specific(self):
        for field, value, label in (('meshGeneration', 3, 'keyed-mesh'), ('castKind', 2, 'keyed-cast'),
                                    ('dissolveWord', 6, 'keyed-dissolve')):
            changed = caster(); changed[field] = value
            r, p = fixture(second=[changed])
            self.assertIn(label, self.audit(r, p)['lights'][0]['history'][1]['classifications'])

    def test_keyed_position_swap_is_motion_even_when_the_exact_multiset_is_unchanged(self):
        r, p = fixture([caster(71, 0), caster(72, 1065353216)], [caster(71, 1065353216), caster(72, 0)])
        history = self.audit(r, p)['lights'][0]['history'][1]
        self.assertIn('keyed-transform', history['classifications'])
        self.assertNotIn('order-only', history['classifications'])
        self.assertEqual(2, len(history['keyedChanges']))

    def test_unchanged_unkeyed_ground_does_not_make_unique_keyed_motion_ambiguous(self):
        ground = caster(0, 1065353216)
        r, p = fixture([ground, caster(71)], [ground, caster(71, 1)])
        history = self.audit(r, p)['lights'][0]['history'][1]
        self.assertEqual(['keyed-transform'], history['classifications'])

    def test_shared_static_slot_can_be_selected_after_a_later_request_adds_dirt(self):
        r, p = fixture(second=[caster()])
        a = r['pointShadowAttribution']
        rows = []
        for i in range(2):
            first = a['rows'][i]
            second = copy.deepcopy(first)
            second.update(ordinal=1, lightIndex=1, selectedOrdinal=1)
            if i == 0:
                second.update(oldSignature=first['newSignature'], mismatch=False, priorDirty=True)
            else:
                first.update(priorDirty=False, mismatch=False, dirtyAfter=False)
                changed = copy.deepcopy(a['inputs'][0]); changed['headerWords'][1] = 1
                a['inputs'].append(changed)
                second.update(priorDirty=False, mismatch=True, dirtyAfter=True, input=1,
                              newSignature=signature(changed['headerWords'], [caster()]))
            rows.extend((first, second))
            frame = a['frames'][i]
            frame.update(rowStart=i*2, rowCount=2, inputCount=1+i)
            snapshot = r['samples'][i]['shadowStage']['callback']['snapshot']
            snapshot['pointAttribution'] = frame.copy()
            snapshot['counters'].update(staticRequests=2, signatures=2, signatureMismatches=1,
                                        selectedStaticRows=2, completedStaticRebuilds=2,
                                        pointFaceDrawCalls=12, pointRigidIssuedInstances=12)
        a['rows'] = rows
        result = self.audit(r, p)
        self.assertEqual('valid', result['integrity']['status'], result)
        self.assertTrue(all(h['continuity'] == 'ambiguous-static-key' for h in result['lights'][0]['history']))
        self.assertTrue(all(not h['keyedChanges'] for h in result['lights'][0]['history']))

    def test_motion_key_scratch_overflow_is_valid_but_limited(self):
        r, p = fixture()
        r['pointShadowAttribution'].update(overflow=9, overflowSequence=2)
        frame = r['pointShadowAttribution']['frames'][1]
        frame.update(availability=6, complete=False)
        r['samples'][1]['shadowStage']['callback']['snapshot']['pointAttribution'] = frame.copy()
        result = self.audit(r, p)
        self.assertEqual('valid', result['integrity']['status'], result)
        self.assertEqual('overflow', result['availability']['status'])
        self.assertEqual(9, result['availability']['overflowCapacity'])

    def test_point_purpose_requires_its_helper_inventory_and_legacy_inventory_stays_frozen(self):
        import inputs
        import capacity
        r, p = fixture()
        self.assertEqual(661, inputs.capture_profile(p)['runtimeFiles'])
        self.assertEqual(411, inputs.capture_profile(p)['contentFiles'])
        self.assertEqual(6, len(inputs.helper_files(PURPOSE)))
        self.assertEqual(5, len(inputs.helper_files(inputs.RELEASE_PURPOSE)))
        self.assertEqual('valid', capacity.audit_treatment(r, p, 3, input_contract=p['inputContract'])['status'])
        del p['helperHashes']['point_attribution.py']
        self.assertEqual('invalid', analyse(r, 1, 123, p, expected_recording_slots=3)['status'])

    def test_new_and_old_purpose_schema_pairs_cannot_be_substituted(self):
        r, p = fixture()
        for value in ('paused-sun-input-stability-release-21.21.0', None):
            p['purpose'] = value
            self.assertEqual('invalid', self.audit(r, p)['integrity']['status'])
