"""Private recording-capacity experiment audit, independent of process control."""
import base64
from collections import Counter
from fractions import Fraction
import hashlib
import json
import re

ENGINE_BASE = '7ae1e83d18a0cafe4d3b079b0dc3a399de390b4f'
GAME_BASE = 'f45462a87441c18682f7c2309ef2f048c4abebb0'
MARKER = 'KE_METAL_RECORDING_CAPACITY_V1 '
POLICY_FIELDS = set(('schemaVersion probe baseFrames selectedRecordingSlots actualRecordingSlots '
                     'requestedDrawableCount actualDrawableCount drawableReadback retirementPolicyCap '
                     'uncommittedDiagnosticBound deviceIdentity deviceName assemblyName assemblyVersion '
                     'assemblyMvid assemblyInformationalVersion').split())
WINDOW_FIELDS = ('width','height','focused','minimized','renderSuppressed','effectiveBaseCapHz','vsync',
                 'rendererInitializesSwapchain','preparePhaseSplitActive')


def validate_expectation(value):
    if type(value) is not int or value not in (3,6):
        raise ValueError('Expected recording slots must be the exact integer 3 or 6')
    return value


def _unique(pairs):
    result = {}
    for key,value in pairs:
        if key in result: raise ValueError('Duplicate startup field '+key)
        result[key] = value
    return result


def _policy(line):
    if len(line) > 8192 or line.count(MARKER.encode()) != 1 or len(line.splitlines()) != 1:
        raise ValueError('Require one bounded startup proof line')
    value = json.loads(line.split(MARKER.encode(),1)[1], object_pairs_hook=_unique)
    if type(value) is not dict or set(value) != POLICY_FIELDS:
        raise ValueError('Startup proof field inventory mismatch')
    return value


def select_startup(raw):
    lines = [line for line in raw.splitlines(keepends=True) if MARKER.encode() in line]
    if len(lines) != 1: raise ValueError('Require exactly one recording-capacity startup proof')
    _policy(lines[0])
    return dict(lineBase64=base64.b64encode(lines[0]).decode('ascii'),
                lineSha256=hashlib.sha256(lines[0]).hexdigest())


def decode_startup(proof):
    if set(proof) != {'lineBase64','lineSha256'}: raise ValueError('Startup byte proof inventory mismatch')
    raw = base64.b64decode(proof['lineBase64'],validate=True)
    if hashlib.sha256(raw).hexdigest() != proof['lineSha256']: raise ValueError('Startup bytes/hash mismatch')
    return raw, _policy(raw)


def validate_startup(selected, metal, expected_slots, probe_commit):
    from analyze import guid, integer
    slots=validate_expectation(expected_slots)
    _,policy=decode_startup(selected)
    for key,value in [('schemaVersion',1),('baseFrames',3),('selectedRecordingSlots',slots),
                      ('actualRecordingSlots',slots),('requestedDrawableCount',3),('actualDrawableCount',3),
                      ('retirementPolicyCap',8),('uncommittedDiagnosticBound',slots+1)]:
        if integer(policy[key])!=value: raise ValueError('Startup '+key+' differs')
    if policy['probe']!='metal-recording-capacity' or policy['drawableReadback']!='available':
        raise ValueError('Windowed capacity startup proof unavailable')
    if not isinstance(policy['deviceName'],str) or not policy['deviceName']:
        raise ValueError('Startup native device missing')
    guid(policy['deviceIdentity'])
    if (policy['assemblyName']!=metal['name'] or metal['name']!='KhaozEngine.Gpu.Metal'
        or guid(policy['assemblyMvid'])!=guid(metal['mvid'])
        or policy['assemblyVersion']!=metal['assemblyVersion'] or metal['assemblyVersion']!='21.13.0.0'
        or policy['assemblyInformationalVersion']!=metal['informationalVersion']
        or metal['informationalVersion']!='21.13.0+'+probe_commit):
        raise ValueError('Startup Metal committed-source identity differs')
    return policy


