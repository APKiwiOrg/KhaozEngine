import copy
import contextlib
import json
import sys
import unittest
from unittest.mock import patch
from analyze import analyse, CONFIG_INTS, SHADOW_INTS, SHADOW_LONGS, SHADOW_QUERIES
import capacity
import collect
import inputs
from test_capacity import capacity_fixture, capacity_workflow
from test_collect import preparation


def contract():
    return dict(purpose='paused-sun-input-stability', schemaVersion=5, framebufferWidth=1591,
                framebufferHeight=1027, recordingSlots=3, baseFrames=3, drawables=3, retirementCap=8,
                mode=1, vsync=True, softwareCapHz=0, preparePhaseSplit=True,
                rendererInitializesSwapchain=True, engineBase='2599bd8a94ce43fc53de62f2544efb89e36d05a8')


def observation(request, index):
    matrix = [1065353216 if i in (0, 5, 10, 15) else 0 for i in range(16)]
    view = dict(viewWords=matrix[:], projectionWords=matrix[:], viewProjectionWords=matrix[:],
                absoluteViewProjectionWords=matrix[:], renderOriginWords=[0, 0, 0],
                jitterPixelsWords=[index, 0], width=1591, height=1027)
    decision = dict(active=True, rendered=index == 0, skipped=index != 0, hadPrevious=index != 0,
                    anySkinnedCaster=False, skinnedCastersCleared=False, resolutionChanged=False,
                    lightMatrixChanged=False, casterDataChanged=False, skinnedCasterCount=0, cascadeCount=1)
    return dict(request=request.copy(), sceneFrameIndex=100 + index, prefixCount=1, viewCount=1,
                cascadeObservationCount=1, cameraCount=1, lightCount=1, decisionCount=1, availability=1,
                complete=True, matrixOrder='row-major-M11-M44', prefixEffectTimeWords=[1065353216 + index], view=view,
                camera=dict(eyeWords=[0, 0, 0], forwardWords=[0, 0, 1065353216]),
                light=dict(rawWords=[0, 3212836864, 0], normalizedWords=[0, 3212836864, 0],
                           selectedWords=[0, 3212836864, 0]),
                fits=dict(count=1, resolution=64, splitWords=[1065353216, 0, 0, 0],
                          absoluteFitWords=[matrix[:], [0]*16, [0]*16, [0]*16]), decision=decision)


def stability_fixture():
    report, proof = json.loads(json.dumps(capacity_fixture()))
    report['schemaVersion'] = 5
    proof.update(purpose='paused-sun-input-stability', inputContract=contract(),
                 frozenEngineBase=contract()['engineBase'])
    proof['helperHashes']['inputs.py'] = 'c'*64
    proof['sourceHashes'].append(dict(path='scripts/mac-frame-ring-waits/inputs.py', sha256='c'*64))
    for endpoint in (report['baseline'], report['end']):
        endpoint['window'].update(width=1591, height=1027)
    for index, sample in enumerate(report['samples']):
        for name in ('completedFrameWindow', 'boundaryWindow'):
            sample[name].update(width=1591, height=1027)
        snapshot = sample['shadowStage']['callback']['snapshot']
        for name in CONFIG_INTS.split():
            snapshot['configuration'].setdefault(name, 0)
        for name in (SHADOW_INTS + ' ' + SHADOW_LONGS).split():
            snapshot['counters'].setdefault(name, 0)
        snapshot['counters']['foliageBuildOrigin'] = 0
        for name in SHADOW_QUERIES:
            snapshot['counters'][name] = dict(calls=0, exactTests=0, accepted=0, sortInputs=0)
        snapshot['inputs'] = observation(snapshot['request'], index)
        snapshot['counters'].update(directionalCascadeCount=1, directionalRendered=index == 0,
                                    directionalSkipped=index != 0)
    return report, proof


@contextlib.contextmanager
def stability_workflow(*, cli_purpose=True):
    with capacity_workflow() as workflow:
        report, _ = stability_fixture()
        workflow.report.clear()
        workflow.report.update(report)
        workflow.manifest.update(purpose=inputs.PURPOSE, inputContract=contract(),
                                 frozenEngineBase=inputs.ENGINE_BASE)
        (workflow.result.parent / 'preparation.json').write_text(json.dumps(workflow.manifest))
        arguments = sys.argv + (['--purpose', inputs.PURPOSE] if cli_purpose else [])
        with patch.object(sys, 'argv', arguments):
            yield workflow


