import base64
import copy
import hashlib
import json
import unittest
from analyze import analyse
from test_analyze import fixture, DEVICE, MVID
import capacity


def capacity_fixture(slots=3, **options):
    report, proof = fixture(**options)
    for endpoint in (report['baseline'], report['end']):
        endpoint['window'].update(width=1668, height=1029, vsync=True)
        totals = endpoint['recordingWait']['totals']
        if totals: totals['depth'] = slots
    for sample in report['samples']:
        for field in ('completedFrameWindow', 'boundaryWindow'):
            sample[field].update(width=1668, height=1029, vsync=True)
        row = sample['recordingWait']
        for field in ('start', 'end'):
            if row[field]['totals']: row[field]['totals']['depth'] = slots
        for role in ('prefix', 'main'):
            receipt=row['callback'][role]['receipt'];receipt['depth']=slots
            receipt['segment']=receipt['recordingOrdinal']%slots
    report['summary']['cadenceCapped'] = True
    report['appCodeIdentity']['informationalVersion'] = '0.1.0+' + capacity.GAME_BASE
    proof['gameCommit'] = capacity.GAME_BASE
    proof['codeIdentities']['app'] = report['appCodeIdentity'].copy()
    policy = dict(schemaVersion=1, probe='metal-recording-capacity', baseFrames=3,
                  selectedRecordingSlots=slots, actualRecordingSlots=slots, requestedDrawableCount=3,
                  actualDrawableCount=3, drawableReadback='available', retirementPolicyCap=8,
                  uncommittedDiagnosticBound=slots+1, deviceIdentity=DEVICE, deviceName='Synthetic Metal',
                  assemblyName='KhaozEngine.Gpu.Metal', assemblyVersion='21.13.0.0', assemblyMvid=MVID,
                  assemblyInformationalVersion='21.13.0+'+proof['probeCommit'])
    raw = ('[startup] ' + capacity.MARKER + json.dumps(policy) + '\n').encode()
    proof.update(expectedRecordingSlots=slots, frozenEngineBase=capacity.ENGINE_BASE,
                 startupProof=dict(lineBase64=base64.b64encode(raw).decode(), lineSha256=hashlib.sha256(raw).hexdigest()),
                 metalAssembly=dict(name=policy['assemblyName'], mvid=MVID, assemblyVersion='21.13.0.0',
                                    informationalVersion=policy['assemblyInformationalVersion'], sha256='f'*64))
    return report, proof


def change_policy(proof, key, value):
    raw = base64.b64decode(proof['startupProof']['lineBase64'])
    policy = json.loads(raw.split(capacity.MARKER.encode(), 1)[1])
    if value is None: policy.pop(key)
    else: policy[key] = value
    raw = capacity.MARKER.encode() + json.dumps(policy).encode() + b'\n'
    proof['startupProof'] = dict(lineBase64=base64.b64encode(raw).decode(), lineSha256=hashlib.sha256(raw).hexdigest())


