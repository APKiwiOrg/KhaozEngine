import base64
import copy
import hashlib
import json
import unittest
from unittest.mock import patch
import capacity
import collect
import inputs
from analyze import analyse, REQUIRED_CLOSURE
from test_capacity import change_policy
from test_inputs import contract, stability_fixture, stability_workflow

PURPOSE = 'paused-sun-input-stability-release-21.21.0'
GAME = 'bfde827b4f959cbdbfd44c14e39ec85b7c35fbdc'
BASE = '84d8fb7f2b34882eb387e252b4058f155ba5700c'
WORLD = '0e818bed9e529ae826b20c4994fb8a2250e5764559bb41613eb233930b35263d'
CATALOG = '786b926e58dafdad62ae6845b82bc8a228de05043c87ab1f87cf0ee8a4e030cc'


def release_contract():
    return dict(contract(), purpose=PURPOSE, engineBase=BASE, gameCommit=GAME,
                engineSourceVersion='21.21.0', assemblyVersion='21.21.0.0')


def release_fixture():
    report, proof = stability_fixture()
    proof.update(purpose=PURPOSE, inputContract=release_contract(), gameCommit=GAME,
                 frozenEngineBase=BASE, runtimeFilesRehashed=661, contentFiles=411)
    proof['worldSelection'].update(gameCommit=GAME, worldSha256=WORLD, catalogSha256=CATALOG)
    report['engineCodeIdentity']['informationalVersion'] = '21.21.0+' + proof['probeCommit']
    report['appCodeIdentity']['informationalVersion'] = '0.1.0+' + GAME
    proof['codeIdentities'] = dict(app=report['appCodeIdentity'].copy(), engine=report['engineCodeIdentity'].copy())
    proof['metalAssembly'].update(assemblyVersion='21.21.0.0', informationalVersion='21.21.0+'+proof['probeCommit'])
    change_policy(proof, 'assemblyVersion', '21.21.0.0')
    change_policy(proof, 'assemblyInformationalVersion', '21.21.0+'+proof['probeCommit'])
    return report, proof


class ReleaseIdentityTests(unittest.TestCase):
    def test_new_contract_is_explicit_and_legacy_contract_is_unchanged(self):
        self.assertEqual(release_contract(), inputs.validate_contract(release_contract()))
        self.assertEqual(contract(), inputs.validate_contract(contract()))
        for field, value in release_contract().items():
            bad = copy.deepcopy(release_contract())
            bad[field] = not value if type(value) is bool else float(value) if type(value) is int else value+'wrong'
            with self.subTest(field=field), self.assertRaises(ValueError): inputs.validate_contract(bad)

    def test_release_proof_accepts_exact_new_source_versions_and_inventory(self):
        report, proof = release_fixture()
        audit = analyse(report, 1, 123, proof, expected_recording_slots=3)
        self.assertEqual('valid', audit['status'], audit)
        self.assertEqual('not established', audit['inputStability']['fullWorkloadEquivalence'])
        self.assertFalse(capacity.compare_runs(report, proof, report, proof)['comparable'])

    def test_old_and_new_purposes_cannot_substitute_source_or_versions(self):
        for field in ('purpose', 'inputContract', 'gameCommit', 'frozenEngineBase', 'worldSelection',
                      'metalAssembly', 'startupProof', 'codeIdentities', 'runtimeFilesRehashed', 'contentFiles'):
            for make, foreign in ((release_fixture, stability_fixture), (stability_fixture, release_fixture)):
                report, proof = make()
                proof[field] = foreign()[1][field]
                with self.subTest(field=field, purpose=proof['purpose']):
                    self.assertEqual('invalid', analyse(report, 1, 123, proof, expected_recording_slots=3)['status'])

    def test_release_inventory_counts_and_world_descriptor_keep_exact_types(self):
        report, proof = release_fixture()
        for field in ('runtimeFilesRehashed', 'contentFiles', 'worldFilesRehashed'):
            for bad in (True, float(proof[field]), proof[field]-1):
                changed = copy.deepcopy(proof); changed[field] = bad
                self.assertEqual('invalid', analyse(report, 1, 123, changed, expected_recording_slots=3)['status'])
        for field in ('schemaVersion', 'worldFiles', 'catalogFiles'):
            changed = copy.deepcopy(proof); changed['worldSelection'][field] = float(changed['worldSelection'][field])
            self.assertEqual('invalid', analyse(report, 1, 123, changed, expected_recording_slots=3)['status'])

    def test_release_collection_finalizes_and_archives_without_changing_legacy_protocol(self):
        import archive
        with stability_workflow() as w:
            report, proof = release_fixture()
            w.report.clear(); w.report.update(report)
            m = w.manifest
            world = m['worldSelection']['worldSha256']; catalog = m['worldSelection']['catalogSha256']
            m.update(purpose=PURPOSE, inputContract=release_contract(), frozenEngineBase=BASE,
                     gameCommit=GAME, runtimeFileCount=661, contentFileCount=411)
            m['worldSelection']['gameCommit'] = GAME
            m['appCodeIdentity'].update(report['appCodeIdentity'])
            m['engineCodeIdentity'].update(report['engineCodeIdentity'])
            for row in m['replacements']:
                row.update(assemblyVersion='21.21.0.0', fileVersion='21.21.0.0',
                           informationalVersion='21.21.0+'+m['probeCommit'])
                for reference in row['references']: reference['version'] = '21.21.0.0'
            # Synthetic filesystem fixture only. Production inventory is the audited pristine manifest.
            for name in [f'Content/release-{i}.data' for i in range(16)] + ['runtime/release.data']:
                path = w.result.parent/'client'/name
                path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(b'release fixture')
                m['clientFiles'].append(dict(path=name, sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                                            bytes=path.stat().st_size, mode=oct(path.stat().st_mode&0o777)))
            preparation = w.result.parent/'preparation.json'
            preparation.write_text(json.dumps(m))
            startup = w.result.parent/'startup.log'
            # capacity_workflow supplies the owned log path in argv.
            import sys
            startup = type(startup)(sys.argv[sys.argv.index('--startup-log')+1])
            startup.write_bytes(base64.b64decode(proof['startupProof']['lineBase64']))
            argv = [PURPOSE if value == inputs.PURPOSE else value for value in sys.argv]
            with patch.object(sys, 'argv', argv), patch.object(capacity, 'RELEASE_WORLD_INPUT_SHA256', world, create=True), \
                 patch.object(capacity, 'RELEASE_CATALOG_INPUT_SHA256', catalog, create=True):
                self.assertEqual(REQUIRED_CLOSURE, collect.validate_preparation(m))
                self.assertEqual(0, collect.main(), w.metadata.read_text())
            final = json.loads(w.metadata.read_text())
            self.assertEqual((661, 411, 40), (final['runtimeFilesRehashed'], final['contentFiles'], final['worldFilesRehashed']))
            self.assertEqual(1, len(w.triggers))
            self.assertEqual(release_contract(), final['inputContract'])
            archive.verify_bundle(w.archiveDestination)


if __name__ == '__main__': unittest.main()
