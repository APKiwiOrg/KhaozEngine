import copy
import json
import tempfile
import unittest
from pathlib import Path
from analyze import analyse

DEVICE = '5a3988cb-a9cc-4505-b608-b68d3b2dd00a'
PREFIX = '2fcbf51b-3d31-4aad-864b-6cf68aba6429'
MAIN = 'e28baa8f-5f18-45fe-b535-e90bac4df034'
EPOCH = '38888768-dfac-4308-b82b-26e96e056f2b'
MVID = '84b191cb-e09c-42f3-b095-18915ef75e92'
APP_MVID = '74883996-3504-4ac0-aae0-b5781b259851'
STAGES = ('scenePrefix pointRequests pointPreparation rigidIndexBuild slotLookup staticSignatures '
          'rigidSignatureQuery rigidSignatureFold foliageIndexBuild foliageSignatureQuery foliageSignatureFold '
          'rowSelection rowPreparation rowRecording rigidRowQuery foliageRowQuery receiverPublication '
          'cascadeFit directionalPreparation directionalSelection directionalRecording directionalCommit').split()
NAMES = ['KhaozEngine.Gpu', 'KhaozEngine.Gpu.Metal', 'KhaozEngine.Windowing', 'KhaozEngine.Render3D', 'KhaozEngine.Game.Render3D']


