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

    def test_registered_root_owns_descendant_subtree(self):
        with tempfile.TemporaryDirectory() as temporary:
            root_path = Path(temporary)
            trace = root_path / "run.ndjson"
            runtime = Runtime(trace, Scope(root_path, (), ()))
            install(runtime.on_event, runtime.scope)

            def child():
                return 4

            def test_root():
                return child()

            source = str(root_path / "test_subject.py")
            child.__code__ = child.__code__.replace(co_filename=source)
            test_root.__code__ = test_root.__code__.replace(co_filename=source)
            runtime.register_test_root(test_root.__code__, "tests/test_subject.py::test_root")
            self.assertEqual(4, test_root())
            runtime.clear_test_root()
            uninstall()
            runtime.close()

            events = [json.loads(line) for line in trace.read_text(encoding="utf-8").splitlines()]
            child_event = next(event for event in events if event["methodFullName"].endswith(child.__code__.co_qualname))
            root_event = next(event for event in events if event["methodFullName"].endswith(test_root.__code__.co_qualname))
            self.assertEqual("tests/test_subject.py::test_root", child_event["testId"])
            self.assertEqual(root_event["testId"], child_event["testId"])
            self.assertEqual(1, child_event["callDepth"])
            self.assertEqual(root_event["callId"], child_event["parentCallId"])
            self.assertTrue(root_event["isHarness"])
            self.assertNotIn("isHarness", child_event)

            manifest = [
                json.loads(line)
                for line in trace.with_name("run.manifest.ndjson").read_text(encoding="utf-8").splitlines()
            ]
            test_member = next(
                record for record in manifest if record.get("method", "").endswith(test_root.__code__.co_qualname)
            )
            self.assertTrue(test_member["isTestRoot"])

    def test_unittest_method_opens_test_extent(self):
        with tempfile.TemporaryDirectory() as temporary:
            root_path = Path(temporary)
            trace = root_path / "run.ndjson"
            runtime = Runtime(trace, Scope(root_path, (), ()))
            install(runtime.on_event, runtime.scope)

            class ExampleTests(unittest.TestCase):
                def test_value(self):
                    return 5

            ExampleTests.test_value.__code__ = ExampleTests.test_value.__code__.replace(
                co_filename=str(root_path / "test_example.py")
            )
            self.assertEqual(5, ExampleTests("test_value").test_value())
            uninstall()
            runtime.close()

            events = [json.loads(line) for line in trace.read_text(encoding="utf-8").splitlines()]
            event = next(item for item in events if item["methodFullName"].endswith("ExampleTests.test_value"))
            self.assertEqual(f"{ExampleTests.__module__}.{ExampleTests.__qualname__}.test_value", event["testId"])
            self.assertTrue(event["isHarness"])


if __name__ == "__main__":
    unittest.main()