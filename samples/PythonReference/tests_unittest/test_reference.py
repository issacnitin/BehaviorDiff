import asyncio
import unittest

from python_reference import (
    DerivedCalculator,
    PropertyHazard,
    async_total,
    collection_shapes,
    make_decorated_total,
    escaping,
    exercise_breadth,
    generator_values,
    make_cycle,
)


class ReferenceTests(unittest.TestCase):
    def test_classes_and_inheritance(self):
        self.assertEqual(60, exercise_breadth(0))
        self.assertEqual(6, DerivedCalculator().adjust(2))

    def test_property_shape_is_not_invoked(self):
        self.assertEqual(61, exercise_breadth(1))
        self.assertEqual(4, PropertyHazard(4).value)

    def test_decorator(self):
        self.assertEqual(62, exercise_breadth(2))
        self.assertEqual(10, make_decorated_total()(5))

    def test_generator(self):
        self.assertEqual(63, exercise_breadth(3))
        self.assertEqual([1, 2], list(generator_values()))

    def test_async(self):
        self.assertEqual(64, exercise_breadth(4))
        self.assertEqual(7, asyncio.run(async_total()))

    def test_exceptions_dataclasses_and_collections(self):
        self.assertEqual(65, exercise_breadth(5))
        with self.assertRaises(LookupError):
            escaping()
        self.assertEqual("A", collection_shapes()["rule"].code)
        cycle = make_cycle()
        self.assertIs(cycle, cycle[0])


if __name__ == "__main__":
    unittest.main()