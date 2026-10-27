import hashlib
import json
from pathlib import Path
import unittest
from unittest.mock import patch
import archive
import collect
from test_capacity import capacity_workflow


class CollectionArchiveTests(unittest.TestCase):
    def test_success_prints_only_after_same_command_durable_publication(self):
        with capacity_workflow() as w:
            order=[]
            publish=archive.publish_bundle
            def record(**kwargs):
                order.append('archive');return publish(**kwargs)
            with patch.object(archive,'publish_bundle',side_effect=record),patch('builtins.print') as output:
                self.assertEqual(0,collect.main())
                self.assertEqual(['archive'],order)
                outcome=json.loads(output.call_args.args[0])
                self.assertEqual('archived',outcome['archive']['archivalStatus'])
            manifest=archive.verify_bundle(w.archiveDestination)
            self.assertEqual('valid',manifest['measurementStatus'])
            self.assertEqual([],manifest['missing'])
            self.assertEqual(w.result.read_bytes(),(w.archiveDestination/'result.json').read_bytes())
            self.assertEqual(w.metadata.read_bytes(),(w.archiveDestination/'collection.json').read_bytes())
            self.assertEqual(w.selectedLine,(w.archiveDestination/'startup-proof.log').read_bytes())
            self.assertNotIn(b'private unrelated',b''.join(p.read_bytes() for p in w.archiveDestination.iterdir()))
            self.assertEqual(1,len(w.triggers))

    def test_archival_failure_has_distinct_exit_and_archive_only_recovery_uses_original_hashes(self):
        with capacity_workflow(6) as w:
            with patch.object(archive,'publish_bundle',side_effect=OSError('synthetic archive fault')), \
                 patch('builtins.print') as output:
                self.assertEqual(3,collect.main())
                outcome=json.loads(output.call_args.args[0])
                self.assertEqual('captured-but-not-archived',outcome['status'])
                self.assertEqual('valid',outcome['measurementStatus'])
            self.assertEqual('collected-and-audited',json.loads(w.metadata.read_text())['status'])
            claim_path=w.result.with_suffix('.archive-claim.json')
            claim=json.loads(claim_path.read_text())
            before={p.name:p.read_bytes() for p in w.result.parent.iterdir() if p.is_file()}
            with patch.object(collect,'main',side_effect=AssertionError('recovery cannot collect')):
                receipt=archive.recover(claim)
            self.assertEqual('archived',receipt['archivalStatus'])
            self.assertEqual(before,{p.name:p.read_bytes() for p in w.result.parent.iterdir() if p.is_file()})
            self.assertEqual(1,len(w.triggers))
            self.assertEqual(hashlib.sha256((w.archiveDestination/'manifest.json').read_bytes()).hexdigest(),receipt['manifestSha256'])

    def test_audited_raw_disappearing_before_archive_cannot_be_reported_as_success(self):
        with capacity_workflow() as w:
            finish=collect.archive_outcome
            def remove_raw(*args):
                w.result.unlink()
                return finish(*args)
            with patch.object(collect,'archive_outcome',side_effect=remove_raw),patch('builtins.print') as output:
                self.assertEqual(3,collect.main())
                self.assertEqual('captured-but-not-archived',json.loads(output.call_args.args[0])['status'])
            self.assertFalse(w.archiveDestination.exists())
            self.assertEqual(1,len(w.triggers))

    def test_limited_run_is_archived_with_limited_measurement_status(self):
        with capacity_workflow(gap=1) as w:
            self.assertEqual(2,collect.main())
            self.assertEqual('limited',archive.verify_bundle(w.archiveDestination)['measurementStatus'])
            self.assertEqual(1,len(w.triggers))

    def test_failed_capture_archives_available_evidence_without_fabricating_raw_or_analysis(self):
        with capacity_workflow() as w:
            with patch.object(collect,'foreground',return_value={'pid':999}):
                self.assertEqual(1,collect.main())
            manifest=archive.verify_bundle(w.archiveDestination)
            self.assertEqual('failed',manifest['measurementStatus'])
            self.assertEqual(['analysis','raw'],manifest['missing'])
            self.assertEqual([],w.triggers)
            self.assertFalse(w.trigger.exists())

    def test_interruption_archives_failure_and_preserves_the_unconsumed_trigger(self):
        with capacity_workflow() as w:
            with patch.object(collect.time,'sleep',side_effect=KeyboardInterrupt):
                self.assertEqual(1,collect.main())
            self.assertTrue(w.trigger.exists())
            self.assertEqual('failed',archive.verify_bundle(w.archiveDestination)['measurementStatus'])
            self.assertFalse(json.loads(w.metadata.read_text())['triggerConsumed'])


if __name__=='__main__':unittest.main()
