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
        snapshot = dict(request=shadow_req.copy(), status=1, configuration={}, counters=dict(staticRequests=0), timings=None)
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
            return analyse(json.loads(path.read_text()), 1, 123, proof)

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
