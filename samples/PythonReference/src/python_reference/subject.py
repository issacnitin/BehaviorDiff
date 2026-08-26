import asyncio
from dataclasses import dataclass


@dataclass
class Rule:
    code: str
    priority: int


class BaseCalculator:
    def adjust(self, value):
        return value + 1


class DerivedCalculator(BaseCalculator):
    def adjust(self, value):
        return super().adjust(value) * 2


class PropertyHazard:
    def __init__(self, value):
        self.value = value

    @property
    def dangerous(self):
        raise AssertionError("canonicalization invoked a property")


def audited(function):
    def wrapper(*args, **kwargs):
        return function(*args, **kwargs)

    return wrapper


def make_decorated_total():
    @audited
    def decorated_total(value):
        return value + 5

    return decorated_total


def generator_values():
    yield 1
    yield 2
    return 3


async def async_total():
    await asyncio.sleep(0)
    return 7


def escaping():
    raise LookupError("expected")


def collection_shapes():
    return {
        "list": [1, 2],
        "tuple": (3, 4),
        "dict": {"b": 2, "a": 1},
        "set": {6, 5},
        "frozenset": frozenset({8, 7}),
        "rule": Rule("A", 10),
    }


def make_cycle():
    value = []
    value.append(value)
    return value