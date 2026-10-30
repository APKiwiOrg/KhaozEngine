import copy
import contextlib
import hashlib
import json
import sys
from types import SimpleNamespace
from unittest.mock import patch
import collect
from test_analyze import fixture
import tempfile
import unittest
from pathlib import Path
from collect import canonical_owned, validate_preparation
from analyze import REQUIRED_CLOSURE

EDGES = {'KhaozEngine.Gpu': set(), 'KhaozEngine.Gpu.Metal': {'KhaozEngine.Gpu'},
         'KhaozEngine.Windowing': {'KhaozEngine.Gpu','KhaozEngine.Gpu.Metal'},
         'KhaozEngine.Render3D': {'KhaozEngine.Gpu','KhaozEngine.Windowing'},
         'KhaozEngine.Game.Render3D': {'KhaozEngine.Render3D','KhaozEngine.Windowing'}}


def preparation():
    return dict(status='prepared-and-audited',probeCommit='a'*40,gameCommit='b'*40,
                runtimeFileCount=644,contentFileCount=395,world=[dict(path='world/'+str(i),sha256='c'*64) for i in range(41)],
                client='/tmp/ring-owned/client',gameRoot='/tmp/game',probeRoot='/tmp/engine',
                clientFiles=[dict(path=x+'.dll',sha256='d'*64,bytes=1,mode='0o644') for x in sorted(REQUIRED_CLOSURE|{'Grimhollow'})],
                replacements=[dict(name=x,sha256='d'*64,assemblyVersion='21.13.0.0',fileVersion='21.13.0.0',
                informationalVersion='21.13.0+'+'a'*40,mvid='3cd633b7-e5ae-4be4-9250-0eb287ed4473',
                references=[dict(name=n,version='21.13.0.0') for n in sorted(EDGES[x]|{'KhaozEngine.Primitives'})]) for x in sorted(REQUIRED_CLOSURE)],
                sourceHashes=[dict(path='KhaozEngine.Gpu/GpuRecordingWaitDiagnostics.cs',sha256='e'*64)],
                appCodeIdentity=dict(name='Grimhollow',mvid='4fbb5a6f-f35c-4335-b951-481cd437cd2a',informationalVersion='0.1.0+'+'b'*40),
                engineCodeIdentity=dict(name='KhaozEngine.Windowing',mvid='3cd633b7-e5ae-4be4-9250-0eb287ed4473',informationalVersion='21.13.0+'+'a'*40))


class CollectorTests(unittest.TestCase):
    def test_required_runtime_dependency_edges_cannot_be_omitted(self):
        for name in sorted(REQUIRED_CLOSURE-{'KhaozEngine.Gpu'}):
            with self.subTest(name=name):
                m=preparation()
                next(x for x in m['replacements'] if x['name']==name)['references']=[]
                with self.assertRaises(ValueError):validate_preparation(m)

    def test_preparation_integer_fields_reject_coercion_and_range_errors(self):
        for field in ('runtimeFileCount','contentFileCount'):
            for replacement in (float(preparation()[field]),True,1<<31):
                with self.subTest(field=field,replacement=replacement):
                    m=preparation();m[field]=replacement
                    with self.assertRaises(ValueError):validate_preparation(json.loads(json.dumps(m)))
        for replacement in (1.0,True,-1,1<<63):
            with self.subTest(bytes=replacement):
                m=preparation();m['clientFiles'][0]['bytes']=replacement
                with self.assertRaises(ValueError):validate_preparation(json.loads(json.dumps(m)))

    def test_tmp_alias_is_canonical_without_allowing_escape(self):
        with tempfile.TemporaryDirectory(prefix="khaozengine-ring-wait-collector-", dir="/tmp") as directory:
            root=Path(directory)
            self.assertEqual((root/'result.json').resolve(),canonical_owned(root/'result.json',root))
            with self.assertRaises(ValueError): canonical_owned(root.parent/'outside.json',root)
            (root/'escape').symlink_to(root.parent,target_is_directory=True)
            with self.assertRaises(ValueError): canonical_owned(root/'escape'/'outside.json',root)

    def test_exact_five_replacements_are_mandatory(self):
        # Shape-only validation does not claim this small synthetic payload contains 644 files.
        m=preparation()
        self.assertEqual(REQUIRED_CLOSURE,validate_preparation(m))
        for corrupt in ('missing','extra','duplicate','version','reference','commit','world','identity','source','escape'):
            with self.subTest(corrupt=corrupt):
                m=preparation()
                if corrupt=='missing':m['replacements'].pop()
                elif corrupt=='extra':m['replacements'].append(dict(m['replacements'][0],name='KhaozEngine.Render2D'))
                elif corrupt=='duplicate':m['replacements'][-1]=m['replacements'][0]
                elif corrupt=='version':m['replacements'][0]['assemblyVersion']='21.14.0.0'
                elif corrupt=='reference':m['replacements'][0]['references'][0]['version']='21.12.0.0'
                elif corrupt=='commit':m['replacements'][0]['informationalVersion']='21.13.0+'+'0'*40
                elif corrupt=='world':m['world'].pop()
                elif corrupt=='identity':m['engineCodeIdentity']['mvid']='00000000-0000-0000-0000-000000000000'
                elif corrupt=='source':m['sourceHashes']=[]
                elif corrupt=='escape':m['clientFiles'][0]['path']='../foreign.dll'
                with self.assertRaises(ValueError):validate_preparation(m)

