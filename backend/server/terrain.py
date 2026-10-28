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
INV_TILE = 1.0 / TILE
TRUNK_R = 0.5           # trunk radius of a tree: the only thing a round or a
                        # hull actually meets in a tree tile (see _trunks)
FLOOR_H = 4.0           # one storey of a town building
CULL_TILES = 8          # coarse ray-cull block: 8 tiles = 128 m (see _build_cull)

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

# Every town sits on a lane: a depot only supplies while its lane is unbroken
# back to its own base, so a town off the lanes would be a dead capture point.
# The list mirrors itself across the x = z diagonal (the mid lane), which is
# what keeps the two sides' holdings symmetric.
TOWN_SPOTS = [
    (270, 188), (390, 188), (520, 188), (650, 188), (780, 188),
    (188, 270), (188, 390), (188, 520), (188, 650), (188, 780),
    (812, 270), (812, 390), (812, 520), (812, 650), (812, 780),
    (270, 812), (390, 812), (520, 812), (650, 812), (780, 812),
    (350, 350), (440, 440), (560, 560), (650, 650), (740, 740),
]

# Towns are *grown*, the way OpenTTD grows a city: a front runs a few tiles of
# street, turns or dies and drops a branch.  Then the network is opened up -
# side streets punched across the long blocks and every loose end joined to
# another street - and a few dozen buildings are put up along it.  Each town
# draws its own block width, run lengths, growth budget and house count, so no
# two read alike - and the arterial it sits on is never touched: the lane is
# straight and it stays so.
TOWN_RUNS = (85, 140)        # street runs (2-6 tiles each) per town
TOWN_RADIUS = (10, 14)       # tiles a town grows out from its middle
TOWN_BLOCK = (2, 4)          # tiles between two parallel streets
TOWN_HOUSES = (120, 200)     # buildings per town: rows along the streets, gaps between them
TOWN_PLAZA = 5               # tiles around the middle kept clear of buildings
TOWN_REACH = 9               # how far a dead end looks for a street to join
TOWN_RUNG = 4                # tiles between the side streets off a long street



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


# ---------------------------------------------------------------------------
# towns: grown from the middle out, the way OpenTTD grows a city
# ---------------------------------------------------------------------------
_DIRS = ((1, 0), (0, 1), (-1, 0), (0, -1))          # +x, +z, -x, -z
_BITS = (ROAD_E, ROAD_S, ROAD_W, ROAD_N)


def _on_lane(x: float, y: float, rng) -> tuple[int, int, float, float]:
    """Snap a town spot onto its lane, then slide it a little along the road.

    A town is a town because the road goes through it: its middle sits on the
    arterial, so the lane runs straight past the market square and the streets
    grow out of it.  A spot off the lane would be a detour nobody drives
    through, and the depot would never be worth capturing.  Also returns the
    lane direction, so the streets know which side of it they are on.
    """
    best = (1e18, float(x), float(y), 0.0, 0.0)
    for lane in LANE_TILES:
        for a, b in zip(lane[:-1], lane[1:]):
            vx, vy = b[0] - a[0], b[1] - a[1]
            ll = vx * vx + vy * vy
            u = max(0.0, min(1.0, ((x - a[0]) * vx + (y - a[1]) * vy) / ll))
            px, py = a[0] + vx * u, a[1] + vy * u
            dd = (px - x) ** 2 + (py - y) ** 2
            if dd < best[0]:
                n = ll ** 0.5
                best = (dd, px, py, vx / n, vy / n)
    _, px, py, ux, uy = best
    k = rng.uniform(-3.0, 3.0)
    return int(round(px + ux * k)), int(round(py + uy * k)), ux, uy


