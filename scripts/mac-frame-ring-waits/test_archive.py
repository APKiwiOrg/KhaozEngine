import copy
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import archive


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='khaozengine-capacity-archive-',dir='/tmp')
        self.root = Path(self.temp.name).resolve()
        self.source = self.root/'source';self.source.mkdir()
        self.parent = self.root/'durable';self.parent.mkdir()
        self.destination = self.parent/'run'
        self.entries = {name: None for name in archive.ARTIFACTS}
        self.hashes = {}
        for index, name in enumerate(archive.ARTIFACTS):
            path=self.source/(name+'.data');path.write_bytes(bytes([index,0,255])+name.encode())
            self.entries[name]=path.name
            self.hashes[name]=dict(sha256=hashlib.sha256(path.read_bytes()).hexdigest(),bytes=path.stat().st_size)

    def tearDown(self): self.temp.cleanup()

    def publish(self,status='valid'):
        return archive.publish_bundle(source_root=self.source,destination=self.destination,entries=self.entries,
                                      measurement_status=status,expected_hashes=self.hashes)

    def test_exact_bytes_and_verified_inventory_with_external_receipt(self):
        receipt=self.publish();manifest=archive.verify_bundle(self.destination)
        self.assertEqual('archived',receipt['archivalStatus'])
        self.assertEqual('valid',receipt['measurementStatus'])
        self.assertEqual(hashlib.sha256((self.destination/'manifest.json').read_bytes()).hexdigest(),receipt['manifestSha256'])
        self.assertNotIn('receipt',manifest)
        for name, relative in self.entries.items():
            self.assertEqual((self.source/relative).read_bytes(),(self.destination/archive.ARTIFACTS[name]).read_bytes())
        self.assertEqual(set(archive.ARTIFACTS.values())|{'manifest.json'},{x.name for x in self.destination.iterdir()})

    def test_existing_destination_is_never_overwritten_but_exact_recovery_is_idempotent(self):
        first=self.publish();before={x.name:x.read_bytes() for x in self.destination.iterdir()}
        with self.assertRaises(FileExistsError):self.publish()
        claim=self.claim();receipt=archive.recover(claim)
        self.assertEqual(first['manifestSha256'],receipt['manifestSha256'])
        self.assertEqual('already-archived',receipt['archivalStatus'])
        self.assertEqual(before,{x.name:x.read_bytes() for x in self.destination.iterdir()})
        (self.destination/archive.ARTIFACTS['raw']).write_bytes(b'changed')
        with self.assertRaises(ValueError):archive.recover(claim)

    def test_racing_empty_destination_cannot_be_replaced(self):
        real=archive._publish_noreplace
        def race(parent,temporary,destination):
            os.mkdir(destination,dir_fd=parent)
            return real(parent,temporary,destination)
        with patch.object(archive,'_publish_noreplace',side_effect=race),self.assertRaises(FileExistsError):self.publish()
        self.assertTrue(self.destination.is_dir())
        self.assertEqual([],list(self.destination.iterdir()))
        self.assertTrue(all((self.source/x).is_file() for x in self.entries.values()))

    def test_parent_substituted_at_publication_cannot_return_a_false_receipt(self):
        real=archive._publish_noreplace
        def substitute(parent,temporary,destination):
            self.parent.rename(self.root/'moved-parent')
            self.parent.symlink_to(self.source,target_is_directory=True)
            return real(parent,temporary,destination)
        with patch.object(archive,'_publish_noreplace',side_effect=substitute),self.assertRaises((ValueError,OSError)):
            self.publish()
        self.assertTrue(self.parent.is_symlink())
        self.assertTrue(all((self.source/x).is_file() for x in self.entries.values()))

    def test_partial_copy_failure_retains_sources_and_never_publishes(self):
        real=archive._copy_entry;calls=[]
        def partial(*args):
            calls.append(1)
            if len(calls)==2:raise OSError('deterministic copy fault')
            return real(*args)
        with patch.object(archive,'_copy_entry',side_effect=partial),self.assertRaises(OSError):self.publish()
        self.assertFalse(self.destination.exists())
        self.assertEqual([],list(self.parent.iterdir()))
        self.assertEqual(len(archive.ARTIFACTS),len(list(self.source.iterdir())))

    def test_source_mutation_is_refused_before_or_during_copy(self):
        (self.source/self.entries['raw']).write_bytes(b'changed')
        with self.assertRaises(ValueError):self.publish()
        self.assertFalse(self.destination.exists())
        path=self.source/self.entries['raw'];path.write_bytes(b'original')
        self.hashes['raw']=dict(sha256=hashlib.sha256(b'original').hexdigest(),bytes=8)
        real=archive._copy_entry
        def mutate(*args):
            result=real(*args);path.write_bytes(b'mutated!');return result
        with patch.object(archive,'_copy_entry',side_effect=mutate),self.assertRaises(ValueError):self.publish()
        self.assertFalse(self.destination.exists())

    def test_paths_and_semantic_inventory_are_fail_closed(self):
        for bad in ('../outside','/absolute','a/../raw','.', '', 'a//b'):
            original=self.entries['raw'];self.entries['raw']=bad
            with self.subTest(path=bad),self.assertRaises((ValueError,OSError)):self.publish()
            self.entries['raw']=original
        self.entries['whole-log']='session.log'
        with self.assertRaises(ValueError):self.publish()

    def test_symlink_sources_parent_components_and_destination_are_refused(self):
        raw=self.source/self.entries['raw'];raw.unlink();raw.symlink_to(self.source/self.entries['analysis'])
        with self.assertRaises((ValueError,OSError)):self.publish()
        raw.unlink();raw.write_bytes(b'ok')
        self.hashes['raw']=dict(sha256=hashlib.sha256(b'ok').hexdigest(),bytes=2)
        alias=self.root/'alias';alias.symlink_to(self.source,target_is_directory=True)
        with self.assertRaises((ValueError,OSError)):
            archive.publish_bundle(source_root=alias,destination=self.destination,entries=self.entries,
                                   measurement_status='valid',expected_hashes=self.hashes)
        link=self.root/'durable-alias';link.symlink_to(self.parent,target_is_directory=True)
        self.destination=link/'run'
        with self.assertRaises((ValueError,OSError)):self.publish()
        self.destination=self.parent/'run';self.destination.symlink_to(self.source,target_is_directory=True)
        with self.assertRaises((ValueError,OSError)):self.publish()

    def test_substituted_source_component_is_refused(self):
        directory=self.source/'nested';directory.mkdir()
        (directory/'raw').write_bytes((self.source/self.entries['raw']).read_bytes())
        self.entries['raw']='nested/raw'
        real=archive._copy_entry
        def substitute(*args):
            directory.rename(self.source/'old');directory.symlink_to(self.source/'old',target_is_directory=True)
            return real(*args)
        with patch.object(archive,'_copy_entry',side_effect=substitute),self.assertRaises((ValueError,OSError)):self.publish()
        self.assertFalse(self.destination.exists())

    def test_manifest_or_unexpected_inventory_is_rejected(self):
        self.publish();manifest=self.destination/'manifest.json';original=manifest.read_bytes()
        (self.destination/'unexpected').write_text('no')
        with self.assertRaises(ValueError):archive.verify_bundle(self.destination)
        (self.destination/'unexpected').unlink()
        manifest.write_bytes(original.replace(b'"schemaVersion":1',b'"schemaVersion":2'))
        with self.assertRaises(ValueError):archive.verify_bundle(self.destination)
        manifest.write_bytes(original)
        path=self.destination/archive.ARTIFACTS['raw'];path.unlink();path.symlink_to(self.source/self.entries['raw'])
        with self.assertRaises((ValueError,OSError)):archive.verify_bundle(self.destination)

    def test_valid_measurement_cannot_publish_a_bundle_with_missing_artifacts(self):
        self.entries['raw']=None;self.hashes.pop('raw')
        with self.assertRaises(ValueError):self.publish('valid')
        self.assertFalse(self.destination.exists())

    def test_failed_and_limited_measurement_status_and_missing_artifacts_stay_explicit(self):
        for status in ('failed','limited'):
            self.destination=self.parent/status
            self.entries['raw']=None;self.hashes.pop('raw',None)
            receipt=self.publish(status)
            manifest=archive.verify_bundle(self.destination)
            self.assertEqual(status,receipt['measurementStatus'])
            self.assertEqual(status,manifest['measurementStatus'])
            self.assertEqual(['raw'],manifest['missing'])

    def claim(self,status="valid"):
        return dict(schemaVersion=1,sourceRoot=str(self.source),destination=str(self.destination),entries=self.entries,
                    expectedHashes=self.hashes,measurementStatus=status)

    def test_archive_only_recovery_never_imports_or_calls_the_collector(self):
        import sys
        with patch.dict(sys.modules,{'collect':None}):
            receipt=archive.recover(self.claim("limited"))
        self.assertEqual('limited',receipt['measurementStatus'])
        self.assertEqual([],list(self.source.glob('*.start')))
        self.assertTrue(all((self.source/x).exists() for x in self.entries.values()))

    def test_archive_only_cli_accepts_the_system_tmp_alias_without_collecting(self):
        import sys
        with patch.object(archive,'ARCHIVE_ROOT',self.parent):
            self.destination=archive.destination_for('a'*40,'synthetic')
            claim=self.claim('failed');claim.update(probeCommit='a'*40,runId='synthetic')
            claim_path=self.source/'capture.archive-claim.json';claim_path.write_text(json.dumps(claim))
            alias=Path(self.temp.name)/'source'/claim_path.name
            with patch.object(sys,'argv',['archive.py','--claim',str(alias)]),patch.dict(sys.modules,{'collect':None}):
                self.assertEqual(0,archive.main())
            self.assertEqual('failed',archive.verify_bundle(self.destination)['measurementStatus'])
            self.assertEqual([],list(self.source.glob('*.start')))

    def test_destination_components_are_exact_and_platform_without_atomic_primitive_is_refused(self):
        for commit,run in [('a'*39,'run'),('a'*40,'../run'),('a'*40,'/run'),('A'*40,'run')]:
            with self.assertRaises(ValueError):archive.destination_for(commit,run)
        with patch.object(archive.sys,'platform','unsupported'),self.assertRaises(NotImplementedError):self.publish()
        self.assertFalse(self.destination.exists())


if __name__=='__main__':unittest.main()
