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

# command bases (tile coordinates)
BASE_TILES = {0: (188.0, 188.0), 1: (812.0, 812.0)}

# lane way points in tile coordinates (three classic lanes on a square map)
LANE_TILES = [
    [(188, 188), (812, 188), (812, 812)],        # north lane
    [(188, 188), (188, 812), (812, 812)],        # west lane
    [(188, 188), (500, 500), (812, 812)],        # mid lane
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


class Terrain:
    def __init__(self, heights: np.ndarray, tiles: np.ndarray, lanes: list,
                 towns: list[dict], bases: dict[int, tuple[float, float]]) -> None:
        self.h = heights.astype(np.float32)      # (d, w) vertex heights
        self.tiles = tiles.astype(np.uint8)      # (d, w)
        self.w = int(heights.shape[1])
        self.d = int(heights.shape[0])
        self.lanes = lanes                       # list[list[(x, z)]] world metres
        self.towns = towns                       # list of dicts
        self.bases = bases                       # faction -> (x, z) metres

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

    def tile_type(self, x: float, z: float) -> int:
        i = int(x / TILE)
        j = int(z / TILE)
        if i < 0 or j < 0 or i >= self.w or j >= self.d:
            return TILE_ROCK
        return int(self.tiles[j, i])

    def blocked(self, x: float, z: float) -> bool:
        return self.tile_type(x, z) in BLOCKING

    def inside(self, x: float, z: float) -> bool:
        """False once a point leaves the map: off the field there is no ground
        to hit, only the edge height ``height_at`` keeps returning."""
        return 0.0 <= x <= (self.w - 1) * TILE and 0.0 <= z <= (self.d - 1) * TILE

    # -- ray casts --------------------------------------------------------
    def ground_hit(self, p0: np.ndarray, p1: np.ndarray,
                   step: float = 8.0,
                   ) -> tuple[bool, float, np.ndarray | tuple[float, float, float]]:
        """Return (hit, t, point) for the first ground contact on the segment."""
        dx = p1[0] - p0[0]
        dy = p1[1] - p0[1]
        dz = p1[2] - p0[2]
        dist = (dx * dx + dy * dy + dz * dz) ** 0.5
        if dist < 1e-4:
            return False, 0.0, p0
        n = max(1, int(dist / step))
        ground0 = self.height_at(p0[0], p0[2]) + 1.0
        if p0[1] <= ground0:
            return True, 0.0, p0
        prev = 0.0
        h = self.h
        tiles = self.tiles
        for k in range(1, n + 1):
            t = k / n
            x = p0[0] + dx * t
            z = p0[2] + dz * t
            y = p0[1] + dy * t
            i = int(x / TILE)
            j = int(z / TILE)
            if 0 <= i < self.w and 0 <= j < self.d:
                ground = h[j, i]
                if tiles[j, i] == TILE_BUILDING:
                    ground += 13.0
                elif tiles[j, i] == TILE_TREE or tiles[j, i] == TILE_FOREST:
                    ground += 10.0
            else:
                ground = 1e6
            if y <= ground:
                a, b = prev, t
                for _ in range(4):
                    m = (a + b) * 0.5
                    my = p0[1] + dy * m
                    mg = self.height_at(p0[0] + dx * m, p0[2] + dz * m)
                    if my > mg:
                        a = m
                    else:
                        b = m
                t = (a + b) * 0.5
                return True, t, (p0[0] + dx * t, p0[1] + dy * t, p0[2] + dz * t)
            prev = t
        return False, 1.0, p1

    def los(self, a: np.ndarray, b: np.ndarray, step: float = 8.0) -> bool:
        hit, _, _ = self.ground_hit(a, b, step)
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
    road = np.zeros((d, w), bool)
    for lane in LANE_TILES:
        for a, b in zip(lane[:-1], lane[1:]):
            n = int(np.hypot(b[0] - a[0], b[1] - a[1]) * 2) + 2
            ts = np.linspace(0.0, 1.0, n, dtype=np.float32)
            i = (a[0] + (b[0] - a[0]) * ts).astype(np.int32)
            j = (a[1] + (b[1] - a[1]) * ts).astype(np.int32)
            ok = (i >= 0) & (i < w) & (j >= 0) & (j < d)
            road[j[ok], i[ok]] = True
    tiles[road] = TILE_ROAD

    # flatten the road corridor so the lanes stay drivable
    flat = _blur3(_blur3(_blur3(height)))
    corridor = _dilate(road, 2)
    height = np.where(corridor, flat, height)

    height = _terrace(height)                       # OpenTTD terraces

    # ---- base pads -------------------------------------------------------
    # the pad itself is drivable; the bunker core in the middle is solid so
    # nothing can walk through the command building
    for fac, (btx, bty) in BASE_TILES.items():
        i0, j0 = int(btx), int(bty)
        tiles[max(0, j0 - 7):j0 + 8, max(0, i0 - 7):i0 + 8] = TILE_BASE
        tiles[max(0, j0 - 2):j0 + 3, max(0, i0 - 2):i0 + 3] = TILE_SOLID

    # ---- towns -----------------------------------------------------------
    towns = []
    clear = road.copy()
    for (tx, ty) in TOWN_SPOTS:
        cx = int(tx + rng.integers(-4, 5))
        cy = int(ty + rng.integers(-4, 5))
        towns.append(dict(x=cx * TILE, z=cy * TILE, tile_x=cx, tile_y=cy,
                          owner=-1, progress=0.0, name=f"T{len(towns):02d}"))
        for _ in range(int(rng.integers(4, 9))):
            bw = int(rng.integers(1, 4))
            bh = int(rng.integers(1, 4))
            bx = int(cx + rng.integers(-8, 9))
            by = int(cy + rng.integers(-8, 9))
            i0, i1 = max(1, bx), min(w - 2, bx + bw)
            j0, j1 = max(1, by), min(d - 2, by + bh)
            if i1 <= i0 or j1 <= j0:
                continue
            patch = tiles[j0:j1, i0:i1]
            patch[(patch != TILE_ROAD) & (patch != TILE_BASE)] = TILE_BUILDING
            clear[j0:j1, i0:i1] = True

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
    return Terrain(height, tiles, lanes, towns, bases)


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
