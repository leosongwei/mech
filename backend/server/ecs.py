"""Minimal numpy-backed Entity Component System.

Entities are integer slots.  Every component is a dense numpy array of shape
``(capacity, *shape)`` so hot loops can be vectorised, and every entity carries
a 64 bit component mask used for fast queries.

Adding a new gameplay mechanic normally means:

    ecs.register("stamina", np.float32)          # a new component
    ...                                          # a new system module
    world.systems.append(stamina_system.run)

Queries are cheap (one vectorised mask test over the capacity) and systems can
work either on the returned id array directly (``arr[ids]``) or with numpy
operations over the whole component array.
"""

from __future__ import annotations

import numpy as np

MAX_COMPONENTS = 64


class ECS:
    def __init__(self, capacity: int = 8192):
        self.capacity = capacity
        self.alive = np.zeros(capacity, bool)
        self.masks = np.zeros(capacity, np.uint64)
        self.free = list(range(capacity - 1, -1, -1))
        self.comps: dict[str, np.ndarray] = {}
        self.defaults: dict[str, object] = {}
        self.bits: dict[str, int] = {}
        self._next_bit = 0
        self.destroyed: list[int] = []      # ids freed during the current tick

    # -- registration -----------------------------------------------------
    def register(self, name: str, dtype, shape=(), default=0):
        if self._next_bit >= MAX_COMPONENTS:
            raise RuntimeError("too many components")
        if shape:
            arr = np.zeros((self.capacity, *shape), dtype)
        else:
            arr = np.zeros(self.capacity, dtype)
        arr[...] = default
        self.comps[name] = arr
        self.defaults[name] = default
        self.bits[name] = 1 << self._next_bit
        self._next_bit += 1
        return arr

    def __getitem__(self, name: str) -> np.ndarray:
        return self.comps[name]

    def get(self, name: str) -> np.ndarray:
        return self.comps[name]

    # -- entity lifecycle -------------------------------------------------
    def create(self) -> int:
        if not self.free:
            raise RuntimeError("entity capacity exhausted")
        eid = self.free.pop()
        self.alive[eid] = True
        self.masks[eid] = 0
        for name, arr in self.comps.items():
            arr[eid] = self.defaults[name]
        return eid

    def destroy(self, eid: int):
        if not self.alive[eid]:
            return
        self.alive[eid] = False
        self.masks[eid] = 0
        for name, arr in self.comps.items():
            arr[eid] = self.defaults[name]
        self.free.append(eid)
        self.destroyed.append(eid)

    def add_comp(self, eid: int, name: str):
        self.masks[eid] |= np.uint64(self.bits[name])

    def remove_comp(self, eid: int, name: str):
        self.masks[eid] &= ~np.uint64(self.bits[name])

    def has(self, eid: int, *names: str) -> bool:
        m = 0
        for n in names:
            m |= self.bits[n]
        return bool(self.masks[eid] & np.uint64(m) == np.uint64(m))

    # -- queries ----------------------------------------------------------
    def query(self, *names: str) -> np.ndarray:
        """Return ids of alive entities that own all listed components."""
        if not names:
            return np.nonzero(self.alive)[0]
        m = 0
        for n in names:
            m |= self.bits[n]
        m = np.uint64(m)
        return np.nonzero(self.alive & ((self.masks & m) == m))[0]

    def count(self, *names: str) -> int:
        return int(self.query(*names).size)
