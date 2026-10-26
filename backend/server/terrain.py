"""OpenTTD-ish tile terrain: heightfield, tile map, lanes and towns.

The heightfield stores *vertex* heights (metres, quantised to ``HSTEP`` steps so
the ground reads as flat terraces joined by one-tile slopes - the angular
OpenTTD look) on a ``W x H`` grid whose vertices sit at ``(i*TILE, j*TILE)``.
The tile map stores one byte per tile (``tiles[j, i]`` covering
``[i*TILE,(i+1)*TILE) square``).

Map is 16000 x 16000 m made of 16 m tiles.  Both arrays are shipped to the
browser (zlib compressed) so client side prediction - ballistic impact
markers, reticles - matches the server exactly.

Everything is generated with numpy: 1 M tiles must never be touched by a
pure python loop.
"""

from __future__ import annotations

import math

import numpy as np

TILE = 16.0
MAPW = 1000
MAPH = 1000
HSTEP = 4.0             # height quantum: one 4 m step per 16 m tile is a
                        # gentle 14 degree ramp, so terraces stay drivable

TILE_GRASS = 0
TILE_ROAD = 1
TILE_BUILDING = 2
TILE_TREE = 3
TILE_ROCK = 4
TILE_BASE = 5
TILE_SOLID = 6          # blocks movement, rendered as part of the base
TILE_FOREST = 7         # thick stand: the client plants several trees per tile

BLOCKING = (TILE_BUILDING, TILE_TREE, TILE_ROCK, TILE_SOLID, TILE_FOREST)
# ... except the two tree tiles, which block with their trunks only - see
# Terrain.blocked / wall_at

# A road tile is a set of pieces, the way OpenTTD composes one from ROADBITS:
# each of the four cardinal bits is the half of the tile that reaches that
# edge, so one bit is a straight, two adjacent bits an elbow, three a T and
# four a crossroads.  OpenTTD has no diagonal road at all; the two diagonal
# bits are ours - a 45 degree band that enters one edge in the middle and
# leaves through the middle of the next, so a staircase of them is one
# continuous diagonal road instead of the tile by tile stair you would
# otherwise have to drive.
ROAD_N = 1              # half piece out to the north edge  (-z)
ROAD_E = 2              # ... east  (+x)
ROAD_S = 4              # ... south (+z)
ROAD_W = 8              # ... west  (-x)
ROAD_D_WS = 16          # west edge middle -> south edge middle (SW corner)
ROAD_D_NE = 32          # north edge middle -> east edge middle (NE corner)
ROAD_DIAG = ROAD_D_WS | ROAD_D_NE
ROAD_STRAIGHT = ROAD_N | ROAD_E | ROAD_S | ROAD_W

ROAD_WEIGHT = 100       # how hard a road tile outweighs its verges when the
                        # corridor is flattened: the asphalt stays level
BUILDING_H = 8.0        # wall height of a building tile the map did not size
TREE_H = 10.0           # how tall a tree stand stops sight (its canopy)
TRUNK_R = 0.5           # trunk radius of a tree: the only thing a round or a
                        # hull actually meets in a tree tile (see _trunks)
FLOOR_H = 4.0           # one storey of a town building

# command bases (tile coordinates)
BASE_TILES = {0: (188.0, 188.0), 1: (812.0, 812.0)}

# lane way points in tile coordinates (three classic lanes on a square map).
# A way point times TILE is a world position, so the half tile in every
# coordinate keeps the traffic on the *centre* of a road tile - which is where
# the tile pieces put the asphalt - and the diagonal lane on the centre of its
# 45 degree band.
LANE_TILES = [
    [(188.5, 188.5), (812.5, 188.5), (812.5, 812.5)],     # north lane
    [(188.5, 188.5), (188.5, 812.5), (812.5, 812.5)],     # west lane
    [(188.0, 188.5), (500.0, 500.5), (812.0, 812.5)],     # mid lane
]

TOWN_SPOTS = [
    (270, 188), (390, 188), (520, 188), (650, 188), (780, 188),
    (188, 270), (188, 390), (188, 520), (188, 650), (188, 780),
    (812, 270), (812, 390), (812, 520), (812, 650), (812, 780),
    (270, 812), (390, 812), (520, 812), (650, 812), (780, 812),
    (350, 350), (440, 440), (560, 560), (650, 650), (740, 740),
    (390, 520), (520, 390), (610, 400), (400, 610),
]