class CapacityTests(unittest.TestCase):
    def test_only_exact_integer_expectations(self):
        for value in (3, 6): self.assertEqual(value, capacity.validate_expectation(value))
        for value in (True, False, 3.0, 6.0, '3', None, 4, [], {}):
            with self.subTest(value=value), self.assertRaises(ValueError): capacity.validate_expectation(value)

    def test_both_treatments_are_observed_not_requested(self):
        for slots in (3, 6):
            r, p = capacity_fixture(slots)
            a = analyse(r, 1, 123, p, expected_recording_slots=slots)
            self.assertEqual('valid', a['status'], a)
            self.assertEqual(slots, a['capacity']['policy']['actualRecordingSlots'])
            self.assertEqual('invalid', analyse(r, 1, 123, p, expected_recording_slots=9-slots)['status'])
            self.assertEqual('invalid', analyse(r, 1, 123, expected_recording_slots=slots)['status'])

    def test_every_available_boundary_and_receipt_is_checked(self):
        paths = [('baseline','recordingWait','totals','depth'), ('end','recordingWait','totals','depth'),
                 ('samples',0,'recordingWait','end','totals','depth'),
                 ('samples',1,'recordingWait','callback','main','receipt','depth'),
                 ('samples',0,'recordingWait','callback','prefix','receipt','depth')]
        for slots in (3, 6):
            for path in paths:
                for bad in (9-slots, float(slots), True):
                    r, p = copy.deepcopy(capacity_fixture(slots))
                    # JSON roundtrip avoids shared endpoint aliases in the synthetic fixture.
                    r = json.loads(json.dumps(r)); target = r
                    for field in path[:-1]: target = target[field]
                    target[path[-1]] = bad
                    self.assertEqual('invalid', capacity.audit_treatment(r,p,slots)['status'], (path,bad))

    def test_startup_policy_identity_inventory_and_exact_types(self):
        corruptions = [('actualRecordingSlots',3), ('actualDrawableCount',6), ('retirementPolicyCap',11),
                       ('uncommittedDiagnosticBound',4), ('baseFrames',6), ('selectedRecordingSlots',3),
                       ('assemblyMvid','00000000-0000-0000-0000-000000000000'),
                       ('assemblyInformationalVersion','21.13.0+'+'b'*40), ('deviceIdentity',MVID),
                       ('actualDrawableCount',None), ('schemaVersion',True), ('actualRecordingSlots',6.0)]
        for key, value in corruptions:
            r,p=capacity_fixture(6);change_policy(p,key,value)
            self.assertEqual('invalid', capacity.audit_treatment(r,p,6)['status'],(key,value))
        r,p=capacity_fixture();p['expectedRecordingSlots']=3.0
        self.assertEqual('invalid', capacity.audit_treatment(r,p,3)['status'])
        r,p=capacity_fixture();p['startupProof']['lineSha256']='0'*64
        self.assertEqual('invalid', capacity.audit_treatment(r,p,3)['status'])

    def test_selected_line_is_exact_and_duplicate_or_extra_fields_are_refused(self):
        r,p=capacity_fixture()
        raw=base64.b64decode(p['startupProof']['lineBase64'])
        self.assertEqual(p['startupProof'],capacity.select_startup(b'irrelevant\n'+raw+b'other\n'))
        for bad in (b'irrelevant', raw+raw, raw.replace(b'"baseFrames": 3',b'"baseFrames": 3, "baseFrames": 3'),
                    raw.replace(b'"baseFrames": 3',b'"baseFrames": 3, "unknown": 3')):
            with self.subTest(raw=bad),self.assertRaises(ValueError):capacity.select_startup(bad)

    def test_absent_observation_stays_limited(self):
        r,p=capacity_fixture(gap=1)
        result=analyse(r,1,123,p,expected_recording_slots=3)
        self.assertEqual('limited',result['status'],result)
        self.assertFalse(result['fullyAttributed'])

    def test_fixed_context_is_checked_from_actual_fields(self):
        for field,bad in [('width',800),('height',600),('focused',False),('vsync',False),
                          ('effectiveBaseCapHz',144),('preparePhaseSplitActive',False)]:
            r,p=capacity_fixture();r['samples'][1]['completedFrameWindow'][field]=bad
            self.assertEqual('invalid',capacity.audit_treatment(r,p,3)['status'],field)

    def test_comparison_reports_both_exact_work_distributions_and_one_observed_difference(self):
        a,pa=capacity_fixture(3);b,pb=capacity_fixture(6)
        result=capacity.compare_runs(a,pa,b,pb)
        self.assertTrue(result['comparable'],result)
        self.assertEqual(0,result['observedFramesPerSecondDifference'])
        self.assertEqual(3,result['control']['capacity']['policy']['actualRecordingSlots'])
        self.assertEqual(6,result['candidate']['capacity']['policy']['actualRecordingSlots'])
        self.assertIn('staticRequests',result['control']['workDistributions'])
        for word in ('speedup','overhead','physicalPresentation'):
            self.assertNotIn(word,result)

    def test_pair_differences_or_limited_inputs_never_interpret_an_fps_effect(self):
        for corruption in ('work','framebuffer','focus','cap','dll','source','reverse','limited','summary','startup'):
            a,pa=capacity_fixture(3);b,pb=capacity_fixture(6)
            if corruption=='work':b['samples'][0]['shadowStage']['callback']['snapshot']['counters']['staticRequests']=1
            elif corruption=='framebuffer':b['baseline']['window']['width']=900
            elif corruption=='focus':b['samples'][0]['boundaryWindow']['focused']=False
            elif corruption=='cap':b['baseline']['window']['effectiveBaseCapHz']=144
            elif corruption=='dll':pb['runtimeFiles'][0].update(sha256='1'*64,afterSha256='1'*64)
            elif corruption=='source':pb['sourceHashes'][0]['sha256']='1'*64
            elif corruption=='reverse':a,pa,b,pb=b,pb,a,pa
            elif corruption=='limited':b,pb=capacity_fixture(6,gap=1)
            elif corruption=='startup':change_policy(pb,'baseFrames',None)
            else:b={'oldSummary':b['summary']}
            result=capacity.compare_runs(a,pa,b,pb)
            self.assertFalse(result['comparable'],corruption)
            self.assertIsNone(result['observedFramesPerSecondDifference'],corruption)
            self.assertTrue(result['differences'],corruption)