def fixture(nonzero=False, gap=-1, zero=False):
    def window(n):
        return dict(focused=True, minimized=False, renderSuppressed=False, width=800, height=600,
                    effectiveBaseCapHz=0, vsync=False, rendererInitializesSwapchain=True,
                    preparePhaseSplitActive=True, preparePhaseSubmissionCount=n, preparePhaseCompositeCount=n)
    def counters(n):
        return dict(hasValue=True, framesBegun=n, drainCount=0, drainMs=0.0, backpressureStallCount=0,
                    backpressureStallMs=0.0, acquireWaitCount=0, acquireWaitMs=0.0, offTimelineDeferred=0,
                    offTimelineOutstanding=0)
    def boundary(n):
        return dict(status=4, totals=None) if n == gap else dict(status=1, totals=dict(deviceIdentity=DEVICE,
                    depth=2, frequency=1000, count=(0 if zero else 2*n) + (n if nonzero else 0),
                    ticks=(0 if zero else 12*n) + (9*n if nonzero else 0)))
    def receipt(n, prefix):
        return dict(deviceIdentity=DEVICE, listIdentity=PREFIX if prefix else MAIN, attempt=n,
                    recordingOrdinal=2*n-1 if prefix else 2*n, segment=1 if prefix else 0, depth=2,
                    count=0 if zero else 1, ticks=0 if zero else (5 if prefix else 7),
                    waitCallCount=0 if zero else 1, frequency=1000)
    b = [boundary(n) for n in range(3)]
    rows = []
    for n in (1, 2):
        req = dict(epoch=EPOCH, sequence=n, enabled=True, mode=1)
        callback = dict(request=req.copy(), declarationCount=1, prefixExpected=True, acknowledgementCount=1,
                        prefixObservations=1, mainObservations=1, identityViolation=False,
                        prefix=dict(status=2, priorAttempt=n-1, receipt=receipt(n, True)),
                        main=dict(status=2, priorAttempt=n-1, receipt=receipt(n, False)))
        rec = dict(status=2, count=None, ticks=None) if gap in (n-1, n) else dict(status=1,
                   count=1 if nonzero else 0, ticks=9 if nonzero else 0)
        shadow_req = dict(epoch=EPOCH, sequence=n, mode=1)
        snapshot = dict(request=shadow_req.copy(), status=1,
                        configuration=dict(hudTiming=False,terrainCastsShadows=True,cascadeCulling=True,
                                           gpuSkinning=False,pointEnabled=True),
                        counters=dict(staticRequests=0,hasPointRequests=True,pointAtlasAvailable=True,
                                      directionalActive=True,directionalRendered=False,directionalSkipped=False,
                                      rigidIndexBuilt=False,foliageIndexBuilt=False),timings=None)
        shadow = dict(request=shadow_req.copy(), status=1, callback=dict(request=shadow_req.copy(), completed=True,
                      producerCount=1, snapshot=snapshot))
        rows.append(dict(startTimestamp=(n-1)*5000, endTimestamp=n*5000, monotonicIntervalSeconds=5.0,
                         loopDeltaSeconds=5.0, completedFrameWindow=window(n-1), boundaryWindow=window(n), counters=counters(n),
                         shadowStage=shadow, recordingWait=dict(request=req.copy(), status=1, callback=callback,
                         start=b[n-1], end=b[n], reconciliation=rec)))
    total_boundaries = 3 if gap == -1 else 2
    interval_count = 2 if gap == -1 else 0 if gap == 1 else 1
    end_valid = gap not in (0, 2)
    result = dict(schemaVersion=4, processId=123, appAssembly='Grimhollow', engineAssembly='KhaozEngine.Windowing MVID='+MVID,
                  appCodeIdentity=dict(name='Grimhollow', mvid=APP_MVID, informationalVersion='0.1.0+'+'b'*40),
                  engineCodeIdentity=dict(name='KhaozEngine.Windowing', mvid=MVID, informationalVersion='21.13.0+'+'a'*40),
                  stopwatchFrequency=1000, durationSeconds=10.0, sampleCount=2, stopReason='duration',
                  baseline=dict(timestamp=0, window=window(0), counters=counters(0), recordingWait=b[0]),
                  end=dict(timestamp=10000, window=window(2), counters=counters(2), recordingWait=b[2]), samples=rows,
                  summary=dict(framesPerSecond=.2, meanFrameMs=5000.0, medianFrameMs=5000.0, p95FrameMs=5000.0,
                  p99FrameMs=5000.0, worstFrameMs=5000.0, shareWithin144HzBudget=0.0, allForegroundRenderedStableSize=True,
                  allPreparePhaseSubmissionsPaired=True, cadenceCapped=False, bufferCapacityReached=False, capacityLimited=False,
                  counterDelta=dict(framesBegun=2, drainCount=0, drainMs=0.0, backpressureStallCount=0, backpressureStallMs=0.0,
                  acquireWaitCount=0, acquireWaitMs=0.0, offTimelineDeferred=0, offTimelineOutstandingChange=0,
                  drainCountPerInterval=0.0, drainMsPerInterval=0.0, backpressureStallCountPerInterval=0.0,
                  backpressureStallMsPerInterval=0.0, acquireWaitCountPerInterval=0.0, acquireWaitMsPerInterval=0.0)),
                  shadowStages=dict(mode=1, validRows=2, invalidRows=0, comparable=True, coverage=[dict(status=1, rows=2)],
                  timings=[dict(stage=x[0].upper()+x[1:], sampleCount=0, meanMs=None, maxMs=None) for x in STAGES], meanScenePrefixMs=None),
                  recordingWaits=dict(enabled=True, totalRows=2, validRoleRows=2, availableBoundaries=total_boundaries,
                  reconciledIntervals=interval_count, roleCoverageComplete=True, boundaryCoverageComplete=gap == -1,
                  intervalCoverageComplete=gap == -1, fullyAttributed=gap == -1,
                  prefix=dict(count=0 if zero else 2, ticks=0 if zero else 10), main=dict(count=0 if zero else 2, ticks=0 if zero else 14),
                  window=dict(status=1, count=2 if nonzero else 0, ticks=18 if nonzero else 0) if end_valid else dict(status=2, count=None, ticks=None),
                  prefixMs=0.0 if zero else 10.0, mainMs=0.0 if zero else 14.0, unattributedMs=(18.0 if nonzero else 0.0) if end_valid else None))
    proof = dict(processId=123, processBefore='123 start dotnet client/Grimhollow.dll --with-server',
                 collectionId=EPOCH,ownershipReserved=True,preflightRechecked=True,triggerConsumed=True,
                 triggerIdentity=[1,42],startedAtUtc='2026-10-27T00:00:00+00:00',
                 triggeredAtUtc='2026-10-27T00:00:01+00:00',triggerConsumedAtUtc='2026-10-27T00:00:02+00:00',
                 resultObservedAtUtc='2026-10-27T00:00:12+00:00',
                 processAfter='123 start dotnet client/Grimhollow.dll --with-server', foregroundBefore=dict(pid=123),
                 foregroundAfter=dict(pid=123), probeCommit='a'*40, gameCommit='b'*40, requestedSeconds=10,
                 triggerCreated=True, freshPaths=True, runtimeFilesRehashed=644, contentFiles=395, worldFilesRehashed=41,
                 sourceHashes=[dict(path='source.cs', sha256='c'*64)], preparationSha256='d'*64,
                 resultSha256='e'*64, requiredClosure=NAMES.copy(), mappedBefore=[x+'.dll' for x in NAMES]+['Grimhollow.dll'],
                 mappedAfter=[x+'.dll' for x in NAMES]+['Grimhollow.dll'],
                 codeIdentities=dict(app=result['appCodeIdentity'].copy(), engine=result['engineCodeIdentity'].copy()),
                 runtimeFiles=[dict(path=x+'.dll', sha256='f'*64, afterSha256='f'*64, stat=[1,2+i,3,4,5],
                 afterStat=[1,2+i,3,4,5]) for i,x in enumerate(NAMES+['Grimhollow'])])
    return result, proof


