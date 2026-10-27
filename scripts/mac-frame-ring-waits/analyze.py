"""Strict offline schema-4 audit. Collection and process ownership stay separate."""
from collections import Counter, defaultdict
import argparse
import datetime
import hashlib
import json
import math
from pathlib import Path
import statistics
import sys

# Offline analysis must leave the helper source clean for the next collection.
if __name__ == '__main__':
    sys.dont_write_bytecode = True

STAGES = ('scenePrefix pointRequests pointPreparation rigidIndexBuild slotLookup staticSignatures '
          'rigidSignatureQuery rigidSignatureFold foliageIndexBuild foliageSignatureQuery foliageSignatureFold '
          'rowSelection rowPreparation rowRecording rigidRowQuery foliageRowQuery receiverPublication '
          'cascadeFit directionalPreparation directionalSelection directionalRecording directionalCommit').split()

def stats(values):
    if not values:
        return {'sampleCount': 0, 'mean': None, 'median': None, 'p95': None, 'min': None, 'max': None}
    ordered = sorted(values)
    return {'sampleCount': len(values), 'mean': statistics.fmean(values),
            'median': statistics.median(values), 'p95': ordered[math.ceil(len(values) * .95) - 1],
            'min': ordered[0], 'max': ordered[-1]}

def leaves(value, prefix=''):
    for name, item in value.items():
        key = prefix + name
        if isinstance(item, dict):
            yield from leaves(item, key + '.')
        else:
            yield key, item

