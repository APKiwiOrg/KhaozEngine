"""Exact-word input observations for a declared single-run stability protocol."""
from collections import Counter
import json

PURPOSE = 'paused-sun-input-stability'
ENGINE_BASE = '2599bd8a94ce43fc53de62f2544efb89e36d05a8'
LEGACY_HELPER_FILES = ('analyze.py', 'collect.py', 'capacity.py', 'archive.py')
HELPER_FILES = LEGACY_HELPER_FILES + ('inputs.py',)
MATRIX_ORDER = 'row-major-M11-M44'
CONTRACT = dict(purpose=PURPOSE, schemaVersion=5, framebufferWidth=1591, framebufferHeight=1027,
                recordingSlots=3, baseFrames=3, drawables=3, retirementCap=8, mode=1, vsync=True,
                softwareCapHz=0, preparePhaseSplit=True, rendererInitializesSwapchain=True, engineBase=ENGINE_BASE)
DECISION_FLAGS = ('active rendered skipped hadPrevious anySkinnedCaster skinnedCastersCleared '
                  'resolutionChanged lightMatrixChanged casterDataChanged').split()
COUNTS = 'prefixCount viewCount cascadeObservationCount cameraCount lightCount decisionCount'.split()
VIEW_MATRICES = ('viewWords', 'projectionWords', 'viewProjectionWords', 'absoluteViewProjectionWords')
GROUPS = ('camera', 'cascadeCamera', 'light', 'cascades')


def _require(condition, path):
    if not condition:
        raise ValueError(path)


def _object(value, names, path):
    _require(type(value) is dict and set(value) == set(names), path + ' fields differ')


def _integer(value, low, high, path):
    _require(type(value) is int and low <= value <= high, path + ' must be a bounded integer')
    return value


def _words(value, length, path, nonfinite):
    _require(type(value) is list and len(value) == length, path + ' word length differs')
    for index, word in enumerate(value):
        field = path + '[' + str(index) + ']'
        _integer(word, 0, 4294967295, field)
        if word & 0x7f800000 == 0x7f800000:
            nonfinite.append(field)


def _leaves(value, path=''):
    if type(value) is dict:
        for key, item in value.items():
            yield from _leaves(item, path + '.' + key if path else key)
    elif type(value) is list:
        for index, item in enumerate(value):
            yield from _leaves(item, path + '[' + str(index) + ']')
    else:
        yield path, value


def _first_difference(left, right):
    a, b = dict(_leaves(left)), dict(_leaves(right))
    for key in dict.fromkeys([*a, *b]):
        if key not in a or key not in b or type(a[key]) is not type(b[key]) or a[key] != b[key]:
            return key
    return None


def validate_contract(contract):
    _object(contract, CONTRACT, 'inputContract')
    for key, expected in CONTRACT.items():
        _require(type(contract[key]) is type(expected) and contract[key] == expected,
                 'inputContract.' + key + ' differs from the declared protocol')
    return contract.copy()


def _request(value, expected):
    from analyze import guid
    _object(value, ('epoch', 'sequence', 'mode'), 'request')
    guid(value['epoch'])
    _integer(value['sequence'], 1, (1 << 63) - 1, 'request.sequence')
    _integer(value['mode'], 1, 2, 'request.mode')
    _require(value == expected, 'request ownership differs')


def _counter_schema(value):
    from analyze import SHADOW_INTS, SHADOW_LONGS, SHADOW_FLAGS, SHADOW_QUERIES
    _object(value, [*SHADOW_INTS.split(), *SHADOW_LONGS.split(), *SHADOW_FLAGS.split(),
                    *SHADOW_QUERIES, 'foliageBuildOrigin'], 'counters')
    for name in SHADOW_INTS.split():
        _integer(value[name], 0, (1 << 31) - 1, 'counters.' + name)
    for name in SHADOW_LONGS.split():
        _integer(value[name], 0, (1 << 63) - 1, 'counters.' + name)
    for name in SHADOW_FLAGS.split():
        _require(type(value[name]) is bool, 'counters.' + name + ' must be Boolean')
    _integer(value['foliageBuildOrigin'], 0, 3, 'counters.foliageBuildOrigin')
    for name in SHADOW_QUERIES:
        query = value[name]
        _object(query, ('calls', 'exactTests', 'accepted', 'sortInputs'), 'counters.' + name)
        for field in query:
            _integer(query[field], 0, (1 << (31 if field == 'calls' else 63)) - 1, 'counters.' + name + '.' + field)


