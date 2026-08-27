import os
import sys
import tempfile
import unittest
from pathlib import Path

from realdiff_python.monitor import Monitor, Scope, install, uninstall


class ScopeTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt", "Windows path semantics")
    def test_scope_canonicalizes_windows_drive_root(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            alias = root / "alias"
            alias.mkdir()
            scope = Scope(alias / "..", (), ())
            source = root / "src" / "app.py"
            code = compile("pass", str(source), "exec")

            self.assertTrue(scope.root.is_absolute())
            self.assertEqual(root.resolve().drive.casefold(), scope.root.drive.casefold())
            self.assertEqual("src/app.py", scope.relative_path(code))

    def test_scope_uses_segment_prefixes_and_exclude_wins(self):
        with tempfile.TemporaryDirectory() as root:
            scope = Scope(Path(root), (("src",),), (("src", "generated"),))
            included = compile("pass", str(Path(root, "src", "app.py")), "exec")
            excluded = compile("pass", str(Path(root, "src", "generated", "app.py")), "exec")
            sibling = compile("pass", str(Path(root, "src-old", "app.py")), "exec")

            self.assertEqual("src/app.py", scope.relative_path(included))
            self.assertIsNone(scope.relative_path(excluded))
            self.assertIsNone(scope.relative_path(sibling))

    def test_code_object_source_resolution_states(self):
        with tempfile.TemporaryDirectory() as root:
            scope = Scope(Path(root), (), ())
            exact = compile("pass", str(Path(root, "src", "app.py")), "exec")
            synthetic = compile("pass", "<generated>", "exec")
            external = compile("pass", str(Path(root).parent / "external.py"), "exec")

            self.assertEqual(("src/app.py", 1, "debugInfo"), scope.source_location(exact))
            self.assertEqual((None, 0, "debugInfoMissing"), scope.source_location(synthetic))
            self.assertEqual((None, 0, "unresolved"), scope.source_location(external))


@unittest.skipUnless(sys.version_info >= (3, 12), "requires sys.monitoring")
class MonitoringTests(unittest.TestCase):
    def tearDown(self):
        uninstall()

    def test_installs_monitor_and_scopes_callbacks_to_repository(self):
        events = []
        root = Path(__file__).resolve().parents[1]
        os.environ["REALDIFF_REPOSITORY_ROOT"] = str(root)
        os.environ["REALDIFF_INCLUDE_NAMESPACES"] = "tests"
        monitor = install(lambda event, code, value, frame: events.append((event, code.co_name, value)))

        def observed():
            return 42

        observed.__code__ = observed.__code__.replace(co_filename=str(Path(__file__).resolve()))
        self.assertEqual(42, observed())

        self.assertIsInstance(monitor, Monitor)
        self.assertIn("native_call", [event[0] for event in events])
        self.assertEqual(["start", "return"], [event[0] for event in events if event[0] != "native_call"])


if __name__ == "__main__":
    unittest.main()