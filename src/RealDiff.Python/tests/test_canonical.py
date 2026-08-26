import math
import unittest

from realdiff_python.canonical import Canonicalizer, DigestStatistics, MISSING


class CanonicalizerTests(unittest.TestCase):
    def test_none_missing_nan_and_negative_zero_are_distinct_and_stable(self):
        canonicalizer = Canonicalizer()
        values = [None, MISSING, float("nan"), -0.0, 0.0]
        digests = [canonicalizer.digest(value).digest for value in values]

        self.assertEqual(5, len(set(digests)))
        self.assertEqual(canonicalizer.digest(float("nan")).digest, canonicalizer.digest(math.nan).digest)

    def test_cycles_and_shared_references_are_deterministic(self):
        canonicalizer = Canonicalizer()
        cycle = []
        cycle.append(cycle)
        shared = []
        with_shared = [shared, shared]
        with_copies = [[], []]

        self.assertEqual(canonicalizer.digest(cycle).digest, Canonicalizer().digest(cycle).digest)
        self.assertNotEqual(Canonicalizer().digest(with_shared).digest, Canonicalizer().digest(with_copies).digest)

    def test_properties_and_attribute_overrides_are_not_invoked(self):
        calls = []

        class Hazard:
            def __getattribute__(self, name):
                if name != "__dict__":
                    calls.append(name)
                return object.__getattribute__(self, name)

            @property
            def dangerous(self):
                calls.append("property")
                raise AssertionError("property executed")

        value = Hazard()
        value.visible = 3
        result = Canonicalizer().digest(value)

        self.assertEqual([], calls)
        self.assertTrue(result.partial)
        self.assertIn("visible", result.rendered)
        self.assertIn("<skipped:Python:PropertyDescriptor>", result.rendered)
        self.assertIn("<skipped:Python:GetAttributeOverride>", result.rendered)

    def test_exact_builtin_collections_do_not_call_subclass_iteration(self):
        class DangerousList(list):
            def __iter__(self):
                raise AssertionError("iteration executed")

        exact = Canonicalizer().digest([1, 2, 3])
        partial = Canonicalizer().digest(DangerousList([1, 2, 3]))

        self.assertFalse(exact.partial)
        self.assertTrue(partial.partial)
        self.assertIn("<skipped:Python:ContainerSubclass>", partial.rendered)

    def test_name_redaction_does_not_change_digest(self):
        canonicalizer = Canonicalizer()
        secret = canonicalizer.digest("one", name="api_token")
        changed = canonicalizer.digest("two", name="api_token")

        self.assertEqual("<redacted>", secret.rendered)
        self.assertNotEqual(secret.digest, changed.digest)

    def test_depth_and_render_limits_are_counted_partial_markers(self):
        statistics = DigestStatistics()
        canonicalizer = Canonicalizer(statistics=statistics, max_depth=1, render_limit=12)
        result = canonicalizer.digest([[["long value"]]])

        self.assertTrue(result.partial)
        self.assertGreater(statistics.depth_limited, 0)
        self.assertEqual(1, statistics.rendered_truncated)


if __name__ == "__main__":
    unittest.main()