class CollectorWorkflowTests(unittest.TestCase):
    @contextlib.contextmanager
    def workflow(self):
        with tempfile.TemporaryDirectory(prefix='khaozengine-ring-wait-workflow-',dir='/tmp') as directory:
            root=Path(directory).resolve();client=root/'client';client.mkdir()
            game=root/'game';game.mkdir()
            m=preparation();r,_=fixture()
            m.update(client=str(client),gameRoot=str(game),probeRoot=str(root/'engine'),
                     appCodeIdentity=r['appCodeIdentity'],engineCodeIdentity=r['engineCodeIdentity'])
            for x in m['replacements']:x['mvid']=r['engineCodeIdentity']['mvid']
            m['clientFiles']=[]
            names=[x+'.dll' for x in sorted(REQUIRED_CLOSURE|{'Grimhollow'})]
            names += ['Content/'+str(i)+'.data' for i in range(395)]
            names += ['runtime/'+str(i)+'.data' for i in range(644-len(names))]
            for i,name in enumerate(names):
                p=client/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(str(i).encode())
                m['clientFiles'].append(dict(path=name,sha256=hashlib.sha256(p.read_bytes()).hexdigest(),bytes=p.stat().st_size,mode=oct(p.stat().st_mode&0o777)))
            for x in m['world']:
                p=game/x['path'];p.parent.mkdir(exist_ok=True);p.write_bytes(b'world');x['sha256']=hashlib.sha256(b'world').hexdigest()
            preparation_path=root/'preparation.json';preparation_path.write_text(json.dumps(m))
            result=root/'result.json';trigger=Path(str(result)+'.start')
            metadata=result.with_suffix('.collection.json')
            identity='123 Tue Oct 27 12:00:00 2026 dotnet '+str(client/'Grimhollow.dll')+' --with-server'
            mapped=[str(client/(x+'.dll')) for x in sorted(REQUIRED_CLOSURE|{'Grimhollow'})]
            triggers=[]
            def emit(_):
                self.assertTrue(trigger.is_file());triggers.append(trigger.stat().st_ino)
                trigger.unlink()  # Fake the recorder's Waiting -> Armed consumption, before publication.
                result.write_text(json.dumps(r))
            argv=['collect.py','--preparation',str(preparation_path),'--ready-process-id','123','--mode','counts','--result',str(result)]
            with patch.object(sys,'argv',argv),patch.object(collect,'identity',return_value=identity), \
                 patch.object(collect,'mappings',return_value=mapped),patch.object(collect,'foreground',return_value={'pid':123}), \
                 patch.object(collect,'verify_sources',return_value=m['sourceHashes']),patch.object(collect.time,'sleep',side_effect=emit):
                yield SimpleNamespace(result=result,trigger=trigger,metadata=metadata,report=r,manifest=m,triggers=triggers)

    def test_one_trigger_and_complete_before_after_audit_without_a_real_process(self):
        with self.workflow() as w:
            self.assertEqual(0,collect.main())
            self.assertEqual(1,len(w.triggers))
            with self.assertRaises(ValueError):collect.main()
            metadata=json.loads(w.metadata.read_text())
            self.assertEqual('collected-and-audited',metadata['status'])
            self.assertEqual(644,len(metadata['payloadBefore']))
            self.assertEqual(metadata['payloadBefore'],metadata['payloadAfter'])
            self.assertEqual(6,len(metadata['runtimeFiles']))
            self.assertTrue(metadata['triggerCreated'])
            self.assertTrue(metadata['ownershipReserved'])
            self.assertTrue(metadata['preflightRechecked'])
            self.assertTrue(metadata['triggerConsumed'])
            self.assertEqual(w.triggers[0],metadata['triggerIdentity'][1])
            self.assertLessEqual(metadata['triggeredAtUtc'],metadata['triggerConsumedAtUtc'])
            self.assertLessEqual(metadata['triggerConsumedAtUtc'],metadata['resultObservedAtUtc'])

    def test_report_from_an_older_window_does_not_consume_this_trigger(self):
        with self.workflow() as w:
            def older_window(_):w.result.write_text(json.dumps(w.report))
            with patch.object(collect.time,'sleep',side_effect=older_window):
                self.assertEqual(1,collect.main())
            self.assertTrue(w.trigger.is_file())
            self.assertEqual(w.report,json.loads(w.result.read_text()))
            metadata=json.loads(w.metadata.read_text())
            self.assertEqual('failed-retain-evidence',metadata['status'])
            self.assertFalse(metadata['triggerConsumed'])
            self.assertIn('consum',metadata['error'])
            self.assertFalse(w.result.with_suffix('.audit.json').exists())

    def test_result_published_during_preflight_is_refused_before_trigger_creation(self):
        with self.workflow() as w:
            def preflight(_):
                w.result.write_text(json.dumps(w.report))
                return w.manifest['sourceHashes']
            with patch.object(collect,'verify_sources',side_effect=preflight):
                self.assertEqual(1,collect.main())
            self.assertFalse(w.trigger.exists())
            self.assertEqual(w.report,json.loads(w.result.read_text()))
            self.assertFalse(json.loads(w.metadata.read_text())['triggerCreated'])

    def test_competitor_during_preflight_cannot_overwrite_the_owners_metadata(self):
        with self.workflow() as w:
            def compete(_):
                before=w.metadata.read_bytes()
                with self.assertRaises((ValueError,FileExistsError)):collect.main()
                self.assertEqual(before,w.metadata.read_bytes())
                self.assertFalse(w.trigger.exists())
                return w.manifest['sourceHashes']
            # One nested invocation at a fixed preflight checkpoint, no concurrent workers.
            calls=[]
            def sources(manifest):
                if not calls:
                    calls.append(True)
                    return compete(manifest)
                return w.manifest['sourceHashes']
            with patch.object(collect,'verify_sources',side_effect=sources):
                self.assertEqual(0,collect.main())
            self.assertEqual(1,len(w.triggers))

    def test_collectors_that_both_pass_freshness_have_one_atomic_claim_winner(self):
        with self.workflow() as w:
            open_file=Path.open
            winner=[]
            def interleave(path,mode='r',*args,**kwargs):
                if path==w.metadata and mode=='x' and not winner:
                    winner.append(None)
                    self.assertEqual(0,collect.main())
                    winner[0]=(w.metadata.read_bytes(),w.result.read_bytes())
                return open_file(path,mode,*args,**kwargs)
            # Suspend the first invocation just before its atomic claim. A second wins,
            # consumes its trigger and publishes before the first resumes. No threads.
            with patch.object(Path,'open',new=interleave):
                with self.assertRaises(FileExistsError):collect.main()
            self.assertEqual(winner[0],(w.metadata.read_bytes(),w.result.read_bytes()))
            self.assertEqual(1,len(w.triggers))
            self.assertFalse(w.trigger.exists())

    def test_interruption_during_preflight_leaves_a_non_overwritable_claim(self):
        with self.workflow() as w:
            with patch.object(collect,'verify_sources',side_effect=KeyboardInterrupt):
                with self.assertRaises(KeyboardInterrupt):collect.main()
            before=w.metadata.read_bytes()
            self.assertEqual('preflight',json.loads(before)['status'])
            with self.assertRaises((ValueError,FileExistsError)):collect.main()
            self.assertEqual(before,w.metadata.read_bytes())
            self.assertFalse(w.trigger.exists())

    def test_interruption_after_trigger_creation_retains_claim_and_unconsumed_trigger(self):
        with self.workflow() as w:
            with patch.object(collect,'now',side_effect=['2026-10-27T00:00:00+00:00',KeyboardInterrupt]):
                with self.assertRaises(KeyboardInterrupt):collect.main()
            self.assertTrue(w.trigger.is_file())
            before=w.metadata.read_bytes()
            with self.assertRaises((ValueError,FileExistsError)):collect.main()
            self.assertEqual(before,w.metadata.read_bytes())
            self.assertTrue(w.trigger.is_file())


if __name__=='__main__':
    unittest.main()