def audit_treatment(report, proof, expected_slots):
    from analyze import guid, integer
    errors = []; policy = None
    def check(condition,message):
        if not condition: errors.append(message)
    try:
        slots = validate_expectation(expected_slots)
        check(validate_expectation(proof['expectedRecordingSlots']) == slots,'Collection expectation differs')
        metal = proof['metalAssembly']
        policy = validate_startup(proof['startupProof'],metal,slots,proof['probeCommit'])
        device = guid(policy['deviceIdentity'])
        check(re.fullmatch('[0-9a-f]{40}',proof['probeCommit']) is not None,'Invalid probe commit')
        check(proof['frozenEngineBase']==ENGINE_BASE and proof['gameCommit']==GAME_BASE,'Frozen source base differs')
        check(report['engineCodeIdentity']['informationalVersion']=='21.13.0+'+proof['probeCommit'],
              'Recorder source identity differs')
        check(report['appCodeIdentity']['informationalVersion'].endswith('+'+GAME_BASE),'Game source identity differs')
        sources=proof['sourceHashes'];paths=[row['path'] for row in sources]
        check(bool(paths) and len(paths)==len(set(paths))
              and all(re.fullmatch('[0-9a-f]{64}',row['sha256']) for row in sources),'Source hash inventory invalid')
        runtime=[row for row in proof['runtimeFiles'] if row['path'].split('/')[-1]=='KhaozEngine.Gpu.Metal.dll']
        check(len(runtime)==1 and runtime[0]['sha256']==metal['sha256']==runtime[0]['afterSha256'],
              'Startup Metal identity differs from mapped payload')
        windows=[report['baseline']['window'],report['end']['window']]
        boundaries=[report['baseline']['recordingWait'],report['end']['recordingWait']]
        receipts=[]; complete=True
        check(report['shadowStages']['mode']==1 and type(report['shadowStages']['mode']) is int,'Counts mode required')
        for sample in report['samples']:
            windows.extend([sample['completedFrameWindow'],sample['boundaryWindow']])
            row=sample['recordingWait'];boundaries.extend([row['start'],row['end']])
            callback=row['callback'];check(callback['prefixExpected'] is True,'Split prefix required')
            for role in ('prefix','main'):
                observation=callback[role]
                if observation['status']==2 and observation['receipt'] is not None: receipts.append(observation['receipt'])
                else: complete=False
        for window in windows:
            check(integer(window['width'])==1668 and integer(window['height'])==1029,'Actual framebuffer differs')
            check(window['focused'] is True and window['minimized'] is False and window['renderSuppressed'] is False,
                  'Actual window is not active and focused')
            check(window['vsync'] is True and integer(window['effectiveBaseCapHz'])==0,'Actual VSync/software cap differs')
            check(window['preparePhaseSplitActive'] is True and window['rendererInitializesSwapchain'] is True,
                  'Actual split/output policy differs')
        for boundary in boundaries:
            if boundary['status']==1 and boundary['totals'] is not None:
                value=boundary['totals']
                check(integer(value['depth'])==slots and guid(value['deviceIdentity'])==device,
                      'Available boundary treatment differs')
            else: complete=False
        for value in receipts:
            check(integer(value['depth'])==slots and guid(value['deviceIdentity'])==device,'Recording receipt treatment differs')
        complete=complete and bool(receipts) and bool(report['samples'])
        return dict(status='invalid' if errors else 'valid' if complete else 'limited',errors=errors,policy=policy)
    except (KeyError,TypeError,ValueError,IndexError,AttributeError,OverflowError) as error:
        return dict(status='invalid',errors=errors+[type(error).__name__+': '+str(error)],policy=policy)


def _run_context(report, proof, slots):
    from analyze import analyse, _analyse_context, leaves
    audit=analyse(report,1,report.get('processId'),proof,expected_recording_slots=slots)
    result=dict(audit=audit,capacity=audit.get('capacity'),workDistributions={})
    try:
        context=_analyse_context(report,1,report['processId'])
        histograms={}
        for sample in report['samples']:
            for key,value in leaves(sample['shadowStage']['callback']['snapshot']['counters']):
                histograms.setdefault(key,Counter())[json.dumps(value,sort_keys=True)]+=1
        distributions={key:[dict(value=json.loads(value),count=count,
                                  share=[Fraction(count,len(report['samples'])).numerator,
                                         Fraction(count,len(report['samples'])).denominator])
                            for value,count in sorted(values.items())] for key,values in sorted(histograms.items())}
        result.update(configuration=context['configuration'],window={key:context['window'][key] for key in WINDOW_FIELDS},
                      workDistributions=distributions,frameMilliseconds=context['frameMilliseconds'],
                      framesPerSecond=context['framesPerSecond'],counterDelta=context['counterDelta'],
                      waits=report['recordingWaits'])
    except (KeyError,TypeError,ValueError,IndexError,AttributeError,ZeroDivisionError):
        pass  # Invalid or historical inputs remain visible through the audit, without invented statistics.
    return result


def compare_runs(control_report, control_proof, candidate_report, candidate_proof):
    control=_run_context(control_report,control_proof,3);candidate=_run_context(candidate_report,candidate_proof,6)
    differences=[]
    for name,run in [('control',control),('candidate',candidate)]:
        if run['audit']['status']!='valid': differences.append(name+' measurement is '+run['audit']['status'])
    def compare(name,left,right):
        if left!=right: differences.append(name+' differs')
    for key in ('configuration','window'):
        compare(key,control.get(key),candidate.get(key))
    def work(run):
        return {key:[(json.dumps(row['value'],sort_keys=True),row['share']) for row in rows]
                for key,rows in run['workDistributions'].items()}
    compare('recorded work distribution',work(control),work(candidate))
    for key in ('probeCommit','gameCommit','sourceHashes','codeIdentities','preparationSha256'):
        compare(key,control_proof.get(key),candidate_proof.get(key))
    def dlls(proof): return sorted((row['path'].split('/')[-1],row['sha256']) for row in proof.get('runtimeFiles',[]))
    compare('DLL bytes',dlls(control_proof),dlls(candidate_proof))
    if all(run.get('capacity') and isinstance(run['capacity'].get('policy'),dict) for run in (control,candidate)):
        compare('native device',control['capacity']['policy'].get('deviceName'),candidate['capacity']['policy'].get('deviceName'))
    comparable=not differences
    return dict(comparable=comparable,differences=differences,control=control,candidate=candidate,
                observedFramesPerSecondDifference=candidate['framesPerSecond']-control['framesPerSecond'] if comparable else None,
                limits=['One observed same-view pair only. Matching recorded work does not prove all work or host load matched.',
                        'CPU API durations are distinct from GPU execution, literal parked time and physical display cadence.',
                        'No causal or sustained-rate conclusion follows from this pair.'])