class InputTests(unittest.TestCase):
    def audit(self, report):
        return inputs.audit_observations(report, contract())

    def test_stable_controlled_inputs_keep_expected_dynamic_and_all_work(self):
        r, p = stability_fixture()
        result = self.audit(r)
        self.assertEqual('valid', result['integrity']['status'], result)
        self.assertEqual('stable', result['controlledInputs']['status'])
        self.assertEqual('not established', result['fullWorkloadEquivalence'])
        self.assertEqual(r['sampleCount'], result['dynamicObservations']['sceneFrameIndex']['coverage'])
        self.assertGreater(result['dynamicObservations']['effectTime']['distinctStates'], 1)
        work = result['workObservations']['directionalRendered']
        self.assertEqual((True, False, 1, 2), (work['first'], work['last'], work['transitions'], work['coverage']))
        self.assertEqual('valid', analyse(r, 1, 123, p, expected_recording_slots=3)['status'])

    def test_each_controlled_word_change_is_precise_and_retained(self):
        groups = [('camera', 'view', 'viewWords', 16), ('camera', 'view', 'projectionWords', 16),
                  ('camera', 'view', 'viewProjectionWords', 16), ('camera', 'view', 'absoluteViewProjectionWords', 16),
                  ('camera', 'view', 'renderOriginWords', 3), ('cascadeCamera', 'camera', 'eyeWords', 3),
                  ('cascadeCamera', 'camera', 'forwardWords', 3), ('light', 'light', 'rawWords', 3),
                  ('light', 'light', 'normalizedWords', 3), ('light', 'light', 'selectedWords', 3),
                  ('cascades', 'fits', 'splitWords', 1)]
        for group, section, field, length in groups:
            for element in range(length):
                r, _ = stability_fixture()
                r['samples'][1]['shadowStage']['callback']['snapshot']['inputs'][section][field][element] ^= 1
                result = self.audit(r)
                with self.subTest(field=field, element=element):
                    self.assertEqual('valid', result['integrity']['status'], result)
                    value = result['controlledInputs']['groups'][group]
                    self.assertEqual('unstable', value['status'])
                    self.assertEqual(2, value['firstChange']['sample'])
                    self.assertIn(field + '[' + str(element) + ']', value['firstChange']['field'])
                    self.assertEqual(2, value['distinctStates'])
        for element in range(16):
            r, _ = stability_fixture()
            r['samples'][1]['shadowStage']['callback']['snapshot']['inputs']['fits']['absoluteFitWords'][0][element] ^= 1
            self.assertEqual('unstable', self.audit(r)['controlledInputs']['groups']['cascades']['status'])

    def test_nonfinite_raw_words_are_retained_with_exact_unusable_path(self):
        r, _ = stability_fixture()
        row = r['samples'][0]['shadowStage']['callback']['snapshot']['inputs']
        row['view']['projectionWords'][7] = 0x7fc01234
        result = self.audit(r)
        self.assertEqual('valid', result['integrity']['status'])
        self.assertEqual('unusable', result['controlledInputs']['status'])
        self.assertIn('view.projectionWords[7]', str(result['unusableFields']))
        self.assertEqual(0x7fc01234, row['view']['projectionWords'][7])

    def test_unavailable_groups_are_not_stable(self):
        r, _ = stability_fixture()
        for sample in r['samples']:
            snapshot = sample['shadowStage']['callback']['snapshot']
            row = snapshot['inputs']
            snapshot['counters'].update(directionalActive=False, directionalCascadeCount=0,
                                        directionalRendered=False, directionalSkipped=False)
            row.update(availability=2, cameraCount=0, lightCount=0)
            row['camera'] = dict(eyeWords=[0]*3, forwardWords=[0]*3)
            row['light'] = dict(rawWords=[0]*3, normalizedWords=[0]*3, selectedWords=[0]*3)
            row['fits'] = dict(count=0, resolution=0, splitWords=[0]*4, absoluteFitWords=[[0]*16 for _ in range(4)])
            row['decision'] = {k: False if type(v) is bool else 0 for k, v in row['decision'].items()}
        result = self.audit(r)
        self.assertEqual('valid', result['integrity']['status'])
        self.assertEqual('unavailable', result['controlledInputs']['groups']['cascades']['status'])

    def test_strict_ownership_lengths_counts_types_and_canonical_unused_slots(self):
        corruptions = [lambda x: x.update(prefixCount=2), lambda x: x.update(viewCount=0),
                       lambda x: x.update(sceneFrameIndex=True), lambda x: x.update(complete=1),
                       lambda x: x.update(availability=1.0), lambda x: x['request'].update(sequence=99),
                       lambda x: x['view'].update(viewWords=[0]), lambda x: x['fits'].update(count=5),
                       lambda x: x['fits'].update(count=True), lambda x: x['fits']['splitWords'].__setitem__(1, 1),
                       lambda x: x['fits']['absoluteFitWords'][1].__setitem__(3, 0x80000000),
                       lambda x: x['view']['viewWords'].__setitem__(0, 4294967296),
                       lambda x: x['view']['viewWords'].__setitem__(0, -1),
                       lambda x: x['view']['viewWords'].__setitem__(0, True),
                       lambda x: x['view']['viewWords'].__setitem__(0, 1.0),
                       lambda x: x['decision'].update(rendered=1)]
        for corrupt in corruptions:
            r, _ = stability_fixture()
            corrupt(r['samples'][0]['shadowStage']['callback']['snapshot']['inputs'])
            self.assertEqual('invalid', self.audit(r)['integrity']['status'])

    def test_scene_index_must_progress_and_schema_four_is_explicitly_legacy(self):
        r, _ = stability_fixture()
        r['samples'][1]['shadowStage']['callback']['snapshot']['inputs']['sceneFrameIndex'] = 100
        self.assertEqual('invalid', self.audit(r)['integrity']['status'])
        r, p = capacity_fixture()
        self.assertEqual('invalid', self.audit(r)['integrity']['status'])
        p.update(purpose='paused-sun-input-stability', inputContract=contract())
        self.assertEqual('invalid', analyse(r, 1, 123, p, expected_recording_slots=3)['status'])

    def test_missing_work_or_configuration_fields_cannot_hide_variation(self):
        for group, key in [('counters', 'directionalSelectedInstances'), ('configuration', 'cascadeCount')]:
            r, _ = stability_fixture()
            del r['samples'][0]['shadowStage']['callback']['snapshot'][group][key]
            self.assertEqual('invalid', self.audit(r)['integrity']['status'])


