import copy
import unittest
from unittest.mock import patch
import point_attribution as point
from analyze import analyse
from test_point_attribution import caster, fixture


def partial_fixture(availability=6, stage='slot', index=1):
    report, proof = fixture()
    arena = report['pointShadowAttribution']
    if availability == 6:
        arena.update(overflow=5, overflowSequence=index+1)
    frame = arena['frames'][index]
    frame.update(availability=availability, complete=False)
    report['samples'][index]['shadowStage']['callback']['snapshot']['pointAttribution'] = frame.copy()
    row = arena['rows'][index]
    if stage == 'selected':
        # Selection is retained before the render work completion update.
        row.update(completed=False, rigidDraws=0, rigidInstances=0)
    else:
        # The initial canonical input survives an interrupted signature evaluation.
        initial_input = copy.deepcopy(arena['inputs'][0])
        initial_input['headerWords'] = [0]*12
        arena['inputs'].append(initial_input)
        for i in range(index, len(arena['frames'])):
            arena['frames'][i]['inputCount'] = len(arena['inputs'])
            report['samples'][i]['shadowStage']['callback']['snapshot']['pointAttribution'] = arena['frames'][i].copy()
        row.update(priorLastRenderedFrame=-(1 << 31), signatureStatus=2,
                   oldSignature=0, newSignature=0, mismatch=False, span=-1, input=len(arena['inputs'])-1,
                   selectedOrdinal=-1, completed=False, rigidDraws=0, rigidInstances=0)
        if stage == 'request':
            row.update(slot=-1, slotOutcome=0, priorDirty=False, dirtyAfter=False)
        else:
            row.update(slot=1, slotOutcome=1, priorDirty=True, dirtyAfter=True)
    return report, proof


