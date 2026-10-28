"""Archetype ECS: an entity's component set is its type.

A component is one dense buffer over the entities that own it, not one array
indexed by a global entity slot.  The group of entities owning exactly the same
component set is an :class:`Archetype`, and it is what holds those buffers, so
an id is no longer a storage address - :attr:`ECS._arch` / :attr:`ECS._row`
turn it into (archetype, row) and everything else addresses entities by id.

What keeps this honest is one rule: **the component set never changes.**  A
state transition that would add or drop a component is a *different* entity -
``systems/smoke.py`` destroys a canister and creates the puff it bursts into.
Two things fall out of that:

* no limit on how many component *kinds* there are (an archetype is a tuple of
  names, not one bit of a 64 bit mask) and no limit on how many entities exist;
* spawning only fills the components the new entity owns, so a projectile does
  not pay for the forty components of a mech, and there is no capacity to
  guess up front.

``ecs["pos"]`` still hands out a view answering ``view[eid]``, ``view[eid, i]``,
``view[ids]`` and ``view[ids, i]``, filling the component's registered default
where an entity does not own it - the same answer the old slot array gave (a
wreck has no ``weapon``, so it reads 0, and no ``target``, so it reads -1).
Systems that want the whole buffer at once should take
``arch.bufs["pos"][:arch.n]`` instead; ``ECS.row`` is the cheap single-entity
door for the ones that still loop.
"""

from __future__ import annotations

import numpy as np

INITIAL = 16            # first buffer size, doubled as an archetype fills up
GROW = 256              # first size of the id -> location tables


class _Spec:
    """What the registry knows about one component kind."""

    __slots__ = ("dtype", "shape", "default")

    def __init__(self, dtype, shape: tuple[int, ...], default) -> None:
        self.dtype = np.dtype(dtype)
        self.shape = tuple(shape)
        self.default = default


class Archetype:
    """Every entity with exactly one component set, in dense rows."""

    __slots__ = ("index", "key", "keyset", "bufs", "ids", "n", "_cap", "_ecs")

    def __init__(self, ecs: ECS, index: int, key: tuple[str, ...],
                 specs: dict[str, _Spec]) -> None:
        self._ecs = ecs
        self.index = index
        self.key = key                       # component names, sorted
        self.keyset = frozenset(key)
        self.bufs: dict[str, np.ndarray] = {}
        self.ids = np.zeros(INITIAL, np.int32)     # row -> entity id
        self.n = 0
        self._cap = INITIAL
        for name in key:
            self.bufs[name] = np.full((INITIAL, *specs[name].shape),
                                      specs[name].default, specs[name].dtype)

    # -- rows -------------------------------------------------------------
    def add(self, eid: int, specs: dict[str, _Spec]) -> int:
        if self.n == self._cap:
            self._resize(self._cap * 2)
        row = self.n
        for name, buf in self.bufs.items():
            buf[row] = specs[name].default
        self.ids[row] = eid
        self.n += 1
        return row

    def swap_remove(self, row: int) -> int:
        """Drop ``row``, moving the last row into the hole.  Returns the id of
        the entity that moved, or -1 when the hole was already the last row."""
        last = self.n - 1
        moved = -1
        if row != last:
            moved = int(self.ids[last])
            for buf in self.bufs.values():
                buf[row] = buf[last]
            self.ids[row] = moved
        self.n = last
        return moved

    def buffer(self, name: str) -> np.ndarray:
        """The live rows of one component: ``buf[:arch.n]``."""
        return self.bufs[name][: self.n]

    def _resize(self, cap: int) -> None:
        # Growing swaps in new arrays, which invalidates every buffer list a
        # caller is holding (`ecs.buffers`, `View._lut`).  Bumping the ECS
        # generation is what makes those caches rebuild - without it a loop
        # keeps writing into the old, shorter array.
        for name, buf in self.bufs.items():
            grown = np.full((cap, *buf.shape[1:]), 0, buf.dtype)
            grown[: self.n] = buf[: self.n]
            self.bufs[name] = grown
        ids = np.zeros(cap, np.int32)
        ids[: self.n] = self.ids[: self.n]
        self.ids = ids
        self._cap = cap
        self._ecs._gen += 1