def _upsample(g: np.ndarray, w: int, h: int) -> np.ndarray:
    gh, gw = g.shape
    xs = np.linspace(0, gw - 1, w, dtype=np.float32)
    ys = np.linspace(0, gh - 1, h, dtype=np.float32)
    x0 = np.floor(xs).astype(np.int32)
    x1 = np.minimum(x0 + 1, gw - 1)
    fx = (xs - x0).astype(np.float32)
    y0 = np.floor(ys).astype(np.int32)
    y1 = np.minimum(y0 + 1, gh - 1)
    fy = (ys - y0).astype(np.float32)
    rows0 = g[y0]
    rows1 = g[y1]
    top = rows0[:, x0] * (1 - fx) + rows0[:, x1] * fx
    bot = rows1[:, x0] * (1 - fx) + rows1[:, x1] * fx
    return top * (1 - fy)[:, None] + bot * fy[:, None]


def _fbm(rng: np.random.Generator, w: int, h: int,
         octaves: int = 6, base_res: int = 2) -> np.ndarray:
    out = np.zeros((h, w), np.float32)
    amp = 1.0
    total = 0.0
    res = base_res
    for _ in range(octaves):
        g = rng.random((res, res)).astype(np.float32)
        out += _upsample(g, w, h) * amp
        total += amp
        amp *= 0.5
        res *= 2
    return out / total


def _blur3(a: np.ndarray) -> np.ndarray:
    """Separable 1-2-1 blur, one tile wide."""
    p = np.pad(a, 1, mode="edge")
    x = (p[:, :-2] + 2.0 * p[:, 1:-1] + p[:, 2:]) * 0.25
    return (x[:-2, :] + 2.0 * x[1:-1, :] + x[2:, :]) * 0.25


def _raster_line(tiles: np.ndarray, a: tuple[float, float], b: tuple[float, float],
                 value: int, step: float = 0.5) -> None:
    """Draw a rasterised line of *value* into the tile map."""
    ax, ay = a
    bx, by = b
    n = int(max(abs(bx - ax), abs(by - ay)) / step) + 2
    ts = np.linspace(0.0, 1.0, n, dtype=np.float32)
    i = (ax + (bx - ax) * ts).astype(np.int32)
    j = (ay + (by - ay) * ts).astype(np.int32)
    ok = (i >= 0) & (i < tiles.shape[1]) & (j >= 0) & (j < tiles.shape[0])
    tiles[j[ok], i[ok]] = value


def _terrace(h: np.ndarray) -> np.ndarray:
    """Snap a smooth heightfield to ``HSTEP`` platforms.

    The whole map becomes flat terraces joined by gentle one-tile ramps: the
    ground reads as cut out of blocks instead of a dune.
    """
    return (np.round(h / HSTEP) * HSTEP).astype(np.float32)


def _road_leg(roads: np.ndarray, a: tuple[float, float],
              b: tuple[float, float]) -> None:
    """Lay the pieces of one lane leg into the road bit map.

    A leg is either along an axis - laid as half-tile pieces, so it elbows and
    crosses whatever it meets - or a 45 degree run, laid as the two diagonal
    pieces a staircase needs: one tile cuts its south-west corner, the next its
    north-east one, and the pair repeats.  Both ends of every piece sit in the
    middle of a tile edge, so consecutive pieces join full width.
    """
    (ax, ay), (bx, by) = a, b
    dx, dy = bx - ax, by - ay
    if dx == 0 and dy == 0:
        return
    if dx == 0 or dy == 0:
        # the leg runs down the middle of a row or a column of tiles
        i0, i1 = int(np.floor(min(ax, bx))), int(np.floor(max(ax, bx)))
        j0, j1 = int(np.floor(min(ay, by))), int(np.floor(max(ay, by)))
        n = max(i1 - i0, j1 - j0)
        sx = (dx > 0) - (dx < 0)
        sy = (dy > 0) - (dy < 0)
        if sx > 0:
            back, fwd = ROAD_W, ROAD_E
        elif sx < 0:
            back, fwd = ROAD_E, ROAD_W
        elif sy > 0:
            back, fwd = ROAD_N, ROAD_S
        else:
            back, fwd = ROAD_S, ROAD_N
        si, sj = (i0 if sx >= 0 else i1), (j0 if sy >= 0 else j1)
        for k in range(n + 1):
            i, j = si + k * sx, sj + k * sy
            if k > 0:
                roads[j, i] |= back
            if k < n:
                roads[j, i] |= fwd
    elif abs(dx) == abs(dy):
        sx = (dx > 0) - (dx < 0)
        sy = (dy > 0) - (dy < 0)
        if sx * sy < 0:
            raise ValueError(f"lane leg {a}->{b}: only south-east diagonals exist")
        n = int(round(abs(dx)))
        i0, j0 = int(np.floor(ax)), int(np.floor(ay))
        for k in range(n):
            i, j = i0 + k * sx, j0 + k * sy
            roads[j, i] |= ROAD_D_WS
            roads[j + sy, i] |= ROAD_D_NE
    else:
        raise ValueError(f"lane leg {a}->{b} is neither axis aligned nor 45 degrees")