class RetainedPartialRowTests(unittest.TestCase):
    def test_valid_retained_partial_states_stay_limited_in_both_entry_points(self):
        for availability in (0, 5, 6):
            for stage in ('request', 'slot', 'selected'):
                with self.subTest(availability=availability, stage=stage):
                    report, proof = partial_fixture(availability, stage)
                    audit = point.audit_attribution(report, proof)
                    self.assertEqual('valid', audit['integrity']['status'], audit)
                    self.assertEqual('overflow' if availability == 6 else 'unavailable', audit['availability']['status'])
                    self.assertEqual(1, audit['availability']['completedFrames'])
                    self.assertEqual([dict(first=2, last=2)], audit['requestInventoryCoverage']['unknownSequenceRanges'])
                    full = analyse(report, 1, 123, proof, expected_recording_slots=3)
                    self.assertEqual('limited', full['status'], full)

    def test_retained_partial_rows_reject_malformed_structure_in_both_entry_points(self):
        mutations = [
            ('null', lambda a: a['rows'].__setitem__(1, None)),
            ('extra-field', lambda a: a['rows'][1].update(extra=0)),
            ('missing-field', lambda a: a['rows'][1].pop('span')),
            *[(field, lambda a, field=field: a['rows'][1].update({field: True}))
              for field in ('input', 'slot', 'key', 'oldSignature', 'rigidInstances')],
            *[(field, lambda a, field=field: a['rows'][1].update({field: 1}))
              for field in point.ROW_FLAGS],
            ('mode-enum', lambda a: a['rows'][1].update(mode=3)),
            ('slot-enum', lambda a: a['rows'][1].update(slotOutcome=4)),
            ('signature-enum', lambda a: a['rows'][1].update(signatureStatus=4)),
            ('input-owner', lambda a: a['rows'][1].update(input=len(a['inputs']))),
            ('span-owner', lambda a: a['rows'][1].update(span=len(a['spans']))),
            ('span-sentinel', lambda a: a['rows'][1].update(span=-2)),
            ('slot-bound', lambda a: a['rows'][1].update(slot=8)),
            ('transient-bound', lambda a: a['rows'][1].update(transientSlot=0)),
            ('ordinal-bound', lambda a: a['rows'][1].update(ordinal=1)),
            ('selected-bound', lambda a: a['rows'][1].update(selectedOrdinal=1)),
            ('transient-selected-bound', lambda a: a['rows'][1].update(transientSelectedOrdinal=2)),
            ('negative-draw', lambda a: a['rows'][1].update(rigidDraws=-1)),
            ('negative-instance', lambda a: a['rows'][1].update(rigidInstances=-1)),
            ('signature-bound', lambda a: a['rows'][1].update(newSignature=1 << 64)),
            ('key-bound', lambda a: a['rows'][1].update(key=1 << 63)),
        ]
        for availability in (0, 5, 6):
            for name, mutate in mutations:
                with self.subTest(availability=availability, mutation=name):
                    report, proof = partial_fixture(availability)
                    mutate(report['pointShadowAttribution'])
                    audit = point.audit_attribution(report, proof)
                    full = analyse(report, 1, 123, proof, expected_recording_slots=3)
                    self.assertEqual('invalid', audit['integrity']['status'], audit)
                    self.assertEqual('invalid', full['status'], full)

    def test_retained_partial_references_cannot_reach_future_owned_tables(self):
        for field, value in (('span', 1), ('input', 1), ('referenceCount', 0), ('casterCount', 0)):
            with self.subTest(field=field):
                report, proof = partial_fixture(5, 'selected', index=0)
                if field == 'input':
                    later_input = copy.deepcopy(report['pointShadowAttribution']['inputs'][0])
                    later_input['rawWords'][0] = 1
                    report['pointShadowAttribution']['inputs'].append(later_input)
                    frame = report['pointShadowAttribution']['frames'][1]
                    frame['inputCount'] = 2
                    report['samples'][1]['shadowStage']['callback']['snapshot']['pointAttribution'] = frame.copy()
                if field in ('referenceCount', 'casterCount'):
                    frame = report['pointShadowAttribution']['frames'][0]
                    frame[field] = value
                    report['samples'][0]['shadowStage']['callback']['snapshot']['pointAttribution'] = frame.copy()
                else:
                    report['pointShadowAttribution']['rows'][0][field] = value
                self.assertEqual('invalid', point.audit_attribution(report, proof)['integrity']['status'])
                self.assertEqual('invalid', analyse(report, 1, 123, proof, expected_recording_slots=3)['status'])