def _pave(roads: np.ndarray, i: int, j: int, bits: int) -> None:
    """Lay road pieces into one tile and join every neighbour they reach.

    The client draws one quad per piece and per tile, so a street that ends on
    another road has to give *that* tile the piece facing back - otherwise the
    last few metres before the junction are missing.  Joining an existing road
    is also how a town closes a block: the walk that runs into a street it
    built earlier simply connects to it.
    """
    roads[j, i] |= bits
    for k, (dx, dy) in enumerate(_DIRS):
        if bits & _BITS[k] and roads[j + dy, i + dx]:
            roads[j + dy, i + dx] |= _BITS[(k + 2) % 4]


def _street_ok(roads: np.ndarray, tiles: np.ndarray, i: int, j: int,
               dx: int, dy: int, spacing: int) -> bool:
    """Whether the town may run a street through (i, j) heading (dx, dy).

    Free grass only, and never closer than ``spacing`` tiles to a street that
    runs the same way: that minimum block width is what turns a town into
    blocks of houses instead of a woven mat of asphalt.
    """
    if tiles[j, i] != TILE_GRASS:
        return False
    horiz = dy == 0
    par = (ROAD_E | ROAD_W) if horiz else (ROAD_N | ROAD_S)
    px, py = (0, 1) if horiz else (1, 0)
    for k in range(1, spacing + 1):
        for s in (1, -1):
            if roads[j + py * k * s, i + px * k * s] & par:
                return False
    return True


def _town_streets(roads: np.ndarray, tiles: np.ndarray, rng,
                  cx: int, cy: int, lane: tuple[float, float],
                  radius: int, spacing: int, runs: int) -> None:
    """Grow the street network of one town out from its middle.

    A street is grown the way OpenTTD grows one: a front runs a few tiles
    straight, then either carries on, turns a corner or dies and drops a
    branch off behind it - and a front that runs into a street it built
    earlier simply joins it, which is what closes a block.  The fronts start
    in the middle and are re-opened on any street that still has room to
    grow, so a town that ran out of fronts on one side carries on on another.

    Every town draws its own block width, its own run lengths and its own
    branching odds, so no two networks read alike; the arterial is never
    touched - nothing here removes a road piece, and ``_street_ok`` keeps the
    town's own streets off a parallel course right next to it.
    """
    own: list[tuple[int, int]] = []                # the town's own street tiles
    fronts: list[tuple[int, int, int]] = [(cx, cy, k) for k in range(4)]
    # ... and the town hangs off both sides of its lane: a street leaving the
    # arterial at a few points along it is what makes the lane the town's main
    # street instead of a road that happens to pass by
    ux, uy = lane
    for k in (-5, -1, 4, 8, -9, 12):
        si = cx + int(round(ux * k))
        sj = cy + int(round(uy * k))
        for side in (1, -1):
            heads = [h for h in rng.permutation(4)
                     if side * (ux * _DIRS[h][1] - uy * _DIRS[h][0]) > 0.2]
            if not heads:
                continue
            h = int(heads[0])
            fi, fj = si + _DIRS[h][0], sj + _DIRS[h][1]
            if not (0 <= fi < tiles.shape[1] and 0 <= fj < tiles.shape[0]):
                continue
            if roads[fj, fi] or tiles[fj, fi] != TILE_GRASS:
                continue
            _pave(roads, fi, fj, _BITS[h] | _BITS[(h + 2) % 4])
            own.append((fi, fj))
            fronts.append((fi, fj, h))

    def reopen() -> bool:
        """Open a front on a street tile that still has somewhere to go."""
        for _ in range(24):
            i, j = own[int(rng.integers(0, len(own)))]
            for k in rng.permutation(4):
                k = int(k)
                if roads[j, i] & _BITS[k]:
                    continue
                ti, tj = i + _DIRS[k][0], j + _DIRS[k][1]
                if abs(ti - cx) <= radius and abs(tj - cy) <= radius:
                    fronts.append((i, j, k))
                    return True
        return False

    for _ in range(runs):
        if not fronts and not reopen():
            break
        idx = int(rng.integers(0, len(fronts)))
        i, j, head = fronts[idx]
        run = int(rng.integers(2, 7))
        path: list[tuple[int, int, int]] = []      # the tiles this run laid
        while run > 0:
            dx, dy = _DIRS[head]
            ti, tj = i + dx, j + dy
            if abs(ti - cx) > radius or abs(tj - cy) > radius:
                break
            if roads[tj, ti]:
                _pave(roads, i, j, _BITS[head])     # joins, and the front ends
                run = -1
                break
            if not _street_ok(roads, tiles, ti, tj, dx, dy, spacing):
                break
            if not roads[j, i]:
                own.append((i, j))
            _pave(roads, i, j, _BITS[head])
            _pave(roads, ti, tj, _BITS[(head + 2) % 4])
            i, j = ti, tj
            path.append((i, j, head))
            own.append((i, j))
            run -= 1
        if not path:
            # boxed in: turn the front instead of giving up, so a street that
            # runs into a wall carries on beside it rather than dead-ending
            opts = [(head + 1) % 4, (head + 3) % 4]
            rng.shuffle(opts)
            for nh in opts:
                ti, tj = i + _DIRS[nh][0], j + _DIRS[nh][1]
                if (abs(ti - cx) > radius or abs(tj - cy) > radius
                        or roads[j, i] & _BITS[nh]):
                    continue
                if roads[tj, ti] or _street_ok(roads, tiles, ti, tj,
                                               _DIRS[nh][0], _DIRS[nh][1],
                                               spacing):
                    fronts[idx] = (i, j, nh)
                    break
            else:
                fronts.pop(idx)                    # nowhere to go from here
            continue
        r = rng.random()
        if r < 0.5:                                # the front carries on
            fronts[idx] = (i, j, head)
        elif r < 0.82:                             # ... and turns a corner
            fronts[idx] = (i, j, (head + (1 if rng.random() < 0.5 else 3)) % 4)
        else:                                      # the street ends here
            fronts.pop(idx)
        if run < 0:
            continue
        # a side street off the back of the run
        if rng.random() < 0.55:
            bi, bj, bh = path[int(rng.integers(0, len(path)))]
            nh = (bh + (1 if rng.random() < 0.5 else 3)) % 4
            if not roads[bj, bi] & _BITS[nh]:
                fronts.append((bi, bj, nh))


