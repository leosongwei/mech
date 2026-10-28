"""Uniform spatial grid over the units: a CSR of square cells.

The battlefield is cut into 128 m cells and every unit that moves *or* can be
fought is filed under the one it stands in - the command base never moves, but
it is a target.  ``start`` says where a cell's block of ``items`` begins, so
"which units are near here" is two array lookups instead of a pass over the
whole field.

    cell 0    cell 1         cell 2   cell 3
    ┌──┐   ┌───────────────┐   ┌──┐   ┌──┐
    │  │   │ 17  42  103   │   │8 │   │  │
    └──┘   └───────────────┘   └──┘   └──┘
             ↑ start[1]        ↑ start[2]

    items = [17, 42, 103, 8, ...]

A grid, not a tree or a hash: units move every tick, so *rebuilding* is the
cost that matters and the layout is close to uniform.  The build walks the
archetypes (whole buffers, no id indirection), sorts the cell keys and counts
them - a few tens of microseconds for a few thousand units, and it buys every
query a handful of candidate ids instead of a scan of the whole battle.

The grid is a **snapshot of one tick**: it keeps its own copy of the positions
alongside the ids, so a query needs no ECS access at all.  Rebuild it after
anything has moved (``World.update`` does, at the top of the tick) - a stale
copy would aim the AI at where a hull used to be.
"""

from __future__ import annotations

import numpy as np

CELL = 128.0            # metres; ~the spacing of a 5000-unit battle on 12 km


class Grid:
    __slots__ = ("cell", "inv", "cw", "cd", "start", "items", "pos", "n")

    def __init__(self, width_m: float, depth_m: float, cell: float = CELL) -> None:
        self.cell = float(cell)
        self.inv = 1.0 / self.cell
        self.cw = max(1, int(width_m * self.inv) + 1)
        self.cd = max(1, int(depth_m * self.inv) + 1)
        self.start = np.zeros(self.cw * self.cd + 1, np.int32)
        self.items = np.zeros(0, np.int32)          # ids, grouped by cell
        self.pos = np.zeros((0, 3), np.float32)     # their positions, same order
        self.n = 0

    # -- building ---------------------------------------------------------
    def build(self, ecs, tags: tuple[str, ...] = ("moves",)) -> None:
        """Refile every entity carrying one of ``tags`` under the cell it stands in.

        Two sets, not one: the movers and the things that can be shot.  The
        command base is the one `combat` hull that never moves, and a grid of
        `moves` alone left it out - so the AI drove at the thing its lane ends
        at and never saw it as a target.  A hull carrying both tags is filed
        once (the archetype is the same object under either query).
        """
        groups = []
        seen: set = set()
        for tag in tags:
            for g in ecs.groups(tag, "pos"):
                if g.arch not in seen:
                    seen.add(g.arch)
                    groups.append(g)
        if len(groups) == 1:
            g = groups[0]
            ids, p = g.ids, g.bufs["pos"][:g.n]
        elif groups:
            ids = np.concatenate([g.ids for g in groups])
            p = np.concatenate([g.bufs["pos"][:g.n] for g in groups])
        else:
            ids = np.zeros(0, np.int32)
            p = np.zeros((0, 3), np.float32)
        self.n = ids.size
        if self.n == 0:
            self.items = ids
            self.pos = p
            self.start[:] = 0
            return

        ci = np.clip((p[:, 0] * self.inv).astype(np.int32), 0, self.cw - 1)
        cj = np.clip((p[:, 2] * self.inv).astype(np.int32), 0, self.cd - 1)
        key = cj * self.cw + ci

        order = np.argsort(key, kind="stable")
        self.items = ids[order]
        self.pos = p[order]
        counts = np.bincount(key, minlength=self.cw * self.cd)
        np.cumsum(counts, out=self.start[1:])
        self.start[0] = 0

    # -- queries ----------------------------------------------------------
    def cell_of(self, x: float, z: float) -> tuple[int, int]:
        return (min(max(int(x * self.inv), 0), self.cw - 1),
                min(max(int(z * self.inv), 0), self.cd - 1))

    def box(self, ci: int, cj: int, r: int) -> tuple[np.ndarray, np.ndarray]:
        """Ids and positions of every unit in the (2r+1)² cell box around
        ``(ci, cj)``, clipped to the map.

        The box is gathered in one vectorised pass: a row of cells is
        contiguous in ``items``, so a cell's block is addressed through
        ``start`` and the blocks are joined with ``repeat`` - 3000 cells is one
        call, not 3000 slices."""
        x0 = max(ci - r, 0)
        x1 = min(ci + r, self.cw - 1)
        j0 = max(cj - r, 0)
        j1 = min(cj + r, self.cd - 1)
        cells = (np.arange(j0, j1 + 1, dtype=np.int32)[:, None] * self.cw
                 + np.arange(x0, x1 + 1, dtype=np.int32)).ravel()
        s = self.start[cells]
        cnt = self.start[cells + 1] - s
        total = int(cnt.sum())
        if total == 0:
            return np.zeros(0, np.int32), self.pos[:0]
        # rank of each unit inside the gathered block
        cs = np.cumsum(cnt) - cnt
        idx = np.repeat(s - cs, cnt) + np.arange(total, dtype=np.int32)
        return self.items[idx], self.pos[idx]