def _analyse_context(report, mode, pid):
    errors = []
    def check(condition, message):
        if not condition:
            errors.append(message)
    def close(a, b):
        return math.isfinite(a) and math.isfinite(b) and math.isclose(a, b, rel_tol=1e-10, abs_tol=1e-9)

    check(report['schemaVersion'] == 4, 'schemaVersion must be 4')
    check(report['processId'] == pid, 'processId mismatch')
    check(report['shadowStages']['mode'] == mode, 'shadow mode mismatch')
    check(mode in (1, 2), 'comparison requires counts or timings')
    check(report['stopReason'] == 'duration', 'capture did not reach its duration boundary')
    samples = report['samples']
    check(0 < len(samples) <= 4096 and len(samples) == report['sampleCount'], 'sample count mismatch')
    if not samples:
        return {'status': 'invalid', 'errors': errors}
    frequency = report['stopwatchFrequency']
    check(type(frequency) is int and frequency > 0, 'invalid frequency')
    if frequency <= 0:
        return {'status': 'invalid', 'errors': errors}
    baseline, end = report['baseline'], report['end']
    previous_tick = baseline['timestamp']
    previous_window = baseline['window']
    previous_counters = baseline['counters']
    epoch = samples[0]['shadowStage']['request']['epoch']
    check(epoch != '00000000-0000-0000-0000-000000000000', 'empty epoch')
    configuration = None
    intervals = []
    durations = defaultdict(list)
    counts = defaultdict(list)
    categories = defaultdict(Counter)
    observed_status = Counter()
    baseline_window = baseline['window']

    def window_valid(window):
        return (window['focused'] and not window['minimized'] and not window['renderSuppressed']
                and window['width'] > 0 and window['height'] > 0
                and all(window[k] == baseline_window[k] for k in
                        ['width', 'height', 'effectiveBaseCapHz', 'vsync', 'rendererInitializesSwapchain',
                         'preparePhaseSplitActive']))

    check(window_valid(baseline_window), 'baseline window invalid')
    for index, sample in enumerate(samples, 1):
        check(sample['startTimestamp'] == previous_tick, f'interval {index} is not contiguous')
        ticks = sample['endTimestamp'] - sample['startTimestamp']
        check(ticks > 0, f'interval {index} is not positive')
        check(close(sample['monotonicIntervalSeconds'], ticks / frequency), f'interval {index} conversion mismatch')
        check(math.isfinite(sample['loopDeltaSeconds']) and sample['loopDeltaSeconds'] >= 0, f'interval {index} bad raw delta')
        intervals.append(ticks * 1000.0 / frequency)
        check(sample['completedFrameWindow'] == previous_window, f'interval {index} completed window mismatch')
        window = sample['boundaryWindow']
        check(window_valid(sample['completedFrameWindow']) and window_valid(window), f'interval {index} window changed')
        for key in ['preparePhaseSubmissionCount', 'preparePhaseCompositeCount']:
            check(window[key] - previous_window[key] == int(baseline_window['preparePhaseSplitActive']), f'interval {index} {key} does not pair')
        check(window['preparePhaseSubmissionCount'] == window['preparePhaseCompositeCount'], f'interval {index} split totals differ')
        if previous_counters['hasValue'] and sample['counters']['hasValue']:
            check(sample['counters']['framesBegun'] - previous_counters['framesBegun'] == 1,
                  f'interval {index} present counter does not pair')
        previous_counters = sample['counters']
        previous_tick, previous_window = sample['endTimestamp'], window
        row = sample['shadowStage']
        observed_status[row['status']] += 1
        check(integer(row['status'],0,14) == 1, f'interval {index} stage status invalid')
        request = {'epoch': epoch, 'sequence': index, 'mode': mode}
        check(row['request'] == request, f'interval {index} request mismatch')
        callback = row['callback']
        check(callback['completed'] and callback['producerCount'] == 1, f'interval {index} incomplete or duplicate producer')
        check(callback['request'] == request, f'interval {index} callback identity mismatch')
        snapshot = callback['snapshot']
        if snapshot is None:
            check(False, f'interval {index} has no snapshot')
            continue
        check(snapshot['request'] == request and snapshot['status'] == 1, f'interval {index} snapshot identity/status mismatch')
        configuration = snapshot['configuration'] if configuration is None else configuration
        check(snapshot['configuration'] == configuration, f'interval {index} configuration changed')
        if snapshot['counters'] is None:
            check(False, f'interval {index} has no counters')
        else:
            for key, value in leaves(snapshot['counters']):
                if isinstance(value, bool) or key == 'foliageBuildOrigin':
                    categories[key][str(value)] += 1
                else:
                    check(type(value) is int and value >= 0, f'interval {index} invalid count {key}')
                    counts[key].append(value)
        timing = snapshot['timings']
        if mode == 1:
            check(timing is None, f'interval {index} counts mode contains timings')
        elif timing is None:
            check(False, f'interval {index} timing mode has no timings')
        else:
            check(timing['frequency'] == frequency, f'interval {index} timing frequency mismatch')
            check(timing['timestampReads'] > 0, f'interval {index} timing mode recorded no reads')
            check(timing['scenePrefix'] is not None, f'interval {index} scene prefix absent')
            for key in STAGES:
                value = timing[key]
                if value is not None:
                    check(type(value) is int and value >= 0, f'interval {index} negative or invalid {key}')
                    durations[key].append(value * 1000.0 / frequency)
    check(previous_tick == end['timestamp'] and previous_window == end['window'], 'end boundary mismatch')
    check(samples[-1]['counters'] == end['counters'], 'end device counters mismatch')
    elapsed = (end['timestamp'] - baseline['timestamp']) / frequency
    check(elapsed >= 10 and close(elapsed, report['durationSeconds']), 'duration mismatch')
    check((samples[-1]['startTimestamp'] - baseline['timestamp']) / frequency < 10, 'capture continued past first duration boundary')
    cadence = stats(intervals)
    fps = len(intervals) * 1000.0 / sum(intervals)
    summary = report['summary']
    for field, actual in [('framesPerSecond', fps), ('meanFrameMs', cadence['mean']),
                          ('medianFrameMs', cadence['median']), ('p95FrameMs', cadence['p95']),
                          ('p99FrameMs', sorted(intervals)[math.ceil(len(intervals) * .99) - 1]),
                          ('worstFrameMs', cadence['max']),
                          ('shareWithin144HzBudget', sum(ms <= 1000.0 / 144 for ms in intervals) / len(intervals))]:
        check(close(summary[field], actual), field + ' summary mismatch')
    check(summary['allForegroundRenderedStableSize'] and summary['allPreparePhaseSubmissionsPaired'], 'reported window/pairing validity false')
    check(not summary['capacityLimited'] and not summary['bufferCapacityReached'], 'buffer capacity reached')
    check(summary['cadenceCapped'] == (baseline_window['effectiveBaseCapHz'] > 0 or baseline_window['vsync']), 'cap summary mismatch')
    supported = baseline['counters']['hasValue'] and all(s['counters']['hasValue'] for s in samples)
    delta = summary['counterDelta']
    check((delta is not None) == supported, 'device counter availability mismatch')
    if supported and delta is not None:
        for name in ['framesBegun', 'drainCount', 'drainMs', 'backpressureStallCount', 'backpressureStallMs',
                     'acquireWaitCount', 'acquireWaitMs', 'offTimelineDeferred', 'offTimelineOutstanding']:
            expected = end['counters'][name] - baseline['counters'][name]
            field = 'offTimelineOutstandingChange' if name == 'offTimelineOutstanding' else name
            check(delta[field] == expected if type(expected) is int else close(delta[field], expected), field + ' device delta mismatch')
            per_interval = field + 'PerInterval'
            if per_interval in delta:
                check(close(delta[per_interval], expected / len(samples)), field + ' device denominator mismatch')
    stage_summary = report['shadowStages']
    check(stage_summary['validRows'] == len(samples) and stage_summary['invalidRows'] == 0
          and stage_summary['comparable'], 'stage coverage not complete')
    saved_coverage = {row['status']: row['rows'] for row in stage_summary['coverage']}
    check(sum(saved_coverage.values()) == len(samples) and all(saved_coverage.get(k) == v for k, v in observed_status.items()), 'coverage summary mismatch')
    stage_stats = {key: stats(durations[key]) for key in STAGES}
    check({row['stage'][0].lower() + row['stage'][1:] for row in stage_summary['timings']} == set(STAGES), 'timing summary inventory mismatch')
    for row in stage_summary['timings']:
        key = row['stage'][0].lower() + row['stage'][1:]
        actual = stage_stats[key]
        check(row['sampleCount'] == actual['sampleCount'], key + ' denominator mismatch')
        for field, expected in [('meanMs', actual['mean']), ('maxMs', actual['max'])]:
            check(row[field] is None if expected is None else row[field] is not None and close(row[field], expected), key + ' summary mismatch')
    expected_prefix = stage_stats['scenePrefix']['mean']
    check(stage_summary['meanScenePrefixMs'] is None if expected_prefix is None else
          stage_summary['meanScenePrefixMs'] is not None and close(stage_summary['meanScenePrefixMs'], expected_prefix),
          'meanScenePrefixMs mismatch')
    return {'status': 'valid' if not errors else 'invalid', 'errors': errors, 'processId': pid,
            'mode': mode, 'epoch': epoch, 'sampleCount': len(samples), 'durationSeconds': elapsed,
            'framesPerSecond': fps, 'frameMilliseconds': cadence, 'configuration': configuration,
            'window': baseline_window, 'cadenceCapped': summary['cadenceCapped'],
            'counterDelta': summary['counterDelta'], 'stageMilliseconds': stage_stats,
            'workCountsPerFrame': {key: stats(values) for key, values in counts.items()},
            'categoricalFrameCounts': dict(categories),
            'limits': ['Callback cadence is not physical display cadence.',
                       'Stages are inclusive CPU wall times, including blocking and scheduling.',
                       'Different workloads do not establish timer overhead or speedup.',
                       'Process ownership, mapped DLL hashes and focus endpoints require separate collection evidence.']}