def _dilate(mask: np.ndarray, r: int) -> np.ndarray:
    out = mask.copy()
    for _ in range(r):
        nxt = out.copy()
        nxt[1:, :] |= out[:-1, :]
        nxt[:-1, :] |= out[1:, :]
        nxt[:, 1:] |= out[:, :-1]
        nxt[:, :-1] |= out[:, 1:]
        out = nxt
    return out


def _tree_hash(a: float, b: float) -> float:
    """The 0..1 hash the client plants its trees with (`renderer.js`, `plant`).

    Both sides must pick the same trunk spots, or a round stops in the air
    beside a trunk the player can see.
    """
    s = math.sin(a * 12.9898 + b * 78.233) * 43758.5453
    return s - math.floor(s)


def _trunks(i: int, j: int, t: int) -> tuple[tuple[float, float, float, float], ...]:
    """Where the trunks of a tree tile stand: ``(x, z, radius, height)`` each.

    One or two trees on a tree tile, three to five on a forest tile, spread over
    the tile but never touching its border - exactly the stand the client
    draws.  The canopy above them is not part of this: it hides what is behind
    it (`sight_at`) without stopping anything that flies.
    """
    r1 = _tree_hash(i, j)
    n = 3 + int(r1 * 3) if t == TILE_FOREST else 1 + int(r1 * 2)
    cx = (i + 0.5) * TILE
    cz = (j + 0.5) * TILE
    out = []
    for q in range(n):
        q1 = _tree_hash(i + q * 5.3, j + q * 1.9)
        q2 = _tree_hash(j + q * 3.7, i + q * 7.1)
        q3 = _tree_hash(i * 1.3 + q, j * 2.7 - q)
        out.append((cx + (q1 - 0.5) * TILE * 0.66,
                    cz + (q2 - 0.5) * TILE * 0.66,
                    TRUNK_R * (0.7 + q3 * 0.5),
                    (10.0 + q3 * 8.0) * 0.40))
    return tuple(out)


