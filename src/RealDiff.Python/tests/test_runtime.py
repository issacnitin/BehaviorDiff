import json
import tempfile
import unittest
from pathlib import Path

from realdiff_python.monitor import Scope, install, uninstall
from realdiff_python.runtime import Runtime


class RuntimeTests(unittest.TestCase):
    def tearDown(self):
        uninstall()

    def test_emits_normal_and_escaping_completion_once(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            trace = root / "run.ndjson"
            runtime = Runtime(trace, Scope(root, (), ()))
            install(runtime.on_event, runtime.scope)

            def completed():
                return 7

            def escaping():
                raise ValueError("expected")

            completed.__code__ = completed.__code__.replace(co_filename=str(root / "subject.py"))
            escaping.__code__ = escaping.__code__.replace(co_filename=str(root / "subject.py"))
            self.assertEqual(7, completed())
            with self.assertRaises(ValueError):
                escaping()
            uninstall()
            runtime.close()

            events = [json.loads(line) for line in trace.read_text(encoding="utf-8").splitlines()]
            self.assertEqual(2, len(events))
            self.assertNotIn("exceptionType", events[0])
            self.assertIn("argsDigest", events[0])
            self.assertIn("returnDigest", events[0])
            self.assertEqual("builtins.ValueError", events[1]["exceptionType"])
            self.assertNotIn("returnDigest", events[1])
            self.assertEqual([0, 0], [event["ordinal"] for event in events])

            manifest = [
                json.loads(line)
                for line in trace.with_name("run.manifest.ndjson").read_text(encoding="utf-8").splitlines()
            ]
            writer = manifest[-1]
            self.assertEqual(2, writer["written"])
            self.assertEqual(writer["written"], writer["enqueued"])
            module = next(record for record in manifest if record["kind"] == "assembly")
            self.assertEqual(module["discoveredMembers"], module["patchedMembers"] + module["skippedMembers"])
            digest = next(record for record in manifest if record["kind"] == "digest")
            self.assertGreater(digest["valuesDigested"], 0)


if __name__ == "__main__":
    unittest.main()