def _pave_to(roads: np.ndarray, a: tuple[int, int], b: tuple[int, int]) -> None:
    """Lay the two pieces that join tile ``a`` to the neighbouring tile ``b``."""
    k = _DIRS.index((b[0] - a[0], b[1] - a[1]))
    _pave(roads, a[0], a[1], _BITS[k])
    _pave(roads, b[0], b[1], _BITS[(k + 2) % 4])


def _town_link(roads: np.ndarray, tiles: np.ndarray, i0: int, j0: int,
               cx: int, cy: int, radius: int, reach: int) -> bool:
    """Pave the shortest free line from the dead end at (i0, j0) to a street.

    A widening search over free ground - houses are walls, open plots are
    passable - and the line is laid along the first street it touches, so the
    way through is the shortest one there is and never runs through a
    building.
    """
    bits = int(roads[j0, i0]) & ROAD_STRAIGHT
    k = _BITS.index(bits)                          # the one way out it has
    back = (i0 + _DIRS[k][0], j0 + _DIRS[k][1])    # ... which is its own street
    here = (i0, j0)
    prev: dict[tuple[int, int], tuple[int, int] | None] = {here: None}
    queue: list[tuple[int, int]] = []
    for dx, dy in _DIRS:
        ti, tj = i0 + dx, j0 + dy
        if (ti, tj) == back:
            continue                               # that road is no way out
        if roads[tj, ti]:
            _pave_to(roads, here, (ti, tj))         # a street right next door
            return True
        if tiles[tj, ti] == TILE_GRASS:
            prev[(ti, tj)] = here
            queue.append((ti, tj))
    head = 0
    while head < len(queue):
        i, j = queue[head]
        head += 1
        for dx, dy in _DIRS:
            ti, tj = i + dx, j + dy
            if abs(ti - i0) > reach or abs(tj - j0) > reach:
                continue
            if abs(ti - cx) > radius + 1 or abs(tj - cy) > radius + 1:
                continue
            if roads[tj, ti]:
                # a street: pave the line back from it to the dead end
                line = [(ti, tj)]
                node: tuple[int, int] | None = (i, j)
                while node is not None:
                    line.append(node)
                    node = prev[node]
                for a, b in zip(line[1:], line):
                    _pave_to(roads, a, b)
                return True
            if tiles[tj, ti] != TILE_GRASS or (ti, tj) in prev:
                continue
            prev[(ti, tj)] = (i, j)
            queue.append((ti, tj))
    return False