class Terrain:
    def __init__(self, heights: np.ndarray, tiles: np.ndarray, lanes: list,
                 towns: list[dict], bases: dict[int, tuple[float, float]],
                 roads: np.ndarray | None = None,
                 walls: np.ndarray | None = None) -> None:
        self.h = heights.astype(np.float32)      # (d, w) vertex heights
        self.tiles = tiles.astype(np.uint8)      # (d, w)
        self.w = int(heights.shape[1])
        self.d = int(heights.shape[0])
        self.lanes = lanes                       # list[list[(x, z)]] world metres
        self.towns = towns                       # list of dicts
        self.bases = bases                       # faction -> (x, z) metres
        # road pieces per tile (ROAD_* bits) and the wall column of every
        # building tile in metres, so the client draws exactly what stops a
        # round instead of guessing a height of its own
        self.roads = (np.zeros((self.d, self.w), np.uint8) if roads is None
                      else roads.astype(np.uint8))
        self.walls = (np.zeros((self.d, self.w), np.uint8) if walls is None
                      else walls.astype(np.uint8))
        # trunk layout of the tree tiles that get asked about, tile index ->
        # ((x, z, r, h), ...) - built on demand, a few hundred entries
        self.trunk_cache: dict[int, tuple] = {}

    # -- sampling ---------------------------------------------------------
    def height_at(self, x: float, z: float) -> float:
        fx = min(max(x / TILE, 0.0), self.w - 1.001)
        fz = min(max(z / TILE, 0.0), self.d - 1.001)
        i = int(fx)
        j = int(fz)
        tx = fx - i
        tz = fz - j
        h = self.h
        return float((h[j, i] * (1 - tx) + h[j, i + 1] * tx) * (1 - tz) +
                     (h[j + 1, i] * (1 - tx) + h[j + 1, i + 1] * tx) * tz)

    def _trunks(self, i: int, j: int) -> tuple:
        k = j * self.w + i
        tr = self.trunk_cache.get(k)
        if tr is None:
            tr = _trunks(i, j, int(self.tiles[j, i]))
            self.trunk_cache[k] = tr
        return tr

    def wall_at(self, x: float, z: float) -> float:
        """Height of the obstacle standing on the ground at (x, z).

        A building is a solid column up to its roof, a tree is nothing but its
        trunk - the canopy overhead stops no round; everything else is bare
        ground.  Off the field nothing flies on, which the caller reads as an
        immediate hit.
        """
        i = int(np.floor(x / TILE))
        j = int(np.floor(z / TILE))
        if i < 0 or j < 0 or i >= self.w or j >= self.d:
            return 1e6
        t = self.tiles[j, i]
        if t == TILE_BUILDING:
            return float(self.walls[j, i]) or BUILDING_H
        if t == TILE_TREE or t == TILE_FOREST:
            for tx, tz, r, h in self._trunks(i, j):
                if (x - tx) ** 2 + (z - tz) ** 2 <= r * r:
                    return h
        return 0.0

    def sight_wall(self, x: float, z: float) -> float:
        """The height a line of sight is cut at: a building up to its roof, a
        tree stand at its canopy - the leaves hide what stands behind them even
        though they stop neither a round nor a hull."""
        i = int(np.floor(x / TILE))
        j = int(np.floor(z / TILE))
        if i < 0 or j < 0 or i >= self.w or j >= self.d:
            return 1e6
        t = self.tiles[j, i]
        if t == TILE_BUILDING:
            return float(self.walls[j, i]) or BUILDING_H
        if t == TILE_TREE or t == TILE_FOREST:
            return TREE_H
        return 0.0

    def obstacle_at(self, x: float, z: float) -> float:
        """The height a round coming in from above is stopped at."""
        return self.height_at(x, z) + self.wall_at(x, z)

    def sight_at(self, x: float, z: float) -> float:
        """The height a line of sight is cut at."""
        return self.height_at(x, z) + self.sight_wall(x, z)

    def tile_type(self, x: float, z: float) -> int:
        i = int(x / TILE)
        j = int(z / TILE)
        if i < 0 or j < 0 or i >= self.w or j >= self.d:
            return TILE_ROCK
        return int(self.tiles[j, i])

    def blocked(self, x: float, z: float) -> bool:
        """Whether a hull cannot stand at (x, z).  A tree blocks with its trunk
        only, so a hull drives under the canopy of a tree or a whole forest."""
        t = self.tile_type(x, z)
        if t == TILE_TREE or t == TILE_FOREST:
            i = int(x / TILE)
            j = int(z / TILE)
            for tx, tz, r, _h in self._trunks(i, j):
                if (x - tx) ** 2 + (z - tz) ** 2 <= r * r:
                    return True
            return False
        return t in BLOCKING

    def trunk_blocked(self, x: float, z: float, rad: float) -> bool:
        """Whether a hull of radius ``rad`` at (x, z) runs into a trunk.

        A trunk is far thinner than the handful of points movement samples, so
        the tiles the hull overlaps are distance tested instead - otherwise a
        mech drives straight through the trees it should be weaving between.
        """
        i0 = max(int((x - rad) / TILE), 0)
        i1 = min(int((x + rad) / TILE), self.w - 1)
        j0 = max(int((z - rad) / TILE), 0)
        j1 = min(int((z + rad) / TILE), self.d - 1)
        for j in range(j0, j1 + 1):
            for i in range(i0, i1 + 1):
                t = self.tiles[j, i]
                if t != TILE_TREE and t != TILE_FOREST:
                    continue
                for tx, tz, r, _h in self._trunks(i, j):
                    if (x - tx) ** 2 + (z - tz) ** 2 <= (rad + r) ** 2:
                        return True
        return False

    def inside(self, x: float, z: float) -> bool:
        """False once a point leaves the map: off the field there is no ground
        to hit, only the edge height ``height_at`` keeps returning."""
        return 0.0 <= x <= (self.w - 1) * TILE and 0.0 <= z <= (self.d - 1) * TILE

    # -- ray casts --------------------------------------------------------
    def _spans(self, p0: np.ndarray, dx: float, dz: float):
        """Yield ``(t_enter, t_exit, i, j)`` for every tile the segment crosses.

        The ground is sampled inside each of them, so the walk cannot step over
        a tile: a 12.7 mm round covers 26 m in one 30 Hz tick, more than a whole
        16 m tile, and a fixed sampling spacing would let it fly straight
        through a building and burst behind it.
        """
        x, z = float(p0[0]), float(p0[2])
        i = int(np.floor(x / TILE))
        j = int(np.floor(z / TILE))
        sx = 1 if dx > 0 else -1
        sz = 1 if dz > 0 else -1
        tx = ((i + (1 if dx > 0 else 0)) * TILE - x) / dx if dx else 2.0
        tz = ((j + (1 if dz > 0 else 0)) * TILE - z) / dz if dz else 2.0
        t = 0.0
        while True:
            tn = min(tx, tz, 1.0)
            yield t, tn, i, j
            if tn >= 1.0:
                return
            t = tn
            if tx <= tz:
                i += sx
                tx = ((i + (1 if dx > 0 else 0)) * TILE - x) / dx
            else:
                j += sz
                tz = ((j + (1 if dz > 0 else 0)) * TILE - z) / dz

    def _trunk_hit(self, p0, dx: float, dy: float, dz: float,
                   i: int, j: int, ta: float, tb: float):
        """Where inside tile ``(i, j)`` the segment enters a trunk, or None.

        A trunk is about a metre wide, far thinner than the 8 m the ground walk
        samples at, so it is solved as a cylinder instead of sampled - which is
        what makes a round fired at a tree actually stop in it.
        """
        t = self.tiles[j, i]
        if t != TILE_TREE and t != TILE_FOREST:
            return None
        a = dx * dx + dz * dz
        if a <= 1e-9:
            return None
        best = None
        for tx, tz, r, h in self._trunks(i, j):
            ex = p0[0] - tx
            ez = p0[2] - tz
            b = 2.0 * (ex * dx + ez * dz)
            c = ex * ex + ez * ez - r * r
            disc = b * b - 4.0 * a * c
            if disc < 0.0:
                continue
            sq = disc ** 0.5
            for root in ((-b - sq) / (2.0 * a), (-b + sq) / (2.0 * a)):
                if ta <= root <= tb and 0.0 <= root <= 1.0:
                    if p0[1] + dy * root <= self.height_at(tx, tz) + h:
                        if best is None or root < best:
                            best = root
                    break
        return best

    def _march(self, p0: np.ndarray, p1: np.ndarray, step: float, at,
               trunks: bool = False,
               ) -> tuple[bool, float, np.ndarray | tuple[float, float, float]]:
        """Walk the segment and report the first point at or below ``at``."""
        dx = float(p1[0] - p0[0])
        dy = float(p1[1] - p0[1])
        dz = float(p1[2] - p0[2])
        dist = (dx * dx + dy * dy + dz * dz) ** 0.5
        if dist < 1e-4:
            return False, 0.0, p0
        if p0[1] <= at(p0[0], p0[2]) + 1.0:
            return True, 0.0, p0
        prev = 0.0
        for ta, tb, i, j in self._spans(p0, dx, dz):
            tt = (self._trunk_hit(p0, dx, dy, dz, i, j, ta, tb)
                  if trunks and 0 <= i < self.w and 0 <= j < self.d else None)
            n = max(1, int((tb - ta) * dist / step + 0.5))
            for k in range(1, n + 1):
                t = ta + (tb - ta) * k / n
                if p0[1] + dy * t > at(p0[0] + dx * t, p0[2] + dz * t):
                    prev = t
                    continue
                if tt is not None and tt < t:
                    return True, tt, (p0[0] + dx * tt, p0[1] + dy * tt,
                                      p0[2] + dz * tt)
                a, b = prev, t
                for _ in range(6):
                    m = (a + b) * 0.5
                    if p0[1] + dy * m > at(p0[0] + dx * m, p0[2] + dz * m):
                        a = m
                    else:
                        b = m
                t = (a + b) * 0.5
                return True, t, (p0[0] + dx * t, p0[1] + dy * t, p0[2] + dz * t)
            if tt is not None:
                return True, tt, (p0[0] + dx * tt, p0[1] + dy * tt, p0[2] + dz * tt)
        return False, 1.0, p1

    def trunk_hit(self, p0: np.ndarray, p1: np.ndarray,
                  ) -> tuple[bool, float, np.ndarray | tuple[float, float, float]]:
        """``(hit, t, point)`` for the first tree trunk the segment runs into."""
        dx = float(p1[0] - p0[0])
        dy = float(p1[1] - p0[1])
        dz = float(p1[2] - p0[2])
        for ta, tb, i, j in self._spans(p0, dx, dz):
            if not (0 <= i < self.w and 0 <= j < self.d):
                continue
            t = self._trunk_hit(p0, dx, dy, dz, i, j, ta, tb)
            if t is not None:
                return True, t, (p0[0] + dx * t, p0[1] + dy * t, p0[2] + dz * t)
        return False, 1.0, p1

    def ground_hit(self, p0: np.ndarray, p1: np.ndarray,
                   step: float = 8.0,
                   ) -> tuple[bool, float, np.ndarray | tuple[float, float, float]]:
        """Return (hit, t, point) for the first ground contact on the segment.

        Buildings and tree trunks count as ground: the wall of a building is a
        vertical step at the tile border, so a round fired into a facade bursts
        on the face - where the smoke can be seen - instead of inside the hull.
        Leaves are not in the way; they only hide (`sight_at`).
        """
        return self._march(p0, p1, step, self.obstacle_at, trunks=True)

    def los(self, a: np.ndarray, b: np.ndarray, step: float = 8.0) -> bool:
        """Whether ``a`` sees ``b``: the ground, the buildings and the tree
        canopies between them decide, not the trunks."""
        hit, _, _ = self._march(a, b, step, self.sight_at)
        return not hit