class IdentityPartitionTests(unittest.TestCase):
    def check_change(self, first, second, labels, added, removed, keyed, signature_changed):
        report, proof = fixture(first, second)
        audit = point.audit_attribution(report, proof)
        self.assertEqual('valid', audit['integrity']['status'], audit)
        light = audit['lights'][0]
        history = light['history'][1]
        self.assertEqual(set(labels), set(history['classifications']))
        self.assertEqual(added, [c['casterId'] for c in history['added']])
        self.assertEqual(removed, [c['casterId'] for c in history['removed']])
        self.assertEqual(keyed, [c['motionKey'] for c in history['keyedChanges']])
        self.assertEqual((len(added), len(removed), len(keyed)),
                         (history['addedCount'], history['removedCount'], history['keyedChangeCount']))
        for field in ('signatureInputsChanged', 'mismatch', 'rebuilt'):
            self.assertEqual(signature_changed, history[field], field)
        self.assertEqual(None if labels == ['unchanged'] else dict(sequence=2, timeSeconds=5.0), light['firstChange'])
        for change in history['keyedChanges']:
            self.assertEqual(['transform'], change['fields'])
        full = analyse(report, 1, 123, proof, expected_recording_slots=3)
        self.assertEqual('valid', full['status'], full)
        self.assertEqual(history, full['pointAttribution']['lights'][0]['history'][1])
        return audit

    def test_unique_key_replacement_survives_identical_signature_inputs(self):
        self.check_change([caster(71)], [caster(72)], ['rigid-membership'], [1], [0], [], False)

    def test_key_appearance_and_disappearance_cannot_cancel_with_unkeyed_geometry(self):
        for before, after in ((0, 71), (71, 0)):
            with self.subTest(before=before, after=after):
                self.check_change([caster(before)], [caster(after)], ['rigid-membership', 'ambiguous-unkeyed'],
                                  [1], [0], [], False)

    def test_mixed_swap_retains_unkeyed_evidence_after_pairing_unique_keys(self):
        self.check_change([caster(71), caster(0, 1065353216)], [caster(71, 1065353216), caster(0)],
                          ['keyed-transform', 'ambiguous-unkeyed'], [3], [1], [71], True)

    def test_mixed_swap_remains_visible_when_submission_order_preserves_signature_inputs(self):
        self.check_change([caster(71), caster(0, 1065353216)], [caster(0), caster(71, 1065353216)],
                          ['keyed-transform', 'ambiguous-unkeyed'], [2], [1], [71], False)

    def test_duplicate_key_cohorts_cannot_cancel_each_others_identical_geometry(self):
        self.check_change([caster(71), caster(71), caster(72, 1065353216), caster(72, 1065353216)],
                          [caster(72), caster(72), caster(71, 1065353216), caster(71, 1065353216)],
                          ['ambiguous-duplicate-motion-key'], [2, 2, 3, 3], [0, 0, 1, 1], [], False)

    def test_duplicate_cohort_difference_preserves_exact_multiplicity(self):
        self.check_change([caster(71), caster(71), caster(71)], [caster(71), caster(71, 1), caster(71, 1)],
                          ['ambiguous-duplicate-motion-key'], [1, 1], [0, 0], [], True)

    def test_unchanged_duplicate_and_unkeyed_records_have_no_invented_identity_change(self):
        records = [caster(71), caster(71), caster(0, 1)]
        self.check_change(records, records, ['unchanged'], [], [], [], False)

    def test_unique_key_and_submission_order_swap_preserves_certain_motion(self):
        self.check_change([caster(71), caster(72, 1065353216)], [caster(72), caster(71, 1065353216)],
                          ['keyed-transform'], [], [], [71, 72], False)

    def test_pure_order_changes_have_no_membership_or_motion_evidence(self):
        for keys in ((71, 72), (0, 0), (71, 71)):
            with self.subTest(keys=keys):
                a, b = caster(keys[0]), caster(keys[1], 1065353216)
                self.check_change([a, b], [b, a], ['order-only'], [], [], [], True)

    def test_detail_budget_keeps_mixed_counts_and_shared_inventory_coverage(self):
        report, proof = fixture([caster(71), caster(0, 1065353216)], [caster(0), caster(71, 1065353216)])
        with patch.object(point, 'DEFAULT_DETAIL_LIMIT', 2):
            audit = point.audit_attribution(report, proof)
            history = audit['lights'][0]['history'][1]
            self.assertEqual({'keyed-transform', 'ambiguous-unkeyed'}, set(history['classifications']))
            self.assertEqual((1, 1, 1), (history['keyedChangeCount'], history['addedCount'], history['removedCount']))
            self.assertEqual((1, [], []), (len(history['keyedChanges']), history['added'], history['removed']))
            self.assertEqual((2, 2, 2), (audit['detailCoverage']['retained'], audit['detailCoverage']['omitted'],
                                        audit['detailCoverage']['firstLimitedSequence']))
            self.assertEqual([dict(first=1, last=2)], audit['requestInventoryCoverage']['completeSequenceRanges'])
            self.assertEqual([], audit['requestInventoryCoverage']['unknownSequenceRanges'])
            self.assertEqual((1, 1, 0), (history['rowId'], history['spanId'], history['inputId']))
            full = analyse(report, 1, 123, proof, expected_recording_slots=3)
            self.assertEqual('limited', full['status'], full)
            self.assertEqual(history, full['pointAttribution']['lights'][0]['history'][1])
