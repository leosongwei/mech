"""Mode registry: pick the rule set the server was started with."""

from __future__ import annotations

from .base import Mode
from .battle import Battle
from .range import Range

MODES = {"battle": Battle, "range": Range}
DEFAULT = "battle"


def get(name: str) -> Mode:
    return MODES.get(name, MODES[DEFAULT])()


__all__ = ["Mode", "MODES", "get"]