def _town_close(roads: np.ndarray, tiles: np.ndarray, rng,
                cx: int, cy: int, radius: int, reach: int) -> None:
    """Run the dead ends of a town on until they reach another street.

    Growth leaves loose ends: a street that stops in the middle of a block, a
    stub behind a house.  A road that leads nowhere is a trap - a mech turns
    into it, finds a wall of buildings and has to reverse out - so every loose
    end gets a way through, found by widening the search a tile at a time over
    free ground until it touches another street.  What stays is what a town is
    supposed to have: cul-de-sacs with buildings all around them, and streets
    that walk out of town into the open field instead of turning back.
    """
    for _ in range(8):                             # closing one frees the next
        ends = []
        for j in range(max(0, cy - radius - 2), min(tiles.shape[0], cy + radius + 3)):
            for i in range(max(0, cx - radius - 2), min(tiles.shape[1], cx + radius + 3)):
                bits = int(roads[j, i]) & ROAD_STRAIGHT
                if bits and not bits & (bits - 1):   # exactly one way out
                    ends.append((i, j))
        if not ends:
            return
        rng.shuffle(ends)
        linked = 0
        for i, j in ends:
            bits = int(roads[j, i]) & ROAD_STRAIGHT
            if not bits or bits & (bits - 1):
                continue                           # closed by the one before
            if _town_link(roads, tiles, i, j, cx, cy, radius, reach):
                linked += 1
                continue
            # nothing to join: a loose end facing open ground runs on out of
            # town instead, the way a town's roads thin out into the fields
            k = (_BITS.index(bits) + 2) % 4        # ... the way it has left
            dx, dy = _DIRS[k]
            ti, tj = i + dx, j + dy
            run = 0
            while (run < 4 and abs(ti - cx) <= radius + 4
                   and abs(tj - cy) <= radius + 4
                   and tiles[tj, ti] == TILE_GRASS and not roads[tj, ti]):
                _pave(roads, i, j, _BITS[k])
                _pave(roads, ti, tj, _BITS[(k + 2) % 4])
                i, j = ti, tj
                ti, tj = i + dx, j + dy
                run += 1
            linked += run > 0
        if not linked:
            return


def _spur(roads: np.ndarray, tiles: np.ndarray, i: int, j: int, k: int,
          cx: int, cy: int, radius: int, reach: int) -> bool:
    """Pave a side street off (i, j) toward the next street over, or nothing.

    The line is scanned before a single piece is laid: if the way through runs
    into a house or into nothing at all the block stays as it is, so this never
    digs a new dead end.
    """
    dx, dy = _DIRS[k]
    line = [(i, j)]
    ti, tj = i + dx, j + dy
    while not roads[tj, ti] and len(line) <= reach:
        if tiles[tj, ti] != TILE_GRASS or abs(ti - cx) > radius + 2 or abs(tj - cy) > radius + 2:
            return False
        line.append((ti, tj))
        ti, tj = ti + dx, tj + dy
    if not roads[tj, ti]:
        return False
    line.append((ti, tj))
    for a, b in zip(line, line[1:]):
        _pave_to(roads, a, b)
    return True