def _lookup(ecs: ECS, ids: np.ndarray) -> np.ndarray:
    """Archetype index of each id, or -1 where it is not a live entity."""
    if ids.size == 0 or ecs._high == 0:
        return np.full(ids.shape, -1, np.int32)
    safe = np.clip(ids, 0, ecs._high - 1)
    bad = (ids < 0) | (ids >= ecs._high) | ~ecs.alive[safe]
    return np.where(bad, -1, ecs._arch[safe])


class Group:
    """One archetype, ready to iterate: buffers hoisted, rows are ``0..n-1``.

    Every live row of an archetype owns the whole component set, so a system
    that matches a group never has to test an entity - it walks rows, which is
    a plain int into a numpy buffer instead of an id through a :class:`View`.
    """

    __slots__ = ("arch", "bufs", "ids", "n")

    def __init__(self, arch: Archetype) -> None:
        self.arch = arch
        self.bufs = arch.bufs
        self.ids = arch.ids[: arch.n]        # row -> entity id, for the few
        self.n = arch.n                      # places that need an id as well


class View:
    """Component access by entity id (``ecs["pos"]``).

    A batch read copies (as ``arr[ids]`` always did), a batch write scatters,
    and an id that does not own the component reads its registered default - the
    same answer the old slot array gave.  The single-entity forms are on the hot
    path of every system, so they resolve (archetype, row) inline and index the
    buffer in one go.
    """

    __slots__ = ("_ecs", "_name", "_dtype", "_shape", "_default", "_bufs", "_gen")

    def __init__(self, ecs: ECS, name: str) -> None:
        spec = ecs._specs[name]
        self._ecs = ecs
        self._name = name
        self._dtype = spec.dtype
        self._shape = spec.shape
        self._default = spec.default
        self._bufs: list[np.ndarray | None] = []
        self._gen = -1

    def _lut(self) -> list[np.ndarray | None]:
        ecs = self._ecs
        if self._gen != ecs._gen:
            self._bufs = ecs.buffers(self._name)
            self._gen = ecs._gen
        return self._bufs

    def _blank(self, n: int) -> np.ndarray:
        return np.full((n, *self._shape), self._default, self._dtype)

    # -- read -------------------------------------------------------------
    def __getitem__(self, key):
        ecs = self._ecs
        if type(key) is tuple:
            ids, tail = key[0], key[1:]
        else:
            ids, tail = key, ()
        if isinstance(ids, (int, np.integer)):
            eid = int(ids)
            if 0 <= eid < ecs._high and ecs.alive[eid]:
                buf = self._lut()[int(ecs._arch[eid])]
                if buf is not None:
                    row = ecs._row[eid]
                    if not tail:
                        return buf[row]
                    if len(tail) == 1:
                        return buf[row, tail[0]]
                    return buf[(row, *tail)]
            val = np.full(self._shape, self._default, self._dtype)
            for i in tail:
                val = val[i]
            return val
        return self._gather(np.asarray(ids), tail)

    def _gather(self, ids: np.ndarray, tail) -> np.ndarray:
        ecs = self._ecs
        n = ids.size
        if not tail:
            out = self._blank(n)
        else:
            out = np.full((n, *self._shape[len(tail):]), self._default, self._dtype)
        if n == 0:
            return out
        where = _lookup(ecs, ids)
        bufs = self._lut()
        for ai in np.unique(where):
            ai = int(ai)
            if ai < 0:
                continue
            buf = bufs[ai]
            if buf is None:
                continue
            sel = np.nonzero(where == ai)[0]
            vals = buf[ecs._row[ids[sel]]]
            if tail:
                vals = vals[(slice(None), *tail)]
            out[sel] = vals
        return out

    # -- write ------------------------------------------------------------
    def __setitem__(self, key, value) -> None:
        ecs = self._ecs
        if type(key) is tuple:
            ids, tail = key[0], key[1:]
        else:
            ids, tail = key, ()
        if isinstance(ids, (int, np.integer)):
            eid = int(ids)
            buf = (self._lut()[int(ecs._arch[eid])]
                   if 0 <= eid < ecs._high and ecs.alive[eid] else None)
            if buf is None:
                raise KeyError(f"entity {eid} has no '{self._name}'")
            row = ecs._row[eid]
            if not tail:
                buf[row] = value
            elif len(tail) == 1:
                buf[row, tail[0]] = value
            else:
                buf[(row, *tail)] = value
            return

        ids = np.asarray(ids)
        if ids.size == 0:
            return
        where = _lookup(ecs, ids)
        bufs = self._lut()
        vals = np.asarray(value)
        for ai in np.unique(where):
            ai = int(ai)
            if ai < 0 or bufs[ai] is None:
                raise KeyError(f"entity {int(ids[where == ai][0])} has no '{self._name}'")
            sel = np.nonzero(where == ai)[0]
            rows = ecs._row[ids[sel]]
            src = vals if vals.ndim == 0 else vals[sel]
            if tail:
                bufs[ai][(rows, *tail)] = src
            else:
                bufs[ai][rows] = src