# Numeric states are the private schema-4 contract, independent of the C# implementation.
LONG_MAX = (1 << 63) - 1
ULONG_MAX = (1 << 64) - 1
INT_MAX = (1 << 31) - 1
REQUIRED_CLOSURE = {'KhaozEngine.Gpu', 'KhaozEngine.Gpu.Metal', 'KhaozEngine.Windowing',
                    'KhaozEngine.Render3D', 'KhaozEngine.Game.Render3D'}


def integer(value, low=0, high=LONG_MAX):
    if type(value) is not int or not low <= value <= high:
        raise ValueError('Invalid exact integer')
    return value


# Integer/enum and Boolean inventories mirror the schema's serialized value carriers.
WINDOW_INTS = 'width height effectiveBaseCapHz'
WINDOW_LONGS = 'preparePhaseSubmissionCount preparePhaseCompositeCount'
WINDOW_FLAGS = ('focused minimized renderSuppressed vsync rendererInitializesSwapchain '
                'preparePhaseSplitActive')
COUNTER_FLAGS = 'hasValue'
COUNTER_LONGS = 'framesBegun drainCount backpressureStallCount acquireWaitCount offTimelineDeferred offTimelineOutstanding'
CONFIG_INTS = ('directionalMode cascadeMergeGap directionalResolution cascadeCount pointFaceResolution '
               'pointRows transientRows staticBudget dynamicBudget pointFilter')