def generate(seed: int = 11) -> Terrain:
    rng = np.random.default_rng(seed)
    w, d = MAPW, MAPH

    # layered terrain: broad hills + medium relief.  The fbm field is stretched
    # over its own percentiles so the map actually has ridges (~300 m) and
    # plains (~20 m) instead of one gentle carpet: terraced afterwards, that
    # contrast is what makes the ground read as cut blocks.
    base = _fbm(rng, w, d, octaves=6)
    relief = _fbm(rng, w, d, octaves=5)
    lo, hi = np.percentile(base, 8), np.percentile(base, 97)
    base = np.clip((base - lo) / (hi - lo), 0.0, 1.0)
    height = 20.0 + base ** 1.35 * 280.0 + (relief - 0.5) * 40.0
    height = _blur3(_blur3(height))

    tiles = np.zeros((d, w), np.uint8)

    # command base plateaus
    yy, xx = np.mgrid[0:d, 0:w].astype(np.float32)
    for fac, (btx, bty) in BASE_TILES.items():
        bx, by = btx * TILE, bty * TILE
        target = float(np.round(height[int(bty), int(btx)] / HSTEP) * HSTEP)
        dist = np.hypot(xx * TILE - bx, yy * TILE - by)
        k = np.clip(1.0 - dist / 600.0, 0.0, 1.0)
        k = k * k * (3 - 2 * k)
        height = height * (1 - k) + target * k

    # ---- roads -----------------------------------------------------------
    # one bit per road piece: the client draws exactly these pieces, so the
    # surface it lays on the ground is the tile map, not a ribbon of its own
    roads = np.zeros((d, w), np.uint8)
    for lane in LANE_TILES:
        for a, b in zip(lane[:-1], lane[1:]):
            _road_leg(roads, a, b)
    road = roads != 0
    tiles[road] = TILE_ROAD

    # ---- road corridor ---------------------------------------------------
    # Cut the roads into the ground.  Every tile of the asphalt is made dead
    # flat - all four of its corners take one terraced height - and the tiles
    # beside it are averaged into it, so what the asphalt is laid on is a run
    # of neat plateaus instead of four corners of four different slopes meeting
    # under the road.  That is what keeps the surface from breaking up at the
    # tile diagonals.  A road tile outvotes the tiles around it, so the road
    # itself never bends: where it changes level, the verges do.
    height = _terrace(height)
    corridor = _dilate(road, 2)
    htile = np.round(_blur3(_blur3(_blur3(height))) / HSTEP) * HSTEP

    def corner_sum(mask: np.ndarray, value: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        acc = np.zeros((d + 1, w + 1), np.float32)
        cnt = np.zeros((d + 1, w + 1), np.float32)
        for dj in (0, 1):
            for di in (0, 1):
                acc[dj:dj + d, di:di + w] += np.where(mask, value, 0.0)
                cnt[dj:dj + d, di:di + w] += mask
        return acc, cnt

    ra, rn = corner_sum(road, htile)
    ca, cn = corner_sum(corridor, htile)
    acc = ra * ROAD_WEIGHT + ca
    cnt = rn * ROAD_WEIGHT + cn
    flat = np.round(acc / np.maximum(cnt, 1.0) / HSTEP) * HSTEP
    height = np.where(cnt[:d, :w] > 0.0, flat[:d, :w], height)

    # ---- base pads -------------------------------------------------------
    # the pad itself is drivable; the bunker core in the middle is solid so
    # nothing can walk through the command building
    for fac, (btx, bty) in BASE_TILES.items():
        i0, j0 = int(btx), int(bty)
        tiles[max(0, j0 - 7):j0 + 8, max(0, i0 - 7):i0 + 8] = TILE_BASE
        tiles[max(0, j0 - 2):j0 + 3, max(0, i0 - 2):i0 + 3] = TILE_SOLID

    # ---- towns -----------------------------------------------------------
    # buildings keep one tile off a straight road but two off a diagonal one:
    # the diagonal piece reaches into the tile corners, so a house beside it
    # would stand in the roadway and wedge a hull against its wall
    straight_road = (roads & ROAD_STRAIGHT) != 0
    diag_road = (roads & ROAD_DIAG) != 0
    no_build = _dilate(straight_road, 1) | _dilate(diag_road, 2)
    walls = np.zeros((d, w), np.uint8)
    towns = []
    clear = road.copy()
    for (tx, ty) in TOWN_SPOTS:
        cx = int(tx + rng.integers(-4, 5))
        cy = int(ty + rng.integers(-4, 5))
        towns.append(dict(x=cx * TILE, z=cy * TILE, tile_x=cx, tile_y=cy,
                          owner=-1, progress=0.0, name=f"T{len(towns):02d}"))
        for _ in range(int(rng.integers(6, 12))):
            bw = int(rng.integers(1, 4))
            bh = int(rng.integers(1, 4))
            bx = int(cx + rng.integers(-8, 9))
            by = int(cy + rng.integers(-8, 9))
            i0, i1 = max(1, bx), min(w - 2, bx + bw)
            j0, j1 = max(1, by), min(d - 2, by + bh)
            if i1 <= i0 or j1 <= j0:
                continue
            if no_build[j0:j1, i0:i1].any():
                continue
            patch = tiles[j0:j1, i0:i1]
            patch[(patch != TILE_ROAD) & (patch != TILE_BASE)] = TILE_BUILDING
            clear[j0:j1, i0:i1] = True
            # one block is one building: its storey count is the wall column
            # every tile of it is raised to, which is also what stops a round
            storeys = int(rng.integers(2, 7))          # 2..6 floors
            walls[j0:j1, i0:i1][patch == TILE_BUILDING] = int(storeys * FLOOR_H)

    # ---- forests ---------------------------------------------------------
    # a thick stand in the middle of every blob (several trees per tile) with a
    # ragged edge of single-tree tiles, plus lone trees scattered over the
    # plains: the countryside reads as groves instead of a painted texture
    for _ in range(460):
        cx = int(rng.integers(6, w - 6))
        cy = int(rng.integers(6, d - 6))
        rad = int(rng.integers(2, 12))
        j0, j1 = max(1, cy - rad), min(d - 1, cy + rad + 1)
        i0, i1 = max(1, cx - rad), min(w - 1, cx + rad + 1)
        jj, ii = np.mgrid[j0:j1, i0:i1]
        r2 = (ii - cx) ** 2 + (jj - cy) ** 2
        inside = r2 <= rad * rad
        core = r2 <= (rad * 0.62) ** 2
        patch = tiles[j0:j1, i0:i1]
        edge = inside & (rng.random(inside.shape) < 0.62)
        patch[edge & (patch == TILE_GRASS)] = TILE_TREE
        patch[core & (patch == TILE_TREE)] = TILE_FOREST

    lone = (rng.random((d, w)) < 0.0045) & (tiles == TILE_GRASS)
    tiles[lone] = TILE_TREE

    # ---- boulders --------------------------------------------------------
    # rock fields instead of hundreds of single marbles scattered evenly over
    # the map: a few outcrops of two to eight boulders each
    for _ in range(80):
        cx = int(rng.integers(6, w - 6))
        cy = int(rng.integers(6, d - 6))
        for _ in range(int(rng.integers(2, 9))):
            rx = cx + int(rng.integers(-4, 5))
            rz = cy + int(rng.integers(-4, 5))
            if 2 <= rx < w - 2 and 2 <= rz < d - 2 and tiles[rz, rx] == TILE_GRASS:
                tiles[rz, rx] = TILE_ROCK

    # keep lanes / towns / base pads clear of obstacles
    keep = _dilate(clear, 3)
    dangerous = (tiles == TILE_TREE) | (tiles == TILE_FOREST) | (tiles == TILE_ROCK)
    tiles[keep & dangerous] = TILE_GRASS
    tiles[road] = TILE_ROAD
    walls[tiles != TILE_BUILDING] = 0

    # ---- world space lane way points -------------------------------------
    lanes = []
    for lane in LANE_TILES:
        pts = []
        for a, b in zip(lane[:-1], lane[1:]):
            seg = float(np.hypot(b[0] - a[0], b[1] - a[1])) * TILE
            n = max(2, int(seg / 60.0))
            for k in range(n):
                t = k / n
                pts.append(((a[0] + (b[0] - a[0]) * t) * TILE,
                            (a[1] + (b[1] - a[1]) * t) * TILE))
        pts.append((lane[-1][0] * TILE, lane[-1][1] * TILE))
        lanes.append([(float(x), float(z)) for x, z in pts])

    bases = {fac: (btx * TILE, bty * TILE) for fac, (btx, bty) in BASE_TILES.items()}
    return Terrain(height, tiles, lanes, towns, bases, roads, walls)


# ---------------------------------------------------------------------------
# weapon range: a 4 km test map with a 200 m hill on the west side and open
# plains on the east, so every weapon can be judged against both a mountainside
# and flat ground from one vantage point
# ---------------------------------------------------------------------------
RANGE_TILES = 250                     # 250 x 16 m = 4 km square
RANGE_SPAWN = (1900.0, 3050.0)        # south centre, the blue cross
RANGE_HILL = (900.0, 1100.0, 200.0)   # centre x, centre z, peak height

# APC posts: static ones hold a spot, patrols drive back and forth along x
RANGE_POSTS = [
    dict(x=950.0, z=1100.0, name="HILL TOP"),
    dict(x=1200.0, z=1450.0, name="HILL SLOPE"),
    dict(x=700.0, z=1750.0, name="HILL WEST"),
    dict(x=1500.0, z=800.0, name="HILL RIDGE"),
    dict(x=2150.0, z=2750.0, name="NEAR"),
    dict(x=2450.0, z=1500.0, name="PLAIN MID"),
    dict(x=3000.0, z=2050.0, name="PLAIN EAST"),
    dict(x=2500.0, z=2450.0, name="PLAIN SOUTH"),
    dict(x=3400.0, z=1200.0, name="PLAIN FAR"),
    dict(x=3350.0, z=2650.0, name="PLAIN SE"),
    dict(x=2500.0, z=1500.0, patrol=(2100.0, 2900.0), name="PATROL MID"),
    dict(x=2900.0, z=2300.0, patrol=(2500.0, 3300.0), name="PATROL EAST"),
]


def generate_range(seed: int = 7) -> Terrain:
    """Flat firing range: one big hill NW, plains everywhere else, no cover."""
    rng = np.random.default_rng(seed)
    n = RANGE_TILES
    yy, xx = np.mgrid[0:n, 0:n].astype(np.float32)
    xm = xx * TILE
    zm = yy * TILE

    hx, hz, peak = RANGE_HILL
    dx = xm - hx
    dz = zm - hz
    r = np.hypot(dx, dz)
    # a wobbly radius keeps the hill from being a perfect cone; the terracing
    # below turns the slopes into the same faceted contours as the battle map
    ang = np.arctan2(dz, dx)
    wobble = 1.0 + 0.16 * np.sin(3.0 * ang + 0.7) + 0.09 * np.sin(5.0 * ang - 1.3)
    radius = 980.0 * wobble
    k = np.clip(1.0 - r / radius, 0.0, 1.0)
    k = k * k * (3.0 - 2.0 * k)                       # smoothstep falloff
    height = peak * k

    # a low spur south of the main hill and a gentle shoulder north of it
    for (sx, sz, sp, sr) in ((600.0, 2100.0, 45.0, 520.0), (1450.0, 420.0, 70.0, 600.0)):
        sk = np.clip(1.0 - np.hypot(xm - sx, zm - sz) / sr, 0.0, 1.0)
        height += sp * (sk * sk * (3.0 - 2.0 * sk))

    # plains: a couple of metres of undulation so the ground is not a mirror
    plains = _fbm(rng, n, n, octaves=4) * 9.0
    blend = np.clip((xm - 1500.0) / 1600.0, 0.0, 1.0)
    height = height + plains * (0.25 + 0.75 * blend)
    height = _terrace(_blur3(_blur3(height)))

    tiles = np.zeros((n, n), np.uint8)                # grass everywhere
    bases = {fac: RANGE_SPAWN for fac in (0, 1)}
    return Terrain(height, tiles, [], [], bases)
