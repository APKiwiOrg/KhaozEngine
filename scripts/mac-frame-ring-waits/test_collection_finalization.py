import contextlib
import hashlib
import json
from pathlib import Path
import unittest
from types import SimpleNamespace
from unittest.mock import patch
import archive
import collect
from analyze import analyse
from test_capacity import capacity_workflow


class CollectionFinalizationTests(unittest.TestCase):
    def test_archival_interruption_preserves_finalized_sources_and_recovery(self):
        for stage in ('publication', 'receipt'):
            with self.subTest(stage=stage), capacity_workflow() as w:
                write = archive._write
                def interrupt_receipt(directory, name, raw):
                    if name.endswith('.archive-receipt.json'):
                        raise KeyboardInterrupt('receipt interrupted')
                    return write(directory, name, raw)
                with contextlib.ExitStack() as patches:
                    if stage == 'publication':
                        patches.enter_context(patch.object(archive, 'publish_bundle', side_effect=KeyboardInterrupt))
                    else:
                        patches.enter_context(patch.object(archive, '_write', side_effect=interrupt_receipt))
                    output = patches.enter_context(patch('builtins.print'))
                    self.assertEqual(3, collect.main())
                    outcome = json.loads(output.call_args.args[0])
                    self.assertEqual('captured-but-not-archived', outcome['status'])
                    self.assertEqual('valid', outcome['measurementStatus'])
                claim = json.loads(w.result.with_suffix('.archive-claim.json').read_text())
                self.assertEqual('valid', claim['measurementStatus'])
                self.assertEqual('collected-and-audited', json.loads(w.metadata.read_text())['status'])
                self.assertEqual(claim['expectedHashes']['collection']['sha256'],
                                 hashlib.sha256(w.metadata.read_bytes()).hexdigest())
                before = {p.name:p.read_bytes() for p in w.result.parent.iterdir() if p.is_file()}
                with patch.object(collect, 'main', side_effect=AssertionError('recovery cannot collect')):
                    receipt = archive.recover(claim)
                self.assertEqual('archived' if stage == 'publication' else 'already-archived',
                                 receipt['archivalStatus'])
                self.assertEqual(before, {p.name:p.read_bytes() for p in w.result.parent.iterdir() if p.is_file()})
                self.assertEqual('valid', archive.verify_bundle(w.archiveDestination)['measurementStatus'])
                self.assertEqual(1, len(w.triggers))

    def test_substituted_finalized_metadata_cannot_be_published_as_valid(self):
        for replacement in (False, True):
            with self.subTest(replacement=replacement), capacity_workflow() as w:
                finish = collect.archive_outcome
                corrupted = []
                def substitute(*args, **kwargs):
                    value = json.loads(w.metadata.read_text())
                    value['triggerConsumed'] = False
                    raw = (json.dumps(value, indent=2)+'\n').encode()
                    if replacement:
                        alternate = w.metadata.with_suffix('.substituted')
                        alternate.write_bytes(raw)
                        alternate.replace(w.metadata)
                    else:
                        w.metadata.write_bytes(raw)
                    corrupted.append(raw)
                    return finish(*args, **kwargs)
                with patch.object(collect, 'archive_outcome', side_effect=substitute), \
                     patch('builtins.print') as output:
                    self.assertEqual(3, collect.main())
                    self.assertEqual('captured-but-not-archived', json.loads(output.call_args.args[0])['status'])
                self.assertEqual(corrupted[0], w.metadata.read_bytes())
                self.assertFalse(w.archiveDestination.exists())
                self.assertFalse(w.result.with_suffix('.archive-claim.json').exists())
                self.assertEqual(1, len(w.triggers))

    def test_final_metadata_replace_failure_preserves_a_recoverable_snapshot(self):
        for replaced in (False, True):
            with self.subTest(replaced=replaced), capacity_workflow() as w:
                replace = collect.os.replace
                injected = []
                def fail_final(source, destination):
                    raw = Path(source).read_bytes()
                    if json.loads(raw)['status'] == 'collected-and-audited' and not injected:
                        injected.append((raw, w.metadata.read_bytes()))
                        if replaced:
                            replace(source, destination)
                        raise OSError('original final metadata publication failure')
                    return replace(source, destination)
                with patch.object(collect.os, 'replace', side_effect=fail_final), patch('builtins.print') as output:
                    try:
                        code = collect.main()
                    except BaseException as error:
                        self.fail('Finalization escaped instead of retaining recovery evidence: '+repr(error))
                    self.assertEqual(3, code)
                    outcome = json.loads(output.call_args.args[0])
                    self.assertEqual('captured-but-not-archived', outcome['status'])
                    self.assertEqual('valid', outcome['measurementStatus'])
                    self.assertEqual('OSError', outcome['errorType'])
                    self.assertEqual('original final metadata publication failure', outcome['error'])
                self.assertEqual(1, len(injected))
                self.assertFalse(w.archiveDestination.exists())
                claim = json.loads(w.result.with_suffix('.archive-claim.json').read_text())
                expected_source = w.result.with_suffix('.collection.recovery.json')
                self.assertEqual(expected_source.name, claim['entries']['collection'])
                self.assertEqual(injected[0][0], expected_source.read_bytes())
                self.assertEqual(injected[0][0 if replaced else 1], w.metadata.read_bytes())
                if not replaced:
                    self.assertEqual(injected[0][0], w.metadata.with_suffix('.tmp').read_bytes())
                before = {p.name:p.read_bytes() for p in w.result.parent.iterdir() if p.is_file()}
                with patch.object(collect, 'main', side_effect=AssertionError('recovery cannot collect')):
                    self.assertEqual('archived', archive.recover(claim)['archivalStatus'])
                self.assertEqual(before, {p.name:p.read_bytes() for p in w.result.parent.iterdir() if p.is_file()})
                self.assertEqual('valid', archive.verify_bundle(w.archiveDestination)['measurementStatus'])
                proof = json.loads((w.archiveDestination/'collection.json').read_text())
                self.assertEqual('valid', analyse(w.report, 1, 123, proof, expected_recording_slots=3)['status'])
                self.assertEqual(1, len(w.triggers))

    def test_failed_capture_metadata_save_keeps_the_original_failure_and_recovery(self):
        with capacity_workflow() as w:
            replace = collect.os.replace
            injected = []
            def fail_error_save(source, destination):
                raw = Path(source).read_bytes()
                if json.loads(raw)['status'] == 'failed-retain-evidence':
                    injected.append(raw)
                    raise OSError('failure record publication failed')
                return replace(source, destination)
            with patch.object(collect, 'foreground', return_value={'pid':999}), \
                 patch.object(collect.os, 'replace', side_effect=fail_error_save), patch('builtins.print') as output:
                try:
                    code = collect.main()
                except BaseException as error:
                    self.fail('Failure metadata save escaped instead of retaining recovery evidence: '+repr(error))
                self.assertEqual(3, code)
                outcome = json.loads(output.call_args.args[0])
                self.assertEqual('failed-not-archived', outcome['status'])
                self.assertEqual('failed', outcome['measurementStatus'])
                self.assertEqual('failure record publication failed', outcome['error'])
            self.assertEqual(1, len(injected))
            claim = json.loads(w.result.with_suffix('.archive-claim.json').read_text())
            pending = Path(claim['sourceRoot'])/claim['entries']['collection']
            self.assertEqual(injected[0], pending.read_bytes())
            self.assertIn('not foreground', json.loads(pending.read_text())['error'])
            self.assertEqual('archived', archive.recover(claim)['archivalStatus'])
            self.assertEqual('failed', archive.verify_bundle(w.archiveDestination)['measurementStatus'])
            self.assertEqual([], w.triggers)

    def test_partial_final_metadata_write_preserves_the_fault_and_a_complete_recovery_copy(self):
        with capacity_workflow() as w:
            open_file = Path.open
            injected = []
            @contextlib.contextmanager
            def partial_open(path, mode='r', *args, **kwargs):
                with open_file(path, mode, *args, **kwargs) as stream:
                    if path == w.metadata.with_suffix('.tmp') and mode == 'xb':
                        def write(raw):
                            if json.loads(raw)['status'] == 'collected-and-audited' and not injected:
                                injected.append(raw)
                                stream.write(raw[:len(raw)//2])
                                raise OSError('partial final metadata write')
                            return stream.write(raw)
                        yield SimpleNamespace(write=write)
                    else:
                        yield stream
            with patch.object(Path, 'open', new=partial_open), patch('builtins.print') as output:
                try:
                    code = collect.main()
                except BaseException as error:
                    self.fail('Partial metadata write escaped instead of retaining evidence: '+repr(error))
                self.assertEqual(3, code)
                outcome = json.loads(output.call_args.args[0])
                self.assertEqual('captured-but-not-archived', outcome['status'])
                self.assertEqual('valid', outcome['measurementStatus'])
                self.assertEqual('partial final metadata write', outcome['error'])
            self.assertEqual(1, len(injected))
            self.assertEqual(injected[0][:len(injected[0])//2], w.metadata.with_suffix('.tmp').read_bytes())
            claim = json.loads(w.result.with_suffix('.archive-claim.json').read_text())
            self.assertEqual(injected[0], (Path(claim['sourceRoot'])/claim['entries']['collection']).read_bytes())
            self.assertEqual('archived', archive.recover(claim)['archivalStatus'])
            self.assertEqual('valid', archive.verify_bundle(w.archiveDestination)['measurementStatus'])
            self.assertEqual(1, len(w.triggers))


if __name__ == '__main__': unittest.main()