CONFIG_FLAGS = 'hudTiming terrainCastsShadows cascadeCulling gpuSkinning pointEnabled'
SHADOW_INTS = ('staticRequests dynamicRequests touchAttempts touchHits acquireAttempts acquireSuccesses '
               'acquireRefusals signatures signatureMismatches selectedStaticRows selectedDynamicRows '
               'selectedTransientRows pointSlotsInUse pointShadowedLights packedRows completedStaticRebuilds '
               'completedDynamicRenders transientDemand pointFaceDrawCalls pointFoliageFaceDrawCalls '
               'pointDynamicSkinnedFaceDrawCalls pointTransientSkinnedFaceDrawCalls rigidIndexPopulation '
               'rigidOversizePopulation foliageSources foliageRanges foliageSelectedProxies directionalCascadeCount '
               'directionalInvalidationCasters directionalMaskTests directionalSelectedSpans '
               'directionalSelectedInstances directionalRigidDrawCalls directionalSkinnedDrawCalls directionalFoliageDrawCalls')
SHADOW_LONGS = 'pointRigidIssuedInstances pointFoliageIssuedInstances foliageStreamProxies'
SHADOW_FLAGS = ('hasPointRequests pointAtlasAvailable directionalActive directionalRendered directionalSkipped '
                'rigidIndexBuilt foliageIndexBuilt')
SHADOW_QUERIES = 'rigidSignatureQueries rigidRowQueries foliageSignatureQueries foliageRowQueries'.split()
SHADOW_CALLBACK_FLAGS = 'completed'
SUMMARY_FLAGS = ('allForegroundRenderedStableSize cadenceCapped bufferCapacityReached capacityLimited '
                 'allPreparePhaseSubmissionsPaired')
SHADOW_SUMMARY_FLAGS = 'comparable'
RING_REQUEST_FLAGS = 'enabled'
RING_CALLBACK_FLAGS = 'prefixExpected identityViolation'
RING_SUMMARY_FLAGS = ('enabled roleCoverageComplete boundaryCoverageComplete intervalCoverageComplete fullyAttributed')
COLLECTION_FLAGS = 'freshPaths triggerCreated ownershipReserved preflightRechecked triggerConsumed'


def integer_fields(value, names, low=0, high=LONG_MAX):
    for name in names.split():
        integer(value[name],low,high)


def boolean_fields(value, names):
    for name in names.split():
        if type(value[name]) is not bool:
            raise ValueError('Invalid exact Boolean '+name)


def typed_fields(value, ints='', longs='', flags='', other=(), enums=None, int_low=0):
    enums = enums or {}
    boolean_fields(value,flags)
    for name, item in value.items():
        if name in ints.split():
            integer(item,int_low,INT_MAX)
        elif name in longs.split():
            integer(item)
        elif name in enums:
            integer(item,0,enums[name])
        elif name not in flags.split() and name not in other:
            raise ValueError('Unknown schema field '+name)