class InputCollectorTests(unittest.TestCase):
    def test_stability_collection_archives_purpose_inputs_and_every_helper_in_the_same_command(self):
        import archive
        with stability_workflow() as workflow:
            status = collect.main()
            self.assertEqual(0, status, workflow.metadata.read_text())
            proof = json.loads(workflow.metadata.read_text())
            self.assertEqual(contract(), proof['inputContract'])
            audit = json.loads(workflow.result.with_suffix('.audit.json').read_text())
            self.assertEqual('valid', audit['status'], audit)
            self.assertEqual('not established', audit['inputStability']['fullWorkloadEquivalence'])
            identity = json.loads(workflow.result.with_suffix('.identity.json').read_text())
            self.assertEqual(inputs.PURPOSE, identity['purpose'])
            self.assertEqual(set(inputs.HELPER_FILES), set(identity['helperHashes']))
            archive.verify_bundle(workflow.archiveDestination)

    def test_stability_preparation_cannot_enter_the_legacy_collection_entry_point(self):
        with stability_workflow(cli_purpose=False) as workflow:
            self.assertEqual(1, collect.main())
            self.assertEqual([], workflow.triggers)
            self.assertIn('purpose', json.loads(workflow.metadata.read_text())['error'])
            self.assertTrue(workflow.result.with_suffix('.archive-receipt.json').is_file())

    def test_preparation_purpose_and_treatment_cannot_be_substituted(self):
        for key, bad in [('framebufferWidth', 1668), ('framebufferHeight', 1029), ('recordingSlots', 6),
                         ('schemaVersion', 4), ('baseFrames', True), ('drawables', 3.0), ('mode', 2),
                         ('vsync', 1), ('purpose', 'capacity')]:
            c = contract(); c[key] = bad
            with self.subTest(key=key), self.assertRaises(ValueError): inputs.validate_contract(c)
        manifest = preparation(); manifest.update(purpose='paused-sun-input-stability', inputContract=contract())
        manifest['sourceHashes'].extend(dict(path='scripts/mac-frame-ring-waits/'+name, sha256='c'*64) for name in ('analyze.py', 'collect.py', 'capacity.py', 'archive.py', 'inputs.py'))
        collect.validate_preparation(manifest)
        manifest['inputContract']['framebufferWidth'] = 1668
        with self.assertRaises(ValueError): collect.validate_preparation(manifest)

    def test_new_helper_identity_and_old_comparison_rejection(self):
        r, p = stability_fixture()
        self.assertEqual('invalid', capacity.audit_treatment(r, p, 3)['status'])
        comparison = capacity.compare_runs(r, p, r, p)
        self.assertFalse(comparison['comparable'])
        self.assertIsNone(comparison['observedFramesPerSecondDifference'])
        p['helperHashes'].pop('inputs.py')
        self.assertEqual('invalid', analyse(r, 1, 123, p, expected_recording_slots=3)['status'])
        r, p = stability_fixture(); p['inputContract']['framebufferWidth'] = 1668
        self.assertEqual('invalid', analyse(r, 1, 123, p, expected_recording_slots=3)['status'])


if __name__ == '__main__': unittest.main()
