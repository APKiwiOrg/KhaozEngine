import contextlib
import hashlib
import json
import os
from pathlib import Path
import stat
import unittest
from unittest.mock import patch
import collect
from test_capacity import capacity_workflow

VERIFY_SOURCES=collect.verify_sources


@contextlib.contextmanager
def refuse_blocking_fifo_reads(target):
    path_open=Path.open;descriptor_open=os.open;unsafe=[]
    def guarded_path(path,*args,**kwargs):
        if path==target:
            unsafe.append('Path.open')
            raise AssertionError('A blocking FIFO open would hang preflight')
        return path_open(path,*args,**kwargs)
    def guarded_descriptor(path,flags,*args,**kwargs):
        try:info=os.stat(path,dir_fd=kwargs.get('dir_fd'),follow_symlinks=False)
        except FileNotFoundError:info=None
        if info is not None and stat.S_ISFIFO(info.st_mode) and not flags&os.O_NONBLOCK:
            unsafe.append('os.open without O_NONBLOCK')
            raise AssertionError('A blocking FIFO descriptor open would hang preflight')
        return descriptor_open(path,flags,*args,**kwargs)
    with patch.object(Path,'open',new=guarded_path),patch.object(os,'open',side_effect=guarded_descriptor):
        yield unsafe


@contextlib.contextmanager
def real_source_workflow():
    with capacity_workflow() as w:
        engine=Path(w.manifest['probeRoot']);engine.mkdir()
        game=Path(w.manifest['gameRoot'])
        committed={}
        for row in w.manifest['sourceHashes']:
            path=engine/row['path'];path.parent.mkdir(parents=True,exist_ok=True)
            actual=Path(collect.__file__).parent/Path(row['path']).name
            raw=actual.read_bytes() if row['path'].startswith('scripts/mac-frame-ring-waits/') else b'engine'
            path.write_bytes(raw);row['sha256']=hashlib.sha256(raw).hexdigest()
            committed[(str(engine),row['path'])]=raw
        for row in w.manifest['gameSourceHashes']:
            committed[(str(game),row['path'])]=(game/row['path']).read_bytes()
        for row in w.manifest['replacements']:
            path=engine/'built'/(row['name']+'.dll');path.parent.mkdir(exist_ok=True)
            raw=(Path(w.manifest['client'])/(row['name']+'.dll')).read_bytes();path.write_bytes(raw)
            row.update(source=str(path),sha256=hashlib.sha256(raw).hexdigest())
        (w.result.parent/'preparation.json').write_text(json.dumps(w.manifest))
        def git_bytes(args,**options):
            if args[:2]!=['git','-C']:raise AssertionError('Unexpected process '+repr(args))
            if args[3:]==['rev-parse','HEAD']:raw=(w.manifest['probeCommit']+'\n').encode()
            elif args[3:]==['status','--porcelain=v1']:raw=b''
            elif args[3]=='show':raw=committed[(args[2],args[4].split(':',1)[1])]
            else:raise AssertionError('Unexpected git request '+repr(args))
            return raw.decode() if options.get('text') else raw
        with patch.object(collect,'verify_sources',side_effect=VERIFY_SOURCES) as source_step, \
             patch.object(collect.subprocess,'check_output',side_effect=git_bytes):
            w.source_step=source_step
            yield w


class SourceAdmissionTests(unittest.TestCase):
    def test_real_source_read_step_accepts_regular_committed_inputs(self):
        with real_source_workflow() as w:
            self.assertEqual(0,collect.main())
            self.assertEqual(2,w.source_step.call_count)
            self.assertEqual(1,len(w.triggers))

    def test_fifo_source_is_refused_without_a_blocking_open_before_world_validation(self):
        for relative in ('assets/worlds/hollowmere/world.json','assets/catalogs/ground.json'):
            with self.subTest(source=relative),real_source_workflow() as w:
                target=Path(w.manifest['gameRoot'])/relative
                target.unlink();os.mkfifo(target)
                with refuse_blocking_fifo_reads(target) as unsafe:
                    self.assertEqual(1,collect.main())
                self.assertEqual(1,w.source_step.call_count)
                self.assertEqual([],unsafe)
                self.assertEqual([],w.triggers)
                self.assertIn('regular',json.loads(w.metadata.read_text())['error'])

    def test_fifo_preparation_and_startup_inputs_are_refused_without_blocking(self):
        for name in ('preparation.json','process.log'):
            with self.subTest(input=name),capacity_workflow() as w:
                target=w.result.parent/name
                target.unlink();os.mkfifo(target)
                with refuse_blocking_fifo_reads(target) as unsafe:
                    self.assertIn(collect.main(),(1,3))
                self.assertEqual([],unsafe)
                self.assertEqual([],w.triggers)


if __name__=='__main__':unittest.main()