def _schema_types(report, mode, pid):
    integer(mode,1,2); integer(pid,2,INT_MAX)
    integer(report['schemaVersion'],4,4); integer(report['processId'],2,INT_MAX)
    integer(report['sampleCount'],1,4096); integer(report['stopwatchFrequency'],1)

    def window(value):
        typed_fields(value,WINDOW_INTS,WINDOW_LONGS,WINDOW_FLAGS)
    def counters(value):
        typed_fields(value,longs=COUNTER_LONGS,flags=COUNTER_FLAGS,other=('drainMs','backpressureStallMs','acquireWaitMs'))
    def request(value):
        integer(value['sequence']); integer(value['mode'],0,2)
    for endpoint in ('baseline','end'):
        value=report[endpoint]
        integer(value['timestamp'],-LONG_MAX-1)
        window(value['window']); counters(value['counters']); boundary(value['recordingWait'])
    for sample in report['samples']:
        integer_fields(sample,'startTimestamp endTimestamp',-LONG_MAX-1)
        window(sample['completedFrameWindow']); window(sample['boundaryWindow']); counters(sample['counters'])
        shadow=sample['shadowStage']; callback=shadow['callback']
        boolean_fields(callback,SHADOW_CALLBACK_FLAGS)
        integer(shadow['status'],0,9); request(shadow['request']); request(callback['request'])
        integer(callback['producerCount'],0,INT_MAX)
        snapshot=callback['snapshot']
        if snapshot is not None:
            request(snapshot['request']); integer(snapshot['status'],0,9)
            typed_fields(snapshot['configuration'],CONFIG_INTS,flags=CONFIG_FLAGS,int_low=-INT_MAX-1)
            counts=snapshot['counters']
            if counts is not None:
                typed_fields(counts,SHADOW_INTS,SHADOW_LONGS,SHADOW_FLAGS,SHADOW_QUERIES,{'foliageBuildOrigin':3})
                for name in SHADOW_QUERIES:
                    if name in counts:
                        typed_fields(counts[name],'calls','exactTests accepted sortInputs')
            timing=snapshot['timings']
            if timing is not None:
                integer(timing['frequency'],1); integer(timing['timestampReads'],0,INT_MAX)
                for stage in STAGES:
                    if timing[stage] is not None:integer(timing[stage])
        ring=sample['recordingWait']; callback=ring['callback']
        boolean_fields(ring['request'],RING_REQUEST_FLAGS); boolean_fields(callback['request'],RING_REQUEST_FLAGS)
        boolean_fields(callback,RING_CALLBACK_FLAGS)
        request(ring['request']); request(callback['request']); integer(ring['status'],0,14)
        integer_fields(callback,'declarationCount acknowledgementCount prefixObservations mainObservations',0,INT_MAX)
        for role in ('prefix','main'):
            observation=callback[role]
            integer(observation['status'],0,10); integer(observation['priorAttempt'],0,ULONG_MAX)
        boundary(ring['start']); boundary(ring['end'])
    boolean_fields(report['summary'],SUMMARY_FLAGS)
    boolean_fields(report['recordingWaits'],RING_SUMMARY_FLAGS)
    delta=report['summary']['counterDelta']
    if delta is not None:
        integer_fields(delta,'framesBegun drainCount backpressureStallCount acquireWaitCount offTimelineDeferred')
        integer(delta['offTimelineOutstandingChange'],-LONG_MAX-1)
    stages=report['shadowStages']
    boolean_fields(stages,SHADOW_SUMMARY_FLAGS)
    integer(stages['mode'],0,2); integer_fields(stages,'validRows invalidRows',0,4096)
    for row in stages['coverage']:
        integer(row['status'],0,9); integer(row['rows'],0,4096)
    for row in stages['timings']:
        integer(row['sampleCount'],0,4096)
    # Ring contributions/remainders and coverage summaries use exact() against bounded,
    # independently computed integer values below. Null remains unavailable, never zero.


def guid(value):
    import uuid
    parsed = uuid.UUID(value)
    if parsed.int == 0:
        raise ValueError('Empty diagnostic identity')
    return str(parsed)


def metadata(value):
    return guid(value['deviceIdentity']), integer(value['depth'], 1, INT_MAX), integer(value['frequency'], 1)


def boundary(value):
    status = integer(value['status'], 0, 5)
    totals = value['totals']
    if status == 1:
        metadata(totals)
        integer(totals['count']); integer(totals['ticks'])
        if totals['count'] == 0 and totals['ticks'] != 0:
            raise ValueError('Zero total count has nonzero ticks')
    elif totals is not None:
        raise ValueError('Unavailable boundary contains invented totals')
    return status


def receipt(observation, frequency):
    if integer(observation['status'],0,10) != 2:
        raise ValueError('Missing successful recording observation')
    r = observation['receipt']
    prior = integer(observation['priorAttempt'], 0, ULONG_MAX)
    if integer(r['attempt'], 0, ULONG_MAX) != ((prior + 1) & ULONG_MAX):
        raise ValueError('Stale recording attempt')
    meta = metadata(r)
    if meta[2] != frequency:
        raise ValueError('Receipt clock disagrees with report')
    guid(r['listIdentity'])
    ordinal = integer(r['recordingOrdinal'], 0, ULONG_MAX)
    if integer(r['segment'],0,INT_MAX) != ordinal % meta[1]:
        raise ValueError('Segment disagrees with captured ordinal')
    count = integer(r['count'], 0, 1); ticks = integer(r['ticks']); calls = integer(r['waitCallCount'], 0, (1 << 31)-1)
    if (count == 0 and (ticks != 0 or calls != 0)) or (count == 1 and calls == 0):
        raise ValueError('Wait count, ticks and calls disagree')
    return r


def reconcile(start, end, identity, count, ticks):
    if start['status'] != 1 or end['status'] != 1:
        return dict(status=2, count=None, ticks=None)
    a, b = start['totals'], end['totals']
    if metadata(a) != identity or metadata(b) != identity:
        return dict(status=4, count=None, ticks=None)
    c, t = b['count'] - a['count'] - count, b['ticks'] - a['ticks'] - ticks
    if not -LONG_MAX-1 <= c <= LONG_MAX or not -LONG_MAX-1 <= t <= LONG_MAX:
        return dict(status=5, count=None, ticks=None)
    if c < 0 or t < 0:
        return dict(status=6, count=None, ticks=None)
    return dict(status=1, count=c, ticks=t)


