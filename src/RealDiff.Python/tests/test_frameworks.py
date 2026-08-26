import json
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path


TRACER_ROOT = Path(__file__).resolve().parents[1]


class FrameworkCorrelationTests(unittest.TestCase):
    def test_pytest_root_count_matches_runner(self):
        source = """
            def child(value):
                return value + 1

            def test_one():
                assert child(1) == 2

            def test_two():
                assert child(2) == 3
        """
        output, events, manifest = self._run("pytest", source)

        self.assertIn("2 passed", output)
        self._assert_two_roots_and_correlated_children(events, manifest)

    def test_unittest_root_count_matches_runner(self):
        source = """
            import unittest

            def child(value):
                return value + 1

            class ExampleTests(unittest.TestCase):
                def test_one(self):
                    self.assertEqual(2, child(1))

                def test_two(self):
                    self.assertEqual(3, child(2))

            if __name__ == "__main__":
                unittest.main()
        """
        output, events, manifest = self._run("unittest", source)

        self.assertIn("Ran 2 tests", output)
        self._assert_two_roots_and_correlated_children(events, manifest)

    def _run(self, framework, source):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "test_sample.py").write_text(textwrap.dedent(source), encoding="utf-8")
            trace = root / "trace" / "run.ndjson"
            environment = os.environ.copy()
            environment["PYTHONPATH"] = str(TRACER_ROOT)
            environment["REALDIFF_TRACE"] = str(trace)
            environment["REALDIFF_REPOSITORY_ROOT"] = str(root)
            command = (
                [sys.executable, "-m", "pytest", "-q", str(root / "test_sample.py")]
                if framework == "pytest"
                else [sys.executable, "-m", "unittest", "discover", "-s", str(root), "-v"]
            )
            result = subprocess.run(command, env=environment, text=True, capture_output=True, check=False)
            output = result.stdout + result.stderr
            self.assertEqual(0, result.returncode, output)
            events = [json.loads(line) for line in trace.read_text(encoding="utf-8").splitlines()]
            manifest_path = trace.with_name("run.manifest.ndjson")
            manifest = [json.loads(line) for line in manifest_path.read_text(encoding="utf-8").splitlines()]
            return output, events, manifest

    def _assert_two_roots_and_correlated_children(self, events, manifest):
        roots = [event for event in events if event.get("isHarness")]
        children = [event for event in events if event["methodFullName"].endswith("::child")]
        self.assertEqual(2, len(roots))
        self.assertEqual(2, len(children))
        self.assertNotIn("(no-test)", [root["testId"] for root in roots])
        self.assertEqual(sorted(root["testId"] for root in roots), sorted(child["testId"] for child in children))
        root_ids = {root["callId"] for root in roots}
        self.assertTrue(all(child["parentCallId"] in root_ids for child in children))
        members = [record for record in manifest if record.get("kind") == "member"]
        self.assertEqual(2, sum(record.get("isTestRoot", False) for record in members))


if __name__ == "__main__":
    unittest.main()