def _observation(value, expected, counters):
    _counter_schema(counters)
    names = ['request', 'sceneFrameIndex', *COUNTS, 'availability', 'complete', 'matrixOrder',
             'prefixEffectTimeWords', 'view', 'camera', 'light', 'fits', 'decision']
    _object(value, names, 'inputs')
    _request(value['request'], expected)
    _integer(value['sceneFrameIndex'], 1, (1 << 63) - 1, 'sceneFrameIndex')
    for name in COUNTS:
        _integer(value[name], 0, (1 << 31) - 1, name)
    _integer(value['availability'], 0, 3, 'availability')
    _require(type(value['complete']) is bool and value['complete'], 'complete must be true')
    _require(value['matrixOrder'] == MATRIX_ORDER, 'matrixOrder differs')
    _require(all(value[name] == 1 for name in ('prefixCount', 'viewCount', 'cascadeObservationCount', 'decisionCount')),
             'prefix/view/cascade/decision multiplicity differs')
    nonfinite = []
    _words(value['prefixEffectTimeWords'], 1, 'prefixEffectTimeWords', nonfinite)
    view = value['view']
    _object(view, [*VIEW_MATRICES, 'renderOriginWords', 'jitterPixelsWords', 'width', 'height'], 'view')
    for name in VIEW_MATRICES:
        _words(view[name], 16, 'view.' + name, nonfinite)
    _words(view['renderOriginWords'], 3, 'view.renderOriginWords', nonfinite)
    _words(view['jitterPixelsWords'], 2, 'view.jitterPixelsWords', nonfinite)
    for name in ('width', 'height'):
        _integer(view[name], 1, (1 << 31) - 1, 'view.' + name)
    for group, fields in (('camera', ('eyeWords', 'forwardWords')),
                          ('light', ('rawWords', 'normalizedWords', 'selectedWords'))):
        _object(value[group], fields, group)
        for field in fields:
            _words(value[group][field], 3, group + '.' + field, nonfinite)
    fits = value['fits']
    _object(fits, ('count', 'resolution', 'splitWords', 'absoluteFitWords'), 'fits')
    count = _integer(fits['count'], 0, 4, 'fits.count')
    _integer(fits['resolution'], 0, (1 << 31) - 1, 'fits.resolution')
    _words(fits['splitWords'], 4, 'fits.splitWords', nonfinite)
    _require(type(fits['absoluteFitWords']) is list and len(fits['absoluteFitWords']) == 4,
             'fits.absoluteFitWords must have four slots')
    for index, matrix in enumerate(fits['absoluteFitWords']):
        _words(matrix, 16, 'fits.absoluteFitWords[' + str(index) + ']', nonfinite)
        if index >= count:
            _require(all(word == 0 for word in matrix), 'unused fits.absoluteFitWords[' + str(index) + '] is not canonical')
    _require(all(word == 0 for word in fits['splitWords'][count:]), 'unused fits.splitWords is not canonical')
    decision = value['decision']
    _object(decision, [*DECISION_FLAGS, 'skinnedCasterCount', 'cascadeCount'], 'decision')
    for name in DECISION_FLAGS:
        _require(type(decision[name]) is bool, 'decision.' + name + ' must be Boolean')
    _integer(decision['skinnedCasterCount'], 0, (1 << 31) - 1, 'decision.skinnedCasterCount')
    _integer(decision['cascadeCount'], 0, 4, 'decision.cascadeCount')
    _require(value['availability'] != 0, 'input groups were not observed')
    active = value['availability'] == 1
    if active:
        _require(value['cameraCount'] == value['lightCount'] == 1 and count > 0 and fits['resolution'] > 0,
                 'active group coverage differs')
        _require(decision['active'] and decision['cascadeCount'] == count
                 and decision['rendered'] != decision['skipped'], 'active decision differs')
    else:
        _require(value['cameraCount'] == value['lightCount'] == count == fits['resolution'] == 0,
                 'unavailable group coverage differs')
        _require(all(word == 0 for group in ('camera', 'light', 'fits') for _, word in _leaves(value[group])),
                 'unavailable groups are not canonical')
        _require(all(item is False if type(item) is bool else item == 0 for item in decision.values()),
                 'unavailable decision is not canonical')
    for name in ('directionalActive', 'directionalRendered', 'directionalSkipped'):
        _require(type(counters[name]) is bool, 'counters.' + name + ' must be Boolean')
    _integer(counters['directionalCascadeCount'], 0, 4, 'counters.directionalCascadeCount')
    _require(counters['directionalActive'] == active and counters['directionalCascadeCount'] == count
             and counters['directionalRendered'] == decision['rendered']
             and counters['directionalSkipped'] == decision['skipped'], 'directional counters disagree with inputs')
    return nonfinite