def exact(value, expected):
    if type(value) is not type(expected):
        return False
    if isinstance(expected,dict):
        return value.keys() == expected.keys() and all(exact(value[k],v) for k,v in expected.items())
    return value == expected


def _ring_audit(report):
    errors = []
    def check(value, message):
        if not value:
            errors.append(message)
    rows = report['samples']; n = len(rows); frequency = integer(report['stopwatchFrequency'], 1)
    summary = report['recordingWaits']
    check(summary['enabled'] is True, 'Ring probe was not enabled')
    expected_epoch = guid(rows[0]['shadowStage']['request']['epoch'])
    expected_mode = integer(report['shadowStages']['mode'],1,2)
    expected_identity = None
    attempts, ordinals = set(), set()
    prefix_count = prefix_ticks = main_count = main_ticks = 0
    valid_roles = prefixes = intervals = 0
    snapshots = []
    previous = report['baseline']['recordingWait']
    snapshots.append(previous)
    check(previous == rows[0]['recordingWait']['start'], 'Baseline ring boundary mismatch')
    for index, sample in enumerate(rows, 1):
        row = sample['recordingWait']; c = row['callback']
        req = dict(epoch=expected_epoch, sequence=index, enabled=True, mode=expected_mode)
        check(exact(row['request'],req) and exact(c['request'],req), f'Ring callback {index} identity/mode mismatch')
        check(integer(row['status'],0,14) == 1, f'Ring callback {index} role coverage invalid')
        check(c['acknowledgementCount'] == 1 and type(c['acknowledgementCount']) is int,
              f'Ring callback {index} acknowledgement cardinality')
        check(c['declarationCount'] == 1 and type(c['declarationCount']) is int,
              f'Ring callback {index} declaration cardinality')
        check(c['identityViolation'] is False, f'Ring callback {index} identity violation')
        split = c['prefixExpected']
        check(type(split) is bool and split == sample['completedFrameWindow']['preparePhaseSplitActive'], 'Prefix mode changed')
        check(c['mainObservations'] == 1 and type(c['mainObservations']) is int
              and c['prefixObservations'] == int(split) and type(c['prefixObservations']) is int, 'Role cardinality mismatch')
        contributions = [receipt(c['main'], frequency)]
        if split:
            contributions.append(receipt(c['prefix'], frequency)); prefixes += 1
        else:
            check(c['prefix']['status'] == 3 and c['prefix']['receipt'] is None, 'Legacy prefix must be not applicable')
        valid_roles += 1
        identity = metadata(contributions[0]); expected_identity = expected_identity or identity
        if len(contributions) == 2:
            check(contributions[0]['listIdentity'] != contributions[1]['listIdentity'], 'Prefix and main share list identity')
        for r in contributions:
            check(metadata(r) == expected_identity, 'Recording device/depth/frequency changed')
            attempt = (guid(r['listIdentity']), r['attempt']); ordinal = (guid(r['deviceIdentity']), r['recordingOrdinal'])
            check(attempt not in attempts and ordinal not in ordinals, 'Duplicate recording attempt or ordinal')
            attempts.add(attempt); ordinals.add(ordinal)
        main_count += contributions[0]['count']; main_ticks += contributions[0]['ticks']
        if split:
            prefix_count += contributions[1]['count']; prefix_ticks += contributions[1]['ticks']
        check(row['start'] == previous, 'Interior ring boundary mismatch')
        boundary(row['start']); boundary(row['end'])
        calc = reconcile(row['start'], row['end'], identity, sum(x['count'] for x in contributions), sum(x['ticks'] for x in contributions))
        check(exact(row['reconciliation'],calc), 'Interval remainder arithmetic mismatch')
        check(calc['status'] in (1, 2), 'Invalid interval remainder')
        intervals += calc['status'] == 1
        previous = row['end']; snapshots.append(previous)
    check(previous == report['end']['recordingWait'], 'Final ring boundary mismatch')
    for snapshot in snapshots:
        if boundary(snapshot) == 1:
            check(metadata(snapshot['totals']) == expected_identity, 'Boundary device/depth/frequency changed')
    for total in (prefix_count, prefix_ticks, main_count, main_ticks, prefix_count+main_count, prefix_ticks+main_ticks):
        integer(total)
    window = reconcile(snapshots[0], snapshots[-1], expected_identity, prefix_count+main_count, prefix_ticks+main_ticks)
    check(window['status'] in (1, 2), 'Invalid full-window remainder')
    available = sum(x['status'] == 1 for x in snapshots)
    full = valid_roles == n and available == n+1 and intervals == n and window['status'] == 1
    expected_summary = dict(enabled=True, totalRows=n, validRoleRows=valid_roles, availableBoundaries=available,
                            reconciledIntervals=intervals, roleCoverageComplete=valid_roles == n,
                            boundaryCoverageComplete=available == n+1, intervalCoverageComplete=intervals == n,
                            fullyAttributed=full, prefix=dict(count=prefix_count,ticks=prefix_ticks) if prefixes else None,
                            main=dict(count=main_count,ticks=main_ticks), window=window,
                            prefixMs=prefix_ticks*1000.0/frequency if prefixes else None,
                            mainMs=main_ticks*1000.0/frequency,
                            unattributedMs=window['ticks']*1000.0/frequency if window['status'] == 1 else None)
    for key, actual in expected_summary.items():
        value = summary[key]
        if key.endswith('Ms') and actual is not None:
            check(type(value) in (int,float) and math.isfinite(value) and math.isclose(actual,value,rel_tol=1e-12,abs_tol=1e-9), key+' mismatch')
        else:
            check(exact(value,actual), key+' mismatch')
    return dict(errors=errors, fullyAttributed=full, validRoleRows=valid_roles, availableBoundaries=available,
                reconciledIntervals=intervals, windowReconciled=window['status'] == 1, windowRemainder=window,
                prefix=expected_summary['prefix'], main=expected_summary['main'])


