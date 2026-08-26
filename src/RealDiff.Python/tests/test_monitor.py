import os
import sys
import tempfile
import unittest
from pathlib import Path

from realdiff_python.monitor import Monitor, Scope, install, uninstall


class ScopeTests(unittest.TestCase):
    def test_scope_uses_segment_prefixes_and_exclude_wins(self):
        with tempfile.TemporaryDirectory() as root:
            scope = Scope(Path(root), (("src",),), (("src", "generated"),))
            included = compile("pass", str(Path(root, "src", "app.py")), "exec")
            excluded = compile("pass", str(Path(root, "src", "generated", "app.py")), "exec")
            sibling = compile("pass", str(Path(root, "src-old", "app.py")), "exec")

            self.assertEqual("src/app.py", scope.relative_path(included))
            self.assertIsNone(scope.relative_path(excluded))
            self.assertIsNone(scope.relative_path(sibling))


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
        self.assertEqual(["start", "return"], [event[0] for event in events])


if __name__ == "__main__":
    unittest.main()