def _summary(values, total):
    signatures = [json.dumps(value, sort_keys=True, separators=(',', ':')) for _, value in values]
    histogram = Counter(signatures)
    first_change = None
    for (sample, value), signature in zip(values[1:], signatures[1:]):
        if signature != signatures[0]:
            first_change = dict(sample=sample, field=_first_difference(values[0][1], value))
            break
    transitions = sum(a[0] + 1 == b[0] and left != right
                      for a, b, left, right in zip(values, values[1:], signatures, signatures[1:]))
    scalar_ints = bool(values) and all(type(value) is int for _, value in values)
    return dict(coverage=len(values), expectedSamples=total, distinctStates=len(histogram),
                first=None if not values else json.loads(signatures[0]),
                last=None if not values else json.loads(signatures[-1]),
                min=min(value for _, value in values) if scalar_ints else None,
                max=max(value for _, value in values) if scalar_ints else None,
                transitions=transitions, firstChange=first_change,
                distribution=[dict(value=json.loads(key), rows=count) for key, count in sorted(histogram.items())])


def _groups(value):
    view, fits = value['view'], value['fits']
    active = value['availability'] == 1
    return dict(camera=dict(view={key: view[key] for key in (*VIEW_MATRICES, 'renderOriginWords', 'width', 'height')}),
                cascadeCamera=dict(camera=value['camera']) if active else None,
                light=dict(light=value['light']) if active else None,
                cascades=dict(fits=dict(count=fits['count'], resolution=fits['resolution'],
                                       splitWords=fits['splitWords'][:fits['count']],
                                       absoluteFitWords=fits['absoluteFitWords'][:fits['count']])) if active else None)


def _controlled_nonfinite(group, field):
    return (group == 'camera' and field.startswith('view.') and not field.startswith('view.jitterPixelsWords')
            or group == 'cascadeCamera' and field.startswith('camera.')
            or group == 'light' and field.startswith('light.')
            or group == 'cascades' and field.startswith('fits.'))