def _identity_audit(report, proof, pid):
    boolean_fields(proof,COLLECTION_FLAGS)
    errors = []
    def check(condition, message):
        if not condition:
            errors.append(message)
    def sha(value, length=64):
        import re
        return isinstance(value,str) and re.fullmatch('[0-9a-f]{'+str(length)+'}',value) is not None
    check(integer(proof['processId'],2,INT_MAX) == pid and report['processId'] == pid, 'Identity PID mismatch')
    check(bool(proof['processBefore']) and proof['processBefore'] == proof['processAfter'], 'Process start/command identity changed')
    check(integer(proof['foregroundBefore']['pid'],2,INT_MAX) == pid
          and integer(proof['foregroundAfter']['pid'],2,INT_MAX) == pid, 'Foreground endpoint mismatch')
    check(integer(proof['requestedSeconds'],10,10) == 10 and proof['freshPaths'] is True
          and proof['triggerCreated'] is True, 'Fresh one-shot capture proof missing')
    guid(proof['collectionId'])
    check(proof['ownershipReserved'] is True and proof['preflightRechecked'] is True
          and proof['triggerConsumed'] is True, 'Owned trigger consumption proof missing')
    check(len(proof['triggerIdentity']) == 2
          and all(integer(x,0,ULONG_MAX) == x for x in proof['triggerIdentity']), 'Trigger file identity missing')
    trigger_times=[datetime.datetime.fromisoformat(proof[key]) for key in
                   ('startedAtUtc','triggeredAtUtc','triggerConsumedAtUtc','resultObservedAtUtc')]
    check(all(x.utcoffset() == datetime.timedelta(0) for x in trigger_times)
          and trigger_times == sorted(trigger_times), 'Trigger consumption chronology invalid')
    world_count=41
    if 'worldSelection' in proof:
        from capacity import validate_world_selection
        world_count=validate_world_selection(proof['worldSelection'],proof['gameCommit'])['worldFiles']
    check(integer(proof['runtimeFilesRehashed'],644,644) == 644 and integer(proof['contentFiles'],395,395) == 395
          and integer(proof['worldFilesRehashed'],world_count,world_count) == world_count, 'Payload/content/world coverage mismatch')
    check(set(proof['requiredClosure']) == REQUIRED_CLOSURE and len(proof['requiredClosure']) == 5, 'Required five-DLL closure mismatch')
    check(sha(proof['probeCommit'],40) and sha(proof['gameCommit'],40), 'Code commit identity missing')
    check(sha(proof['preparationSha256']) and sha(proof['resultSha256']), 'Manifest or result hash missing')
    check(bool(proof['sourceHashes']) and all(x['path'] and sha(x['sha256']) for x in proof['sourceHashes']), 'Source hashes missing')
    names = REQUIRED_CLOSURE | {'Grimhollow'}
    files = {Path(row['path']).name:row for row in proof['runtimeFiles']}
    check(set(files) == {x+'.dll' for x in names} and len(files) == len(proof['runtimeFiles']), 'Critical runtime inventory mismatch')
    for row in files.values():
        for key in ('stat','afterStat'):
            check(len(row[key]) == 5, 'Invalid file identity tuple')
            for index, value in enumerate(row[key]):integer(value,0,ULONG_MAX if index < 2 else LONG_MAX)
        check(sha(row['sha256']) and row['sha256'] == row['afterSha256'] and row['stat'] == row['afterStat'], 'Mapped payload changed')
    for key in ('mappedBefore','mappedAfter'):
        check({Path(x).name for x in proof[key]} == set(files), 'Critical mapped-DLL coverage mismatch')
    for key, field, commit in (('app','appCodeIdentity','gameCommit'),('engine','engineCodeIdentity','probeCommit')):
        actual = report[field]; expected = proof['codeIdentities'][key]
        check(set(actual) == set(expected) == {'name','mvid','informationalVersion'}
              and actual['name'] == expected['name']
              and actual['informationalVersion'] == expected['informationalVersion']
              and guid(actual['mvid']) == guid(expected['mvid']), key+' code identity mismatch')
        check(actual['informationalVersion'].endswith('+'+proof[commit]), key+' committed-source stamp mismatch')
    check(report['appCodeIdentity']['name'] == 'Grimhollow' and report['appAssembly'].split(',')[0] == 'Grimhollow', 'Game assembly identity mismatch')
    check(report['engineCodeIdentity']['name'] == 'KhaozEngine.Windowing'
          and report['engineCodeIdentity']['mvid'].lower() in report['engineAssembly'].lower(), 'Recorder assembly identity mismatch')
    return errors