def _town_rungs(roads: np.ndarray, tiles: np.ndarray, rng,
                cx: int, cy: int, radius: int, rung: int, reach: int) -> None:
    """Cut the long streets of a town into blocks.

    A street that runs on for a dozen tiles without a way off it is a canyon:
    the player drives it because there is nothing else to do, and never finds
    the street behind the block.  So every ``rung`` tiles a street that carries
    straight through gets a side street punched across to the next one - only
    where a street is really there to reach, so a block gets opened up rather
    than a cul-de-sac added.
    """
    off = int(rng.integers(0, max(1, rung)))
    i0, i1 = max(0, cx - radius - 2), min(tiles.shape[1], cx + radius + 3)
    j0, j1 = max(0, cy - radius - 2), min(tiles.shape[0], cy + radius + 3)
    for j in range(j0, j1):
        for i in range(i0, i1):
            bits = int(roads[j, i]) & ROAD_STRAIGHT
            if not bits or (bits & (bits - 1)) == 0:
                continue                           # nothing, or a dead end
            if (bits & (ROAD_E | ROAD_W)) == (ROAD_E | ROAD_W) and not bits & (ROAD_N | ROAD_S):
                if (i + off) % rung:
                    continue
                if not _spur(roads, tiles, i, j, 3, cx, cy, radius, reach):
                    _spur(roads, tiles, i, j, 1, cx, cy, radius, reach)
            elif (bits & (ROAD_N | ROAD_S)) == (ROAD_N | ROAD_S) and not bits & (ROAD_E | ROAD_W):
                if (j + off) % rung:
                    continue
                if not _spur(roads, tiles, i, j, 0, cx, cy, radius, reach):
                    _spur(roads, tiles, i, j, 2, cx, cy, radius, reach)