class AnalyzerTests(unittest.TestCase):
    def audit(self, report, proof):
        with tempfile.TemporaryDirectory(prefix='khaozengine-ring-wait-synthetic-', dir='/tmp') as directory:
            path = Path(directory) / 'result.json'
            path.write_text(json.dumps(report))
            return analyse(json.loads(path.read_text()), 1, 123, json.loads(json.dumps(proof)))

    def test_exact_zero_and_nonzero_remainder_are_accepted(self):
        for options in ({'zero': True}, {}, {'nonzero': True}):
            with self.subTest(options=options):
                r, p = fixture(**options)
                a = self.audit(r, p)
                self.assertEqual('valid', a['status'], a)
                self.assertTrue(a['fullyAttributed'])

    def test_interior_and_endpoint_gaps_are_explicitly_limited(self):
        for gap in (0, 1, 2):
            with self.subTest(gap=gap):
                r, p = fixture(gap=gap)
                a = self.audit(r, p)
                self.assertEqual('limited', a['status'], a)
                self.assertFalse(a['fullyAttributed'])
                self.assertEqual(gap == 1, a['windowReconciled'])
                self.assertEqual(2, a['validRoleRows'])

    def test_runtime_acceptance_requires_ownership_and_consumed_trigger_proof(self):
        for field in ('ownershipReserved','preflightRechecked','triggerConsumed'):
            with self.subTest(field=field):
                r,p=fixture();p[field]=False
                result=self.audit(r,p)
                self.assertEqual('invalid',result['status'])
                self.assertFalse(result['fullyAttributed'])

    def test_review_boolean_substitutions_are_rejected(self):
        for path,value in ((('samples',0,'shadowStage','callback','completed'),1.0),
                           (('shadowStages','comparable'),1),(('summary','capacityLimited'),0)):
            with self.subTest(path=path,value=value):
                r,p=json.loads(json.dumps(fixture()))
                self.assertEqual('valid',self.audit(r,p)['status'])
                target=r
                for key in path[:-1]:target=target[key]
                target[path[-1]]=value
                result=self.audit(r,p)
                self.assertEqual('invalid',result['status'],result)
                self.assertFalse(result['fullyAttributed'])

    def test_unavailable_public_counters_keep_false_has_value_and_null_delta(self):
        r,p=fixture()
        for counters in [r['baseline']['counters'],r['end']['counters']]+[s['counters'] for s in r['samples']]:
            counters.update(hasValue=False,framesBegun=0)
        r['summary']['counterDelta']=None
        result=self.audit(r,p)
        self.assertEqual('valid',result['status'],result)
        self.assertTrue(result['fullyAttributed'])

    def boolean_paths(self,value,path=()):
        if isinstance(value,dict):
            for key,item in value.items():yield from self.boolean_paths(item,path+(key,))
        elif isinstance(value,list):
            for key,item in enumerate(value):yield from self.boolean_paths(item,path+(key,))
        elif type(value) is bool:
            yield path,value

    def test_every_boolean_field_rejects_numeric_and_string_substitutes(self):
        for gap in (-1,1):
            originals=json.loads(json.dumps(fixture(gap=gap)))
            control=self.audit(*originals)
            self.assertEqual('valid' if gap==-1 else 'limited',control['status'],control)
            self.assertEqual(gap==-1,control['fullyAttributed'])
            for section,original in enumerate(originals):
                for path,value in self.boolean_paths(original):
                    for substitute in (int(value),float(value),str(value).lower(),str(int(value))):
                        with self.subTest(gap=gap,section=section,path=path,substitute=substitute):
                            data=json.loads(json.dumps(originals))
                            target=data[section]
                            for key in path[:-1]:target=target[key]
                            target[path[-1]]=substitute
                            result=self.audit(*data)
                            self.assertEqual('invalid',result['status'],result)
                            self.assertFalse(result['fullyAttributed'])

    def test_declared_boolean_fields_are_required_and_cannot_be_null(self):
        originals=json.loads(json.dumps(fixture()))
        for section,original in enumerate(originals):
            for path,_ in self.boolean_paths(original):
                for missing in (False,True):
                    with self.subTest(section=section,path=path,missing=missing):
                        data=json.loads(json.dumps(originals))
                        target=data[section]
                        for key in path[:-1]:target=target[key]
                        if missing:del target[path[-1]]
                        else:target[path[-1]]=None
                        result=self.audit(*data)
                        self.assertEqual('invalid',result['status'],result)
                        self.assertFalse(result['fullyAttributed'])

    def test_every_present_integer_field_rejects_float_and_bool_after_json_round_trip(self):
        def integers(value,path=()):
            if isinstance(value,dict):
                for key,item in value.items():yield from integers(item,path+(key,))
            elif isinstance(value,list):
                for key,item in enumerate(value):yield from integers(item,path+(key,))
            elif type(value) is int:
                yield path,value
        r,p=fixture()
        # Cover optional integer-bearing shadow structures as well as the minimal counts fixture.
        for sample in r['samples']:
            snapshot=sample['shadowStage']['callback']['snapshot']
            snapshot['configuration'].update(directionalMode=1,cascadeMergeGap=0,directionalResolution=1024,
                cascadeCount=4,pointFaceResolution=256,pointRows=8,transientRows=1,staticBudget=2,dynamicBudget=2,pointFilter=1)
            snapshot['counters'].update(foliageBuildOrigin=0,pointRigidIssuedInstances=0,
                rigidSignatureQueries=dict(calls=0,exactTests=0,accepted=0,sortInputs=0))
        self.assertEqual('valid',self.audit(r,p)['status'])
        originals=json.loads(json.dumps([r,p]))
        for section,original in enumerate(originals):
            for path,value in integers(original):
                for replacement in (float(value),bool(value)):
                    with self.subTest(section=section,path=path,replacement=replacement):
                        data=json.loads(json.dumps(originals))
                        target=data[section]
                        for key in path[:-1]:target=target[key]
                        target[path[-1]]=replacement
                        result=self.audit(*data)
                        self.assertEqual('invalid',result['status'],result)
                        self.assertFalse(result['fullyAttributed'])

    def test_integer_ranges_and_enum_values_cannot_escape_the_schema(self):
        for field,value in (('sampleCount',4097),('stopwatchFrequency',1<<63),('processId',1<<31)):
            r,p=fixture();r[field]=value
            result=self.audit(r,p)
            self.assertEqual('invalid',result['status'])
            self.assertFalse(result['fullyAttributed'])
        for field,value in (('priorAttempt',1<<64),('status',11),('status',-1)):
            r,p=fixture();r['samples'][0]['recordingWait']['callback']['main'][field]=value
            result=self.audit(r,p)
            self.assertEqual('invalid',result['status'])
            self.assertFalse(result['fullyAttributed'])
        for changes in ('window-int32','depth-int32','shadow-int32','shadow-int64','shadow-enum','configuration-int32','stat-uint64'):
            with self.subTest(changes=changes):
                r,p=fixture()
                if changes=='window-int32':
                    for window in [r['baseline']['window'],r['end']['window']]+[s[k] for s in r['samples'] for k in ('completedFrameWindow','boundaryWindow')]:
                        window['width']=1<<31
                elif changes=='depth-int32':
                    for boundary in [r['baseline']['recordingWait'],r['end']['recordingWait']]+[s['recordingWait'][k] for s in r['samples'] for k in ('start','end')]:
                        boundary['totals']['depth']=1<<31
                elif changes.startswith('shadow-') or changes=='configuration-int32':
                    for sample in r['samples']:
                        snapshot=sample['shadowStage']['callback']['snapshot']
                        if changes=='shadow-int32':snapshot['counters']['staticRequests']=1<<31
                        elif changes=='shadow-int64':snapshot['counters']['pointRigidIssuedInstances']=1<<63
                        elif changes=='shadow-enum':snapshot['counters']['foliageBuildOrigin']=4
                        else:snapshot['configuration']['pointRows']=1<<31
                else:
                    p['runtimeFiles'][0]['stat'][1]=p['runtimeFiles'][0]['afterStat'][1]=1<<64
                result=self.audit(r,p)
                self.assertEqual('invalid',result['status'],result)
                self.assertFalse(result['fullyAttributed'])

    def test_not_applicable_prefix_keeps_strict_status_and_prior_attempt_types(self):
        r,p=json.loads(json.dumps(fixture()))
        for window in [r['baseline']['window'],r['end']['window']]+[s[k] for s in r['samples'] for k in ('completedFrameWindow','boundaryWindow')]:
            window.update(preparePhaseSplitActive=False,preparePhaseSubmissionCount=0,preparePhaseCompositeCount=0)
        for b in [r['baseline']['recordingWait'],r['end']['recordingWait']]+[s['recordingWait'][k] for s in r['samples'] for k in ('start','end')]:
            b['totals']['count'] //= 2
            b['totals']['ticks'] = b['totals']['count']*7
        for sample in r['samples']:
            sample['recordingWait']['callback'].update(prefixExpected=False,prefixObservations=0,
                prefix=dict(status=3,priorAttempt=0,receipt=None))
        r['recordingWaits'].update(prefix=None,prefixMs=None)
        self.assertEqual('valid',self.audit(r,p)['status'])
        for field,value in (('status',3.0),('status',True),('priorAttempt',0.0),('priorAttempt',False)):
            mutated=json.loads(json.dumps(r))
            mutated['samples'][0]['recordingWait']['callback']['prefix'][field]=value
            result=self.audit(mutated,p)
            self.assertEqual('invalid',result['status'])
            self.assertFalse(result['fullyAttributed'])

    def test_timing_integer_fields_are_strict_while_durations_remain_floating_point(self):
        r,p=fixture()
        r['shadowStages']['mode']=2
        r['shadowStages']['meanScenePrefixMs']=0.0
        for summary in r['shadowStages']['timings']:
            summary.update(sampleCount=2,meanMs=0.0,maxMs=0.0)
        for sample in r['samples']:
            shadow=sample['shadowStage']
            for request in (shadow['request'],shadow['callback']['request'],shadow['callback']['snapshot']['request'],
                            sample['recordingWait']['request'],sample['recordingWait']['callback']['request']):request['mode']=2
            shadow['callback']['snapshot']['timings']=dict(frequency=1000,timestampReads=2,**{key:0 for key in STAGES})
        def audit(report):return analyse(json.loads(json.dumps(report)),2,123,json.loads(json.dumps(p)))
        self.assertEqual('valid',audit(r)['status'])
        for field in ('frequency','timestampReads',*STAGES):
            for replacement in (float(r['samples'][0]['shadowStage']['callback']['snapshot']['timings'][field]),True,1<<63):
                with self.subTest(field=field,replacement=replacement):
                    mutated=json.loads(json.dumps(r))
                    mutated['samples'][0]['shadowStage']['callback']['snapshot']['timings'][field]=replacement
                    result=audit(mutated)
                    self.assertEqual('invalid',result['status'],result)
                    self.assertFalse(result['fullyAttributed'])

    def test_code_mvid_comparison_uses_guid_identity_not_hex_letter_case(self):
        r,p=fixture()
        p['codeIdentities']['app']['mvid']=p['codeIdentities']['app']['mvid'].upper()
        p['codeIdentities']['engine']['mvid']=p['codeIdentities']['engine']['mvid'].upper()
        self.assertEqual('valid',self.audit(r,p)['status'])

    def test_large_boundary_integers_are_subtracted_without_float_rounding(self):
        r,p=fixture()
        seen=set()
        boundaries=[r['baseline']['recordingWait'],r['end']['recordingWait']]
        boundaries += [row['recordingWait'][key] for row in r['samples'] for key in ('start','end')]
        for value in boundaries:
            if id(value) in seen: continue
            seen.add(id(value))
            value['totals']['count'] += 9007199254740993
            value['totals']['ticks'] += 9007199254740993
        a=self.audit(r,p)
        self.assertEqual('valid',a['status'],a)
        self.assertEqual({'status':1,'count':0,'ticks':0},a['windowRemainder'])

    def test_invalid_audit_never_advertises_complete_runtime_attribution(self):
        for corrupt in ('identity','summary'):
            with self.subTest(corrupt=corrupt):
                r,p=fixture()
                if corrupt=='identity': p['runtimeFiles'][0]['afterSha256']='0'*64
                else: r['recordingWaits']['main']['ticks']=15
                result=self.audit(r,p)
                self.assertEqual('invalid',result['status'])
                self.assertFalse(result['fullyAttributed'])

    def test_schema_unknown_or_three_is_rejected(self):
        for schema in (None, 3, 5):
            r, p = fixture(); r['schemaVersion'] = schema
            self.assertEqual('invalid', self.audit(r, p)['status'])

    def test_corrupt_rows_and_arithmetic_are_rejected(self):
        for corrupt in ('missing-role', 'duplicate-epoch', 'duplicate-sequence', 'duplicate-attempt', 'ticks',
                        'device', 'depth', 'frequency', 'negative-remainder', 'bad-summary', 'bad-coverage',
                        'duplicate-ack', 'stale-attempt', 'interval-contiguity', 'boundary-alias', 'role-status',
                        'disabled', 'mode', 'sample-count', 'schema-field', 'integer-bool', 'summary-float',
                        'row-status-bool', 'request-sequence-bool', 'shadow-mode-bool', 'summary-remainder-float'):
            with self.subTest(corrupt=corrupt):
                r, p = fixture(); row = r['samples'][1]['recordingWait']; c = row['callback']; m = c['main']['receipt']
                if corrupt == 'missing-role': c['main'] = None
                elif corrupt == 'duplicate-epoch': row['request']['epoch'] = '00000000-0000-0000-0000-000000000000'
                elif corrupt == 'duplicate-sequence': row['request']['sequence'] = 1
                elif corrupt == 'duplicate-attempt': m['attempt'] = 1; c['main']['priorAttempt'] = 0
                elif corrupt == 'ticks': m['ticks'] += 1
                elif corrupt == 'device': m['deviceIdentity'] = PREFIX
                elif corrupt == 'depth': m['depth'] = 3
                elif corrupt == 'frequency': m['frequency'] = 999
                elif corrupt == 'negative-remainder': row['end']['totals']['ticks'] = 23
                elif corrupt == 'bad-summary': r['recordingWaits']['main']['ticks'] = 15
                elif corrupt == 'bad-coverage': r['recordingWaits']['availableBoundaries'] = 2
                elif corrupt == 'duplicate-ack': c['acknowledgementCount'] = 2
                elif corrupt == 'stale-attempt': m['attempt'] = 0
                elif corrupt == 'interval-contiguity': r['samples'][1]['startTimestamp'] = 4999
                elif corrupt == 'boundary-alias': row['start'] = copy.deepcopy(row['start']); row['start']['totals']['ticks'] += 1
                elif corrupt == 'role-status': row['status'] = 0
                elif corrupt == 'disabled': r['recordingWaits']['enabled'] = False
                elif corrupt == 'mode': r['shadowStages']['mode'] = 2
                elif corrupt == 'sample-count': r['sampleCount'] = 3
                elif corrupt == 'schema-field': del r['samples'][0]['recordingWait']
                elif corrupt == 'integer-bool': m['count'] = True
                elif corrupt == 'summary-float': r['recordingWaits']['main']['count'] = 2.0
                elif corrupt == 'row-status-bool': row['status'] = True
                elif corrupt == 'request-sequence-bool':
                    r['samples'][0]['recordingWait']['request']['sequence'] = True
                    r['samples'][0]['recordingWait']['callback']['request']['sequence'] = True
                elif corrupt == 'shadow-mode-bool': r['shadowStages']['mode'] = True
                elif corrupt == 'summary-remainder-float': r['recordingWaits']['window']['count'] = 0.0
                self.assertEqual('invalid', self.audit(r, p)['status'])

    def test_capacity_and_early_or_late_duration_cannot_claim_completion(self):
        for field, value in (('stopReason','capacity'), ('durationSeconds',9.99), ('durationSeconds',11)):
            r,p=fixture(); r[field]=value
            self.assertEqual('invalid', self.audit(r,p)['status'])
        r,p=fixture();r['samples'][1]['startTimestamp']=10000
        self.assertEqual('invalid', self.audit(r,p)['status'])

    def test_complete_runtime_acceptance_requires_fresh_identity_evidence(self):
        r,p=fixture()
        self.assertEqual('limited', self.audit(r,None)['status'])
        for corrupt in ('command','mapped','hash','game','engine','closure','source','focus','fresh','world'):
            with self.subTest(corrupt=corrupt):
                r,p=fixture()
                if corrupt=='command':p['processAfter']+=' changed'
                elif corrupt=='mapped':p['mappedAfter'].remove('KhaozEngine.Gpu.dll')
                elif corrupt=='hash':p['runtimeFiles'][0]['afterSha256']='0'*64
                elif corrupt=='game':p['codeIdentities']['app']['mvid']=MVID
                elif corrupt=='engine':p['probeCommit']='0'*40
                elif corrupt=='closure':p['requiredClosure'].pop()
                elif corrupt=='source':p['sourceHashes']=[]
                elif corrupt=='focus':p['foregroundAfter']['pid']=456
                elif corrupt=='fresh':p['freshPaths']=False
                elif corrupt=='world':p['worldFilesRehashed']=40
                self.assertEqual('invalid', self.audit(r,p)['status'])


if __name__ == '__main__':
    unittest.main()
