import contextlib
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import capacity
import collect
from analyze import analyse, REQUIRED_CLOSURE
from test_collect import preparation


def fingerprint(rows):
    text=''.join(row['path']+' '+row['sha256']+'\n' for row in sorted(rows,key=lambda row:row['path']))
    return hashlib.sha256(text.encode()).hexdigest()


def selection(world_hash, catalog_hash):
    return dict(schemaVersion=1,gameCommit=capacity.GAME_BASE,root='assets/worlds/hollowmere',
                worldFiles=40,worldSha256=world_hash,catalogFiles=2,catalogSha256=catalog_hash)


@contextlib.contextmanager
def install_world(manifest, game, client=None):
    names=['world.json']+[f'regions/r_{i}_0.json' for i in range(30)]
    names += [f'navigation/{i}.kenav' for i in range(9)]
    world=[]
    for name in names:
        path=game/'assets/worlds/hollowmere'/name
        path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(b'world')
        world.append(dict(path=str(path.relative_to(game)),sha256=hashlib.sha256(b'world').hexdigest()))
    catalogs=[]
    for index,name in enumerate(('archetypes','ground')):
        relative='assets/catalogs/'+name+'.json';path=game/relative
        raw=name.encode();path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(raw)
        catalogs.append(dict(path=relative,sha256=hashlib.sha256(raw).hexdigest()))
        target='Content/catalogs/'+name+'.json'
        row=dict(path=target,sha256=hashlib.sha256(raw).hexdigest(),bytes=len(raw),mode='0o644')
        if client is None:
            manifest['clientFiles'].append(row)
        else:
            old=next(r for r in manifest['clientFiles'] if r['path']==f'Content/{index}.data')
            (client/old['path']).unlink()
            destination=client/target;destination.parent.mkdir(parents=True,exist_ok=True);destination.write_bytes(raw)
            row['mode']=oct(destination.stat().st_mode&0o777);old.clear();old.update(row)
    manifest.update(world=world,gameRoot=str(game),gameCommit=capacity.GAME_BASE,
                    frozenEngineBase=capacity.ENGINE_BASE,gameSourceHashes=world+catalogs,
                    worldSelection=selection(fingerprint(world),fingerprint(catalogs)))
    manifest['appCodeIdentity']['informationalVersion']='0.1.0+'+capacity.GAME_BASE
    with patch.object(capacity,'WORLD_INPUT_SHA256',fingerprint(world),create=True), \
         patch.object(capacity,'CATALOG_INPUT_SHA256',fingerprint(catalogs),create=True):
        yield manifest


@contextlib.contextmanager
def specimen():
    with tempfile.TemporaryDirectory(prefix='khaozengine-world-inventory-',dir='/tmp') as temporary:
        game=Path(temporary).resolve()
        with install_world(preparation(),game) as manifest:
            yield manifest,game


class WorldInventoryTests(unittest.TestCase):
    def test_complete_directory_and_separate_catalogs_are_accepted(self):
        with specimen() as (manifest,_):
            self.assertEqual(REQUIRED_CLOSURE,collect.validate_preparation(manifest))
            self.assertEqual(manifest['worldSelection'],collect.verify_world(manifest))

    def test_missing_duplicate_replaced_or_changed_world_rows_are_refused(self):
        for change in ('missing','duplicate','replacement','hash'):
            with self.subTest(change=change),specimen() as (manifest,_):
                if change=='missing':manifest['world'].pop()
                elif change=='duplicate':manifest['world'][-1]=dict(manifest['world'][0])
                elif change=='replacement':manifest['world'][-1]['path']='assets/worlds/hollowmere/foreign.json'
                else:manifest['world'][-1]['sha256']='0'*64
                with self.assertRaises(ValueError):collect.validate_preparation(manifest)

    def test_catalogs_and_world_must_match_declared_sources_and_catalog_payload(self):
        for change in ('world-source','catalog-source','catalog-hash','catalog-payload'):
            with self.subTest(change=change),specimen() as (manifest,_):
                if change=='world-source':manifest['gameSourceHashes']=manifest['gameSourceHashes'][1:]
                elif change=='catalog-source':manifest['gameSourceHashes'].pop()
                elif change=='catalog-hash':manifest['gameSourceHashes'][-1]['sha256']='0'*64
                else:manifest['clientFiles'][-1]['sha256']='0'*64
                with self.assertRaises(ValueError):collect.validate_preparation(manifest)

    def test_working_world_inventory_and_catalog_bytes_are_verified(self):
        for change in ('extra','missing','world-bytes','catalog-bytes','symlink'):
            with self.subTest(change=change),specimen() as (manifest,game):
                target=game/manifest['world'][0]['path']
                if change=='extra':(target.parent/'extra.json').write_bytes(b'world')
                elif change=='missing':target.unlink()
                elif change=='world-bytes':target.write_bytes(b'changed')
                elif change=='catalog-bytes':(game/'assets/catalogs/ground.json').write_bytes(b'changed')
                else:
                    target.unlink();target.symlink_to(game/manifest['world'][1]['path'])
                with self.assertRaises((ValueError,OSError)):collect.verify_world(manifest)

    def test_offline_capacity_audit_requires_the_named_selection_and_exact_types(self):
        from test_capacity import capacity_fixture
        report,proof=capacity_fixture()
        proof['worldFilesRehashed']=40
        proof['worldSelection']=selection('e057497431deaf910db248d706c385e99898b0830a06fa8a9415d07dc5fa9707',
                                          '753413079a39bbfb68f96e911279585805db34666f67adf914628bac7ec890f2')
        self.assertEqual('valid',analyse(report,1,123,proof,expected_recording_slots=3)['status'])
        for field in ('schemaVersion','worldFiles','catalogFiles'):
            changed=copy.deepcopy(proof);changed['worldSelection'][field]=float(changed['worldSelection'][field])
            self.assertEqual('invalid',analyse(report,1,123,changed,expected_recording_slots=3)['status'],field)
        for field in ('worldSelection','worldFilesRehashed'):
            changed=copy.deepcopy(proof);del changed[field]
            self.assertEqual('invalid',analyse(report,1,123,changed,expected_recording_slots=3)['status'],field)

    def test_capacity_collection_cannot_fall_back_to_the_unidentified_legacy_world_rows(self):
        from test_capacity import capacity_workflow
        with capacity_workflow() as w:
            del w.manifest['worldSelection']
            w.manifest['world']=[dict(path=f'world/{index}',sha256=hashlib.sha256(b'world').hexdigest())
                                 for index in range(41)]
            (w.result.parent/'preparation.json').write_text(json.dumps(w.manifest))
            self.assertEqual(1,collect.main())
            self.assertEqual([],w.triggers)
            self.assertFalse(w.trigger.exists())

    def test_changed_world_inventory_after_capture_remains_failed_evidence(self):
        from test_capacity import capacity_workflow
        import archive
        with capacity_workflow() as w:
            emit=collect.time.sleep
            def change_after_emit(delay):
                emit(delay)
                (Path(w.manifest['gameRoot'])/'assets/worlds/hollowmere/extra.json').write_bytes(b'extra')
            with patch.object(collect.time,'sleep',side_effect=change_after_emit):
                self.assertEqual(1,collect.main())
            self.assertEqual(1,len(w.triggers))
            self.assertEqual('failed',archive.verify_bundle(w.archiveDestination)['measurementStatus'])


if __name__=='__main__':unittest.main()