def _town_houses(roads: np.ndarray, tiles: np.ndarray, walls: np.ndarray,
                 clear: np.ndarray, no_build: np.ndarray, rng,
                 cx: int, cy: int, radius: int, count: int) -> int:
    """Put up to ``count`` buildings along the streets of one town.

    Houses are put up in rows, the way a street gets built up: a plot is drawn,
    and then two to five more go up shoulder to shoulder beside it along the
    same frontage before the town moves on to another street.  The rows are
    what makes a town read as a town - a terrace here, a gap with a back
    garden there - and the count is what keeps it from becoming a maze of
    walls, which is also why the plots *not* taken stay open on both sides of
    the street.

    The plots facing the arterial are left to ``no_build``: with a tile or two
    of verge kept clear, the lane the front drives on stays a road with a
    shoulder instead of a corridor between two walls.
    """
    reach = radius + 2
    i0, i1 = max(0, cx - reach), min(tiles.shape[1], cx + reach + 1)
    j0, j1 = max(0, cy - reach), min(tiles.shape[0], cy + reach + 1)
    st = roads[j0:j1, i0:i1] != 0
    sub = tiles[j0:j1, i0:i1]
    h, w = sub.shape
    free = (sub == TILE_GRASS) & ~no_build[j0:j1, i0:i1] & ~st
    jj, ii = np.mgrid[j0:j1, i0:i1]
    # the square in the middle stays open - a disc, not a block, so the town
    # wraps its corners around it: it is where a mech comes down when the town
    # is a forward spawn point, and where the civilians walk
    plaza = np.hypot(ii - cx, jj - cy) <= TOWN_PLAZA
    front = _dilate(st, 1) & ~st & free & ~plaza

    def put(y: int, x: int) -> bool:
        """Raise one building of one to four tiles on a plot that faces a
        street, and return whether there was room for it."""
        r = rng.random()
        bw = bh = 1
        if r > 0.86:
            bw = bh = 2
        elif r > 0.58:
            bw, bh = (2, 1) if rng.random() < 0.5 else (1, 2)
        y0, x0 = min(y, h - bh), min(x, w - bw)
        if (not free[y0:y0 + bh, x0:x0 + bw].all()
                or (sub[y0:y0 + bh, x0:x0 + bw] != TILE_GRASS).any()
                or plaza[y0:y0 + bh, x0:x0 + bw].any()):
            return False
        # the closer to the middle, the taller the block
        d = math.hypot(x + i0 - cx, y + j0 - cy)
        lo, hi = (4, 8) if d < 9.0 else (3, 6) if d < 15.0 else (2, 4)
        storeys = int(rng.integers(lo, hi + 1))
        sub[y0:y0 + bh, x0:x0 + bw] = TILE_BUILDING
        walls[j0 + y0:j0 + y0 + bh, i0 + x0:i0 + x0 + bw] = storeys * FLOOR_H
        clear[j0 + y0:j0 + y0 + bh, i0 + x0:i0 + x0 + bw] = True
        free[y0:y0 + bh, x0:x0 + bw] = False
        return True

    plots = np.argwhere(front)
    rng.shuffle(plots)
    built = 0
    for y, x in plots:
        if built >= count:
            break
        if not front[y, x] or not free[y, x]:
            continue                               # row already ran over it
        # which way does the frontage run? the plot faces the street across
        # one side, so the row continues along the other axis
        along = (1, 0)
        for dx, dy in _DIRS:
            ny, nx = y + dy, x + dx
            if 0 <= ny < h and 0 <= nx < w and st[ny, nx]:
                along = (0, 1) if dy else (1, 0)
                break
        step = 1 if rng.random() < 0.5 else -1
        run = int(rng.integers(2, 6))              # buildings in this row
        py, px = y, x
        while run > 0:
            if built >= count:
                break
            if put(py, px):
                built += 1
                run -= 1
            elif not free[py, px]:
                break
            else:
                run -= 1
            py += along[1] * step
            px += along[0] * step
            if not (0 <= py < h and 0 <= px < w):
                break
    clear[j0:j1, i0:i1] |= plaza
    return built


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
        self._build_cull()

    def _build_cull(self) -> None:
        """Max (ground + obstacle) per ``CULL_TILES`` square block.

        ``_march`` samples every 8 m and each sample costs a bilinear height
        read plus a wall lookup.  A point that is higher than *everything* the
        block can hold cannot be stopped by it, so the sample is skipped - which
        is exact, never a rejection of a real hit: the grid is the maximum of
        the block, so a false "clear" is impossible and ballistics are
        unchanged.  Two grids, because a round is stopped by trunks and walls
        while a line of sight is also cut by canopies (``TREE_H``).
        """
        h = self.h
        d, w = h.shape
        nb = CULL_TILES
        # the vertex grid and the tile grid are the same shape, so one block
        # reduction serves both
        solid = np.zeros_like(h)
        sight = np.zeros_like(h)
        build = self.tiles == TILE_BUILDING
        wall = np.maximum(self.walls.astype(np.float32), BUILDING_H)
        solid[build] = wall[build]
        sight[build] = wall[build]
        trees = (self.tiles == TILE_TREE) | (self.tiles == TILE_FOREST)
        solid[trees] = TREE_H            # a trunk is shorter, TREE_H is safe
        sight[trees] = TREE_H

        win = np.lib.stride_tricks.sliding_window_view
        step = [slice(None, None, nb), slice(None, None, nb)]

        def blocks(a: np.ndarray) -> np.ndarray:
            # max over the block *and* the one row/column of vertices on its far
            # edge (a block of nb tiles spans nb+1 vertices, so a 9x9 window).
            # Exact, not grown by a whole block: a dilated grid would make the
            # cull useless in hilly country, which is where rays are longest.
            return win(a, (nb + 1, nb + 1))[step[0], step[1]].max(axis=(2, 3))

        self._cull_h = blocks(h)
        self._cull_solid = self._cull_h + blocks(solid)
        self._cull_sight = self._cull_h + blocks(sight)
        self._cull_d, self._cull_w = self._cull_h.shape
        self._cull_inv = 1.0 / (TILE * nb)
        self._wmax = self.w - 1.001
        self._dmax = self.d - 1.001

    # -- sampling ---------------------------------------------------------
    def height_at(self, x: float, z: float) -> float:
        """Bilinear height.  This is the hottest scalar in the server (every
        march sample, every hull, every sight line), so it reads the four
        corners with `.item()` and does the blend in python floats: numpy
        scalars cost ~110 ns per read and ~100 ns per arithmetic step, which
        made the function three times slower than the arithmetic it does."""
        fx = min(max(x * INV_TILE, 0.0), self._wmax)
        fz = min(max(z * INV_TILE, 0.0), self._dmax)
        i = int(fx)
        j = int(fz)
        tx = fx - i
        tz = fz - j
        item = self.h.item
        h00 = item(j, i)
        h10 = item(j, i + 1)
        h01 = item(j + 1, i)
        h11 = item(j + 1, i + 1)
        # note: plain python floats (double), so this agrees with the client's
        # float64 copy of the same formula slightly better than float32 did
        return (h00 * (1.0 - tx) + h10 * tx) * (1.0 - tz) + \
               (h01 * (1.0 - tx) + h11 * tx) * tz

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
        # a scaled int, not np.floor(x / TILE): floor on a python float goes
        # through numpy's dispatcher and costs as much as the rest together
        i = int(x * INV_TILE) if x >= 0.0 else -1
        j = int(z * INV_TILE) if z >= 0.0 else -1
        if i < 0 or j < 0 or i >= self.w or j >= self.d:
            return 1e6
        t = self.tiles.item(j, i)
        if t == TILE_BUILDING:
            return self.walls.item(j, i) or BUILDING_H
        if t == TILE_TREE or t == TILE_FOREST:
            for tx, tz, r, h in self._trunks(i, j):
                if (x - tx) ** 2 + (z - tz) ** 2 <= r * r:
                    return h
        return 0.0

    def sight_wall(self, x: float, z: float) -> float:
        """The height a line of sight is cut at: a building up to its roof, a
        tree stand at its canopy - the leaves hide what stands behind them even
        though they stop neither a round nor a hull."""
        i = int(x * INV_TILE) if x >= 0.0 else -1
        j = int(z * INV_TILE) if z >= 0.0 else -1
        if i < 0 or j < 0 or i >= self.w or j >= self.d:
            return 1e6
        t = self.tiles.item(j, i)
        if t == TILE_BUILDING:
            return self.walls.item(j, i) or BUILDING_H
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
        i = int(x * INV_TILE) if x >= 0.0 else -1
        j = int(z * INV_TILE) if z >= 0.0 else -1
        if i < 0 or j < 0 or i >= self.w or j >= self.d:
            return TILE_ROCK
        return self.tiles.item(j, i)

    def blocked(self, x: float, z: float) -> bool:
        """Whether a hull cannot stand at (x, z).  A tree blocks with its trunk
        only, so a hull drives under the canopy of a tree or a whole forest."""
        t = self.tile_type(x, z)
        if t == TILE_TREE or t == TILE_FOREST:
            i = int(x * INV_TILE)
            j = int(z * INV_TILE)
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
                t = self.tiles.item(j, i)
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
        i = int(x * INV_TILE) if x >= 0.0 else -1
        j = int(z * INV_TILE) if z >= 0.0 else -1
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
        t = self.tiles.item(j, i)
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
               trunks: bool = False, cull: np.ndarray | None = None,
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
        # coarse cull: a sample above the maximum of its block is skipped
        cull = self._cull_solid if cull is None else cull
        cdi, cwi, cinv = self._cull_d, self._cull_w, self._cull_inv
        for ta, tb, i, j in self._spans(p0, dx, dz):
            tt = (self._trunk_hit(p0, dx, dy, dz, i, j, ta, tb)
                  if trunks and 0 <= i < self.w and 0 <= j < self.d else None)
            # a span that sits inside one block can be dismissed as a whole:
            # both ends in the same 128 m cell means the cell bounds it, so
            # `min(y)` above the cell maximum proves the whole span clear
            if tt is None:
                ax_ = int((p0[0] + dx * ta) * cinv)
                az_ = int((p0[2] + dz * ta) * cinv)
                bx_ = int((p0[0] + dx * tb) * cinv)
                bz_ = int((p0[2] + dz * tb) * cinv)
                if ax_ == bx_ and az_ == bz_ and 0 <= ax_ < cwi and 0 <= az_ < cdi:
                    ya = p0[1] + dy * ta
                    yb = p0[1] + dy * tb
                    if (ya if ya < yb else yb) > cull[az_, ax_]:
                        prev = tb
                        continue
            n = max(1, int((tb - ta) * dist / step + 0.5))
            for k in range(1, n + 1):
                t = ta + (tb - ta) * k / n
                tx = p0[0] + dx * t
                tz = p0[2] + dz * t
                ty = p0[1] + dy * t
                ci = int(tx * cinv)
                cj = int(tz * cinv)
                if 0 <= ci < cwi and 0 <= cj < cdi and ty > cull[cj, ci]:
                    prev = t
                    continue
                if ty > at(tx, tz):
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
        return self._march(p0, p1, step, self.obstacle_at, trunks=True,
                           cull=self._cull_solid)

    def los(self, a: np.ndarray, b: np.ndarray, step: float = 8.0) -> bool:
        """Whether ``a`` sees ``b``: the ground, the buildings and the tree
        canopies between them decide, not the trunks."""
        hit, _, _ = self._march(a, b, step, self.sight_at, cull=self._cull_sight)
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
    lane_road = roads != 0

    # ---- towns -----------------------------------------------------------
    # A town is grown from its middle out, and it is grown *before* the ground
    # is cut, because its streets have to be part of the corridor: the town
    # sits on terraces like the lanes do.  The arterial is not the town's to
    # touch - nothing below ever removes a road piece and nothing builds on
    # the lane or its verge, so the front's road stays straight and open
    # however the town happens to grow around it.
    straight_road = (roads & ROAD_STRAIGHT) != 0
    diag_road = (roads & ROAD_DIAG) != 0
    # buildings keep one tile off a straight road but two off a diagonal one:
    # the diagonal piece reaches into the tile corners, so a house beside it
    # would stand in the roadway and wedge a hull against its wall
    no_build = _dilate(straight_road, 1) | _dilate(diag_road, 2)
    walls = np.zeros((d, w), np.uint8)
    towns = []
    clear = lane_road.copy()
    for (tx, ty) in TOWN_SPOTS:
        cx, cy, ux, uy = _on_lane(float(tx), float(ty), rng)
        towns.append(dict(x=cx * TILE, z=cy * TILE, tile_x=cx, tile_y=cy,
                          owner=-1, progress=0.0, name=f"T{len(towns):02d}"))
        radius = int(rng.integers(TOWN_RADIUS[0], TOWN_RADIUS[1] + 1))
        _town_streets(roads, tiles, rng, cx, cy, (ux, uy), radius,
                      int(rng.integers(TOWN_BLOCK[0], TOWN_BLOCK[1] + 1)),
                      int(rng.integers(TOWN_RUNS[0], TOWN_RUNS[1] + 1)))
        # cross streets first, then every loose end gets a way out - both
        # before anything is built, so the new streets are open on both sides
        _town_rungs(roads, tiles, rng, cx, cy, radius, TOWN_RUNG, TOWN_REACH)
        _town_close(roads, tiles, rng, cx, cy, radius, TOWN_REACH)
        _town_houses(roads, tiles, walls, clear, no_build, rng, cx, cy,
                     radius, int(rng.integers(TOWN_HOUSES[0], TOWN_HOUSES[1] + 1)))

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

    # keep the asphalt and the town squares clear of obstacles: a trunk in the
    # verge stops a hull, and the square in the middle of a town is where a
    # mech comes down when the town is a forward spawn point
    keep = _dilate(clear, 1) | _dilate(lane_road, 3)
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