def analyse(report, mode, pid, proof=None, *, expected_recording_slots=None):
    try:
        if type(report.get('schemaVersion')) is not int or report['schemaVersion'] != 4:
            raise ValueError('Only schema 4 is supported')
        _schema_types(report,mode,pid)
        context = _analyse_context(report,mode,pid)
        ring = _ring_audit(report)
        errors = context['errors'] + ring.pop('errors')
        if proof is not None:
            errors.extend(_identity_audit(report,proof,pid))
        capacity_result = None
        if expected_recording_slots is not None:
            from capacity import audit_treatment
            capacity_result = audit_treatment(report,proof,expected_recording_slots)
            errors.extend(capacity_result['errors'])
        full = not errors and ring['fullyAttributed'] and proof is not None
        if capacity_result is not None and capacity_result['status'] != 'valid': full = False
        return dict(status='invalid' if errors else 'valid' if full else 'limited', errors=errors,
                    processId=pid, sampleCount=report['sampleCount'], durationSeconds=report['durationSeconds'],
                    framesPerSecond=context['framesPerSecond'], identityAudited=proof is not None,
                    **{**ring, 'fullyAttributed':full},
                    **({'capacity':capacity_result} if capacity_result is not None else {}),
                    limits=['Counted CPU ring waits only. No GPU completion or physical presentation claim.',
                            'Whole-window reconciliation and interval coverage are independent.',
                            'Different workloads establish neither overhead nor speedup.'])
    except (KeyError, TypeError, ValueError, IndexError, OverflowError, AttributeError) as error:
        return dict(status='invalid', errors=[type(error).__name__+': '+str(error)], fullyAttributed=False)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input',type=Path); parser.add_argument('output',type=Path)
    parser.add_argument('--mode',type=int,choices=[1,2],required=True)
    parser.add_argument('--pid',type=int,required=True)
    parser.add_argument('--collection',type=Path)
    parser.add_argument('--expected-recording-slots',type=int,choices=[3,6])
    args=parser.parse_args()
    raw=args.input.read_bytes()
    proof=json.loads(args.collection.read_text()) if args.collection else None
    if proof is not None and proof['resultSha256'] != hashlib.sha256(raw).hexdigest():
        raise ValueError('Raw result differs from collection evidence')
    result=analyse(json.loads(raw),args.mode,args.pid,proof,expected_recording_slots=args.expected_recording_slots)
    result['inputSha256']=hashlib.sha256(raw).hexdigest()
    with args.output.open('x') as stream:
        json.dump(result,stream,indent=2);stream.write('\n')
    print(json.dumps(result))
    raise SystemExit(0 if result['status']=='valid' else 2 if result['status']=='limited' else 1)