if __name__=='__main__': unittest.main()

class CapacityCollectorTests(unittest.TestCase):
    def test_capacity_collection_selects_only_owned_startup_line_and_checks_actual_treatment(self):
        import sys
        from unittest.mock import patch
        import collect
        import test_collect
        for slots in (3,6):
            with test_collect.CollectorWorkflowTests().workflow() as w:
                r,p=capacity_fixture(slots)
                w.report.clear();w.report.update(r)
                w.manifest['gameCommit']=capacity.GAME_BASE
                w.manifest['appCodeIdentity']=r['appCodeIdentity']
                w.manifest['frozenEngineBase']=capacity.ENGINE_BASE
                metal=next(x for x in w.manifest['replacements'] if x['name']=='KhaozEngine.Gpu.Metal')
                metal['sha256']=next(x for x in w.manifest['clientFiles'] if x['path']=='KhaozEngine.Gpu.Metal.dll')['sha256']
                preparation=w.result.parent/'preparation.json';preparation.write_text(json.dumps(w.manifest))
                log=w.result.parent/'process.log'
                line=base64.b64decode(p['startupProof']['lineBase64'])
                log.write_bytes(b'private unrelated log line\n'+line+b'other\n')
                args=sys.argv+['--expected-recording-slots',str(slots),'--startup-log',str(log)]
                with patch.object(sys,'argv',args):self.assertEqual(0,collect.main())
                proof=json.loads(w.metadata.read_text())
                self.assertEqual(slots,proof['expectedRecordingSlots'])
                self.assertEqual(p['startupProof'],proof['startupProof'])
                self.assertNotIn('private unrelated',json.dumps(proof))
                self.assertEqual('valid',analyse(w.report,1,123,proof,expected_recording_slots=slots)['status'])

    def test_wrong_startup_treatment_is_refused_before_any_trigger(self):
        import sys
        from unittest.mock import patch
        import collect
        import test_collect
        with test_collect.CollectorWorkflowTests().workflow() as w:
            _,p=capacity_fixture(6)
            w.manifest['frozenEngineBase']=capacity.ENGINE_BASE
            w.manifest['gameCommit']=capacity.GAME_BASE
            w.manifest['appCodeIdentity']['informationalVersion']='0.1.0+'+capacity.GAME_BASE
            preparation=w.result.parent/'preparation.json';preparation.write_text(json.dumps(w.manifest))
            log=w.result.parent/'process.log';log.write_bytes(base64.b64decode(p['startupProof']['lineBase64']))
            with patch.object(sys,'argv',sys.argv+['--expected-recording-slots','3','--startup-log',str(log)]):
                self.assertEqual(1,collect.main())
            self.assertEqual([],w.triggers)
            self.assertFalse(w.trigger.exists())