class ECS:
    def __init__(self, capacity: int = GROW) -> None:
        self._specs: dict[str, _Spec] = {}
        self.archetypes: list[Archetype] = []
        self._by_key: dict[tuple[str, ...], Archetype] = {}
        self._views: dict[str, View] = {}
        self._luts: dict[str, tuple[int, list]] = {}
        self._gen = 0                        # bumped when an archetype appears
        self.alive = np.zeros(0, bool)
        self._arch = np.zeros(0, np.int32)
        self._row = np.zeros(0, np.int32)
        self._free: list[int] = []
        self._high = 0
        # ids freed during this tick, for observers that want to know who died;
        # ``World.update`` clears the list at the top of every tick, so a slot
        # that is recycled in the same tick is alive again but still listed
        self.destroyed: list[int] = []
        self._grow(capacity)

    # -- registration -----------------------------------------------------
    def register(self, name: str, dtype, shape: tuple[int, ...] = (),
                 default: object = 0) -> None:
        """Declare a component.  Every kind must be declared before the first
        entity exists, because an archetype allocates its buffers from here."""
        if self.archetypes:
            raise RuntimeError("register every component before creating entities")
        if name in self._specs:
            raise RuntimeError(f"component '{name}' already registered")
        self._specs[name] = _Spec(dtype, shape, default)

    def buffers(self, name: str) -> list[np.ndarray | None]:
        """Archetype index -> the buffer holding ``name`` (None where that group
        does not own it).  Cached and rebuilt when an archetype appears - a
        component never joins an existing group, so that is the only event that
        can invalidate it."""
        cached = self._luts.get(name)
        if cached is not None and cached[0] == self._gen:
            return cached[1]
        lut = [a.bufs.get(name) for a in self.archetypes]
        self._luts[name] = (self._gen, lut)
        return lut

    def locate(self, ids: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        """(rows, archetype indices) for ascending ids - the order preserving
        door, for the systems whose iteration order must not change because it
        draws the random numbers."""
        ids = np.asarray(ids)
        return self._row[ids], self._arch[ids]

    def groups(self, *names: str) -> list[Group]:
        """The non-empty archetypes owning all of ``names``, buffers hoisted.

        Iterating these is archetype-major: fine for anything that integrates
        entities independently (movement, countdowns), but it reorders the
        world, so a system that draws random numbers per entity must keep using
        ``query`` + ``locate`` instead."""
        need = frozenset(names)
        return [Group(a) for a in self.archetypes if a.n and need <= a.keyset]

    def get(self, name: str) -> View:
        return self[name]

    def __getitem__(self, name: str) -> View:
        view = self._views.get(name)
        if view is None:
            if name not in self._specs:
                raise KeyError(f"unknown component '{name}'")
            view = self._views[name] = View(self, name)
        return view

    # -- entity lifecycle -------------------------------------------------
    def create(self, comps=()) -> int:
        """Spawn an entity owning exactly ``comps``.  This is the only place a
        component set is decided - see the module docstring."""
        key = tuple(sorted(set(comps)))
        arch = self._by_key.get(key)
        if arch is None:
            missing = [n for n in key if n not in self._specs]
            if missing:
                raise KeyError(f"unknown component(s) {missing}")
            arch = Archetype(self, len(self.archetypes), key, self._specs)
            self.archetypes.append(arch)
            self._by_key[key] = arch
            self._gen += 1
        eid = self._alloc()
        self._arch[eid] = arch.index
        self._row[eid] = arch.add(eid, self._specs)
        self.alive[eid] = True
        return eid

    def destroy(self, eid: int) -> None:
        if eid < 0 or eid >= self._high or not self.alive[eid]:
            return
        ai = int(self._arch[eid])
        if ai >= 0:
            arch = self.archetypes[ai]
            row = int(self._row[eid])
            moved = arch.swap_remove(row)
            if moved >= 0:
                self._row[moved] = row
            self._arch[eid] = -1
        self.alive[eid] = False
        self._row[eid] = 0
        self._free.append(eid)
        self.destroyed.append(eid)

    def _alloc(self) -> int:
        if self._free:
            return self._free.pop()          # LIFO: the id a corpse just left
        eid = self._high
        if eid >= self.alive.size:
            self._grow(max(GROW, self.alive.size * 2))
        self._high = eid + 1
        return eid

    def _grow(self, cap: int) -> None:
        old = self.alive.size
        alive = np.zeros(cap, bool)
        arch = np.full(cap, -1, np.int32)
        row = np.zeros(cap, np.int32)
        alive[:old] = self.alive
        arch[:old] = self._arch
        row[:old] = self._row
        self.alive, self._arch, self._row = alive, arch, row

    # -- queries ----------------------------------------------------------
    def query(self, *names: str) -> np.ndarray:
        """Ids of the entities owning all of ``names``, ascending.

        Ascending matters: it is the order the old slot scan produced, and the
        order systems consume the world in - a different order would draw a
        different sequence of random numbers and change the whole battle."""
        if not names:
            return np.nonzero(self.alive[: self._high])[0].astype(np.int32)
        need = frozenset(names)
        parts = [a.ids[: a.n] for a in self.archetypes if a.n and need <= a.keyset]
        if not parts:
            return np.zeros(0, np.int32)
        out = parts[0] if len(parts) == 1 else np.concatenate(parts)
        return np.sort(out)

    def count(self, *names: str) -> int:
        return int(self.query(*names).size)

    def has(self, eid: int, *names: str) -> bool:
        if eid < 0 or eid >= self._high or not self.alive[eid]:
            return False
        ai = int(self._arch[eid])
        if ai < 0:
            return False
        keyset = self.archetypes[ai].keyset
        return all(n in keyset for n in names)

    def has_arr(self, ids: np.ndarray, name: str) -> np.ndarray:
        """Vectorised :meth:`has`."""
        ids = np.asarray(ids)
        if ids.size == 0:
            return np.zeros(0, bool)
        where = _lookup(self, ids)
        lut = np.zeros(max(1, len(self.archetypes)), bool)
        for arch in self.archetypes:
            lut[arch.index] = name in arch.keyset
        return (where >= 0) & lut[np.maximum(where, 0)]

    def validate(self) -> None:
        """Assert the id -> (archetype, row) table and the archetypes agree.

        Cheap enough to run at the end of a replay: it catches bookkeeping that
        a value comparison would not, like a row pointing past a buffer."""
        for eid in np.nonzero(self.alive[: self._high])[0]:
            ai = int(self._arch[eid])
            assert ai >= 0, f"entity {eid} is alive but has no archetype"
            arch = self.archetypes[ai]
            row = int(self._row[eid])
            assert 0 <= row < arch.n, f"entity {eid}: row {row} outside {arch.n}"
            assert int(arch.ids[row]) == eid, f"row {row} of {ai} holds {arch.ids[row]}"
        for arch in self.archetypes:
            for row in range(arch.n):
                eid = int(arch.ids[row])
                assert self.alive[eid], f"archetype row holds dead entity {eid}"
                assert int(self._arch[eid]) == arch.index and int(self._row[eid]) == row

    def slot(self, eid: int) -> tuple[dict[str, np.ndarray], int]:
        """(the entity's archetype buffers, its row in them).

        For a per-entity loop that touches many components: one lookup, then
        every access is ``bufs["pos"][row, 0]`` instead of a :class:`View`
        round trip.  ``("combat" in bufs)`` is the cheap component test."""
        ai = int(self._arch[eid])
        if ai < 0:
            return {}, -1
        return self.archetypes[ai].bufs, int(self._row[eid])

    def row(self, eid: int, name: str) -> tuple[np.ndarray, int]:
        """The fast door for a single entity: ``buf, row = ecs.row(eid, "pos")``
        straight into numpy, without building a view."""
        ai = int(self._arch[eid])
        return self.archetypes[ai].bufs[name], int(self._row[eid])
