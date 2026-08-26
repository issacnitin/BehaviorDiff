import asyncio

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


def test_classes_and_inheritance():
    assert exercise_breadth(0) == 60
    assert DerivedCalculator().adjust(2) == 6


def test_property_shape_is_not_invoked():
    assert exercise_breadth(1) == 61
    assert PropertyHazard(4).value == 4


def test_decorator():
    assert exercise_breadth(2) == 62
    assert make_decorated_total()(5) == 10


def test_generator():
    assert exercise_breadth(3) == 63
    assert list(generator_values()) == [1, 2]


def test_async():
    assert exercise_breadth(4) == 64
    assert asyncio.run(async_total()) == 7


def test_exceptions_dataclasses_and_collections():
    assert exercise_breadth(5) == 65
    try:
        escaping()
    except LookupError:
        pass
    assert collection_shapes()["rule"].code == "A"
    cycle = make_cycle()
    assert cycle[0] is cycle