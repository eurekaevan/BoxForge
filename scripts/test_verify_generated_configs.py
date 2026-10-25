"""Regression tests for official-core verification process handling."""

import asyncio
import copy
import importlib.util
from pathlib import Path
import os
import signal
import sys
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location(
    "verify_generated_configs", Path(__file__).with_name("verify-generated-configs.py"))
VERIFY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFY)

DNS_SPEC = importlib.util.spec_from_file_location(
    "verify_dns_races", Path(__file__).with_name("verify-dns-races.py"))
DNS_VERIFY = importlib.util.module_from_spec(DNS_SPEC)
DNS_SPEC.loader.exec_module(DNS_VERIFY)


class VerificationProcessTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="boxforge-verifier-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def run_process(self, source, **arguments):
        return VERIFY.run_command([sys.executable, "-c", source], self.root, "test negative case", **arguments)

    def test_dns_race_verification_rejects_unsupported_inputs_before_launching_core(self):
        supported = {"strategy": "ipv4_only", "timeout": "3s", "rules": [
            {"action": "respond", "race": True, "query_type": ["A"], "ip_cidr": ["0.0.0.0/0"]}]}
        for field, value in (("strategy", None), ("timeout", "5s"), ("rules", [])):
            with self.subTest(field=field):
                dns = copy.deepcopy(supported)
                dns[field] = value
                with self.assertRaisesRegex(ValueError, "requires Ipv4Only.*ParallelFastest.*3s"):
                    asyncio.run(DNS_VERIFY.main(self.root / "must-not-be-started", {"dns": dns}))

    def test_success_preserves_stdout_and_stderr(self):
        output = self.run_process("import sys; print('stdout'); print('stderr', file=sys.stderr)")
        self.assertIn("stdout", output)
        self.assertIn("stderr", output)

    def test_expected_exit_and_diagnostic_are_accepted(self):
        output = self.run_process("import sys; print('unsupported flow: bad', file=sys.stderr); sys.exit(1)",
                                  expected_error="unsupported flow: bad")
        self.assertIn("unsupported flow: bad", output)

    def test_unrelated_error_cannot_satisfy_negative_case(self):
        with self.assertRaisesRegex(RuntimeError, "test negative case: expected exit 1") as error:
            self.run_process("import sys; print('permission denied', file=sys.stderr); sys.exit(1)",
                             expected_error="unsupported flow: bad")
        self.assertIn("permission denied", str(error.exception))

    @unittest.skipIf(os.name == "nt", "POSIX executable script")
    def test_rejection_verifier_rejects_unrelated_core_process_failure(self):
        generated = self.root / "generated"
        profiles = generated / "profiles/Linux"
        tls = generated / "tls/Linux"
        profiles.mkdir(parents=True)
        tls.mkdir(parents=True)
        (profiles / "config.json").write_text(
            '{"route":{"rules":[],"rule_set":[{"tag":["ai","cn"],'
            '"url":"https://example.com/{tag}.srs"}]}}')
        (tls / "config.json").write_text('{"outbounds":[{"type":"vless"}]}')
        core = self.root / "fake-core"
        core.write_text("#!/bin/sh\nprintf 'permission denied\\n' >&2\nexit 1\n")
        core.chmod(0o755)

        with self.assertRaisesRegex(RuntimeError, "Official core negative case invalid-ip-version") as error:
            VERIFY.verify_rejections(core, self.root, generated)
        self.assertIn("permission denied", str(error.exception))
        self.assertIn("invalid ip version: 5", str(error.exception))

    def test_negative_case_rejects_success_and_wrong_exit_status(self):
        for status in (0, 2):
            with self.subTest(status=status), self.assertRaisesRegex(RuntimeError, f"received exit {status}"):
                self.run_process(f"import sys; print('unsupported flow: bad'); sys.exit({status})",
                                 expected_error="unsupported flow: bad")

    def test_positive_case_rejects_failure_with_diagnostics(self):
        with self.assertRaisesRegex(RuntimeError, "test negative case: exited with status 1") as error:
            self.run_process("import sys; print('invalid config'); sys.exit(1)")
        self.assertIn("invalid config", str(error.exception))

    def test_timeout_fails_with_context_and_partial_diagnostics(self):
        with self.assertRaisesRegex(RuntimeError, "test negative case: timed out") as error:
            self.run_process("import time; print('startup diagnostic', flush=True); time.sleep(10)",
                             timeout=0.3, expected_error="startup diagnostic")
        self.assertIn("startup diagnostic", str(error.exception))

    @unittest.skipIf(os.name == "nt", "POSIX signal exit semantics")
    def test_signal_is_failure_even_with_expected_diagnostic(self):
        with self.assertRaisesRegex(RuntimeError, f"terminated by signal {signal.SIGTERM}"):
            self.run_process("import os, signal; print('unsupported flow: bad', flush=True); "
                             "os.kill(os.getpid(), signal.SIGTERM)", expected_error="unsupported flow: bad")

    def test_launch_failure_has_case_context(self):
        with self.assertRaisesRegex(RuntimeError, "missing command: could not start process"):
            VERIFY.run_command([str(self.root / "nonexistent-command")], self.root, "missing command")


if __name__ == "__main__":
    unittest.main()