def audit_observations(report, contract):
    errors, unusable, observations = [], [], []
    work, groups = {}, {name: [] for name in GROUPS}
    dynamic = {name: [] for name in ('jitter', 'sceneFrameIndex', 'effectTime', 'decision')}
    availability = Counter()
    total = 0
    try:
        c = validate_contract(contract)
        _integer(report['schemaVersion'], 5, 5, 'schemaVersion')
        total = _integer(report['sampleCount'], 1, 4096, 'sampleCount')
        _require(type(report['samples']) is list and len(report['samples']) == total, 'sample count differs')
        _integer(report['shadowStages']['mode'], c['mode'], c['mode'], 'shadowStages.mode')
        epoch = report['samples'][0]['shadowStage']['request']['epoch']
        previous_scene = None
        for sample_index, sample in enumerate(report['samples'], 1):
            try:
                row = sample['shadowStage']
                expected = dict(epoch=epoch, sequence=sample_index, mode=c['mode'])
                _request(row['request'], expected)
                _integer(row['status'], 1, 1, 'shadowStage.status')
                callback = row['callback']
                _request(callback['request'], expected)
                _require(type(callback['completed']) is bool and callback['completed'], 'callback is not complete')
                _integer(callback['producerCount'], 1, 1, 'callback.producerCount')
                snapshot = callback['snapshot']
                _request(snapshot['request'], expected)
                _integer(snapshot['status'], 1, 1, 'snapshot.status')
                from analyze import CONFIG_INTS, CONFIG_FLAGS, typed_fields
                _object(snapshot['configuration'], [*CONFIG_INTS.split(), *CONFIG_FLAGS.split()], 'configuration')
                typed_fields(snapshot['configuration'], CONFIG_INTS, flags=CONFIG_FLAGS, int_low=-(1 << 31))
                value = snapshot['inputs']
                nonfinite = _observation(value, expected, snapshot['counters'])
                if previous_scene is not None:
                    _require(value['sceneFrameIndex'] == previous_scene + 1, 'sceneFrameIndex must advance once per callback')
                previous_scene = value['sceneFrameIndex']
                unusable.extend(dict(sample=sample_index, field=field) for field in nonfinite)
                observations.append((sample_index, value))
                availability[{1: 'active', 2: 'disabled', 3: 'degenerate'}[value['availability']]] += 1
                for name, observed in _groups(value).items():
                    if observed is not None:
                        groups[name].append((sample_index, observed))
                for name, observed in dict(jitter=value['view']['jitterPixelsWords'], sceneFrameIndex=value['sceneFrameIndex'],
                                           effectTime=value['prefixEffectTimeWords'], decision=value['decision']).items():
                    dynamic[name].append((sample_index, observed))
            except (KeyError, TypeError, ValueError, IndexError, AttributeError) as error:
                errors.append('sample ' + str(sample_index) + ': ' + str(error))
            # All raw counters contribute, including rows with invalid input ownership.
            try:
                counters = sample['shadowStage']['callback']['snapshot']['counters']
                _counter_schema(counters)
                for field, value in _leaves(counters):
                    _require(type(value) is bool or type(value) is int and value >= 0, 'invalid work counter ' + field)
                    work.setdefault(field, []).append((sample_index, value))
            except (KeyError, TypeError, ValueError, AttributeError) as error:
                errors.append('sample ' + str(sample_index) + ': ' + str(error))
    except (KeyError, TypeError, ValueError, IndexError, AttributeError) as error:
        errors.append(str(error))
    controlled = {}
    for name, values in groups.items():
        summary = _summary(values, total)
        bad = [field for field in unusable if _controlled_nonfinite(name, field['field'])]
        summary['status'] = ('unusable' if bad else 'unavailable' if len(values) != total or not total
                             else 'unstable' if summary['distinctStates'] > 1 else 'stable')
        summary['unusableFields'] = bad
        available_indices = {index for index, _ in values}
        summary['unavailableSamples'] = [index for index in range(1, total + 1) if index not in available_indices]
        controlled[name] = summary
    states = {value['status'] for value in controlled.values()}
    status = next((name for name in ('unusable', 'unavailable', 'unstable') if name in states), 'stable')
    dynamics = {name: _summary(values, total) for name, values in dynamic.items()}
    for name, prefix in (('jitter', 'view.jitterPixelsWords'), ('effectTime', 'prefixEffectTimeWords')):
        dynamics[name]['unusableFields'] = [field for field in unusable if field['field'].startswith(prefix)]
    return dict(integrity=dict(status='invalid' if errors else 'valid', errors=errors, observedRows=len(observations),
                               expectedRows=total, availability=dict(availability)),
                controlledInputs=dict(status='invalid' if errors else status, groups=controlled),
                dynamicObservations=dynamics, workObservations={key: _summary(values, total) for key, values in sorted(work.items())},
                unusableFields=unusable, fullWorkloadEquivalence='not established',
                limits=['Scene effect time is observed at prefix entry, not authoritative time of day.',
                        'Actor poses, full caster identity, animation, point-cache history, foliage focus/settings and residency are unobserved.',
                        'Stable observed inputs and counts do not establish whole-workload equivalence or an FPS effect.'])
