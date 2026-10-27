#!/usr/bin/env python3
"""Generate the turbine-hover mech design plates as deterministic JPEG files.

This first pass intentionally renders only the silhouette draft.  The model is
made from coarse code-defined meshes; no game assets or game code are changed.
Run from any directory with: python3 doc/design/build.py
"""

from __future__ import annotations

from dataclasses import dataclass
from functools import reduce
from math import cos, pi, sin, sqrt
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent
OUT = ROOT / "silhouette-draft.jpg"

BG = "#f5f7fa"
INK = "#4169e1"
FAINT = "#d8e0ee"
MUTED = "#78859a"
DARK = "#26364d"
WHITE = "#fbfcfe"
SS = 2

Vec3 = tuple[float, float, float]
Color = str | None


def add(a: Vec3, b: Vec3) -> Vec3:
    return a[0] + b[0], a[1] + b[1], a[2] + b[2]


def sub(a: Vec3, b: Vec3) -> Vec3:
    return a[0] - b[0], a[1] - b[1], a[2] - b[2]


def mul(a: Vec3, k: float) -> Vec3:
    return a[0] * k, a[1] * k, a[2] * k


def dot(a: Vec3, b: Vec3) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def cross(a: Vec3, b: Vec3) -> Vec3:
    return (
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )


def unit(a: Vec3) -> Vec3:
    length = sqrt(dot(a, a))
    return a if length == 0 else mul(a, 1 / length)


@dataclass
class Mesh:
    vertices: list[Vec3]
    faces: list[tuple[int, ...]]
    fill: Color = WHITE
    width: float = 1.35


class Sheet:
    def __init__(self, width: int, height: int):
        self.width = width
        self.height = height
        self.image = Image.new("RGB", (width * SS, height * SS), BG)
        self.draw = ImageDraw.Draw(self.image)
        self.fonts: dict[int, ImageFont.FreeTypeFont] = {}
        self._grid()

    def font(self, size: int) -> ImageFont.FreeTypeFont:
        key = size * SS
        if key not in self.fonts:
            self.fonts[key] = ImageFont.load_default(size=key)
        return self.fonts[key]

    def _grid(self) -> None:
        for y in range(22, self.height, 26):
            for x in range(22, self.width, 26):
                self.draw.ellipse(
                    ((x * SS - 1, y * SS - 1), (x * SS + 1, y * SS + 1)),
                    fill=FAINT,
                )

    def line(self, points, fill=INK, width=1.0) -> None:
        self.draw.line(
            [(round(x * SS), round(y * SS)) for x, y in points],
            fill=fill,
            width=max(1, round(width * SS)),
            joint="curve",
        )

    def polygon(self, points, fill=WHITE, outline=INK, width=1.0) -> None:
        pts = [(round(x * SS), round(y * SS)) for x, y in points]
        if fill:
            self.draw.polygon(pts, fill=fill)
        if outline:
            self.draw.line(pts + [pts[0]], fill=outline, width=max(1, round(width * SS)), joint="curve")

    def text(self, xy, text, size=16, fill=DARK, anchor="la", bold=False) -> None:
        self.draw.text(
            (round(xy[0] * SS), round(xy[1] * SS)),
            text,
            font=self.font(size),
            fill=fill,
            anchor=anchor,
            stroke_width=1 if bold else 0,
            stroke_fill=fill,
        )

    def dashed(self, a, b, fill=MUTED, width=0.8, dash=7, gap=6) -> None:
        dx, dy = b[0] - a[0], b[1] - a[1]
        length = sqrt(dx * dx + dy * dy)
        if not length:
            return
        ux, uy = dx / length, dy / length
        at = 0.0
        while at < length:
            end = min(at + dash, length)
            self.line(
                [(a[0] + ux * at, a[1] + uy * at), (a[0] + ux * end, a[1] + uy * end)],
                fill,
                width,
            )
            at += dash + gap

    def save(self, path: Path) -> None:
        result = self.image.resize((self.width, self.height), Image.Resampling.LANCZOS)
        result.save(path, "JPEG", quality=95, subsampling=0, dpi=(180, 180))


def loft_z(sections: list[tuple[float, float, float, float, float]], fill=WHITE) -> Mesh:
    """Faceted hull loft. Sections are x, y, z, half-width, half-height."""
    vertices: list[Vec3] = []
    ring = [(-0.72, 1), (0.72, 1), (1, 0.42), (1, -0.42),
            (0.72, -1), (-0.72, -1), (-1, -0.42), (-1, 0.42)]
    for x, y, z, w, h in sections:
        vertices.extend((x + rx * w, y + ry * h, z) for rx, ry in ring)
    faces: list[tuple[int, ...]] = [tuple(reversed(range(8)))]
    for row in range(len(sections) - 1):
        a, b = row * 8, (row + 1) * 8
        for i in range(8):
            j = (i + 1) % 8
            faces.append((a + i, a + j, b + j, b + i))
    faces.append(tuple((len(sections) - 1) * 8 + i for i in range(8)))
    return Mesh(vertices, faces, fill)


def box(center: Vec3, size: Vec3, fill=WHITE) -> Mesh:
    x, y, z = center
    sx, sy, sz = (v / 2 for v in size)
    vertices = [
        (x + dx * sx, y + dy * sy, z + dz * sz)
        for dx, dy, dz in [(-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
                            (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)]
    ]
    faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4),
             (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    return Mesh(vertices, faces, fill)


def cylinder(a: Vec3, b: Vec3, radius: float, sides=12, fill=WHITE, radius_b=None) -> Mesh:
    axis = unit(sub(b, a))
    reference = (0.0, 1.0, 0.0) if abs(axis[1]) < 0.9 else (1.0, 0.0, 0.0)
    u = unit(cross(axis, reference))
    v = unit(cross(axis, u))
    rb = radius if radius_b is None else radius_b
    vertices = []
    for center, r in [(a, radius), (b, rb)]:
        for i in range(sides):
            angle = 2 * pi * i / sides
            offset = add(mul(u, cos(angle) * r), mul(v, sin(angle) * r))
            vertices.append(add(center, offset))
    faces = [tuple(reversed(range(sides))), tuple(range(sides, sides * 2))]
    for i in range(sides):
        j = (i + 1) % sides
        faces.append((i, j, sides + j, sides + i))
    return Mesh(vertices, faces, fill)


def beam(a: Vec3, b: Vec3, width: float, depth: float, fill=WHITE) -> Mesh:
    axis = unit(sub(b, a))
    reference = (0.0, 0.0, 1.0) if abs(axis[2]) < 0.9 else (1.0, 0.0, 0.0)
    u = unit(cross(axis, reference))
    v = unit(cross(axis, u))
    vertices = []
    for center in [a, b]:
        for du, dv in [(-1, -1), (1, -1), (1, 1), (-1, 1)]:
            vertices.append(add(center, add(mul(u, du * width / 2), mul(v, dv * depth / 2))))
    faces = [(3, 2, 1, 0), (4, 5, 6, 7), (0, 1, 5, 4),
             (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    return Mesh(vertices, faces, fill)


def translated(mesh: Mesh, delta: Vec3) -> Mesh:
    return Mesh([add(p, delta) for p in mesh.vertices], mesh.faces, mesh.fill, mesh.width)


def mech_meshes() -> list[Mesh]:
    meshes: list[Mesh] = []

    # Revision 02: the long center spine packages two vertical turbines.
    meshes.append(loft_z([
        (0, 3.55, -2.95, 0.48, 0.66),
        (0, 3.64, -2.55, 0.72, 0.96),
        (0, 3.65, -1.50, 0.84, 1.08),
        (0, 3.62, -0.15, 0.86, 1.08),
        (0, 3.55, 1.20, 0.72, 0.95),
        (0, 3.35, 2.48, 0.36, 0.55),
    ]))
    # Sloped, fully armored cockpit; there is no glazed helicopter bubble.
    meshes.append(loft_z([
        (0, 3.95, 0.55, 0.66, 0.75),
        (0, 3.84, 1.52, 0.58, 0.70),
        (0, 3.40, 2.56, 0.28, 0.38),
    ]))

    # Low tank-style hatch, with an M1X-inspired armored sight behind it.
    meshes.append(cylinder((0, 4.65, 1.00), (0, 4.79, 1.00), 0.43, 16))
    meshes.append(cylinder((0, 4.67, 0.16), (0, 4.84, 0.16), 0.25, 12))
    meshes.append(loft_z([
        (0, 5.10, -0.10, 0.24, 0.27),
        (0, 5.12, 0.18, 0.31, 0.35),
        (0, 5.08, 0.49, 0.26, 0.29),
    ]))
    meshes.append(cylinder((-0.09, 5.11, 0.47), (-0.09, 5.11, 0.55), 0.13, 12, fill="#e4e9f2"))
    meshes.append(cylinder((0.17, 5.13, 0.48), (0.17, 5.13, 0.54), 0.060, 10, fill="#e4e9f2"))

    # Two vertical, tandem top intakes define the long rear engine deck.
    for z in (-0.72, -1.91):
        meshes.append(cylinder((0, 4.67, z), (0, 4.84, z), 0.52, 20))
        meshes.append(cylinder((0, 4.835, z), (0, 4.86, z), 0.39, 20, fill="#e4e9f2"))

    # Smaller rear propulsion nozzle, still on the centerline.
    meshes.append(cylinder((0, 3.55, -2.70), (0, 3.55, -3.24), 0.29, 16, radius_b=0.40))
    meshes.append(cylinder((0, 3.55, -3.25), (0, 3.55, -3.29), 0.32, 16, fill="#e4e9f2"))

    # High, close-in pylons retain the first draft's basic mounting shape.
    for side in (-1, 1):
        meshes.append(beam((side * 0.65, 4.18, 0.30), (side * 1.39, 4.18, 0.30), 0.22, 0.34))

    # Left hand: autocannon.
    meshes.append(loft_z([
        (-1.42, 4.20, -0.50, 0.20, 0.27),
        (-1.42, 4.20, 0.10, 0.26, 0.35),
        (-1.42, 4.20, 1.16, 0.20, 0.28),
    ]))
    meshes.append(cylinder((-1.42, 4.22, 0.98), (-1.42, 4.22, 3.94), 0.085, 10))
    meshes.append(cylinder((-1.42, 4.22, 3.72), (-1.42, 4.22, 4.12), 0.14, 10))

    # Right hand: high-mounted rocket pod with a visible seven-tube face.
    meshes.append(loft_z([
        (1.43, 4.22, -0.60, 0.28, 0.32),
        (1.43, 4.22, 0.15, 0.37, 0.43),
        (1.43, 4.22, 1.43, 0.37, 0.43),
    ]))
    for dx, dy in [(0, 0), (-0.17, 0.14), (0.17, 0.14), (-0.17, -0.14),
                   (0.17, -0.14), (0, 0.26), (0, -0.26)]:
        meshes.append(cylinder((1.43 + dx, 4.22 + dy, 1.42),
                               (1.43 + dx, 4.22 + dy, 1.49), 0.070, 10, fill="#e4e9f2"))

    body_count = len(meshes)

    # Draft 04: raise the hip mounts and move the complete support stance outboard.
    for side in (-1, 1):
        x = side * 1.14
        hip = (x, 2.20, -0.42)
        knee = (side * 1.27, 1.40, 0.60)
        ankle = (side * 1.32, 0.69, -0.08)
        meshes.append(cylinder((side * 0.72, 2.20, -0.42), (x + side * 0.24, 2.20, -0.42), 0.30, 10))
        meshes.append(beam(hip, knee, 0.82, 1.00))
        meshes.append(cylinder((side * 0.95, 1.40, 0.60), (side * 1.51, 1.40, 0.60), 0.30, 10))
        meshes.append(beam(knee, ankle, 0.78, 0.92))
        meshes.append(cylinder((side * 1.06, 0.69, -0.08), (side * 1.58, 0.69, -0.08), 0.26, 10))
        # The planform is ski-like, but the section is a thick air chamber.
        meshes.append(loft_z([
            (side * 1.32, 0.34, -2.65, 0.38, 0.20),
            (side * 1.32, 0.34, -2.34, 0.59, 0.25),
            (side * 1.32, 0.34, 2.18, 0.60, 0.25),
            (side * 1.32, 0.34, 2.65, 0.40, 0.20),
        ]))
        meshes.append(loft_z([
            (side * 1.32, 0.12, -2.53, 0.36, 0.09),
            (side * 1.32, 0.12, -2.30, 0.54, 0.10),
            (side * 1.32, 0.12, 2.14, 0.55, 0.10),
            (side * 1.32, 0.12, 2.52, 0.37, 0.09),
        ], fill="#e4e9f2"))

    # The upper assembly retains its lowered draft-03 position.
    for mesh in meshes[:body_count]:
        mesh.vertices = [(x, y - 0.65, z) for x, y, z in mesh.vertices]
    return meshes


class Camera:
    def __init__(self, eye: Vec3, target: Vec3, center, scale: float):
        self.target = target
        self.view = unit(sub(target, eye))
        self.right = unit(cross(self.view, (0.0, 1.0, 0.0)))
        self.up = unit(cross(self.right, self.view))
        self.center = center
        self.scale = scale

    def project(self, point: Vec3):
        p = sub(point, self.target)
        return (
            self.center[0] + dot(p, self.right) * self.scale,
            self.center[1] - dot(p, self.up) * self.scale,
        )

    def depth(self, point: Vec3) -> float:
        return dot(sub(point, self.target), self.view)


def draw_model(sheet: Sheet, meshes: list[Mesh], camera: Camera) -> None:
    faces = []
    for mesh in meshes:
        for indices in mesh.faces:
            points = [mesh.vertices[i] for i in indices]
            depth = sum(camera.depth(p) for p in points) / len(points)
            faces.append((depth, mesh, points))
    # Far faces first; near white faces hide hidden construction lines.
    faces.sort(key=lambda item: item[0], reverse=True)
    for _, mesh, points in faces:
        sheet.polygon([camera.project(p) for p in points], mesh.fill, INK, mesh.width)


def ground_line(sheet: Sheet, camera: Camera, z=0.0, span=4.0) -> None:
    sheet.dashed(camera.project((-span, 0, z)), camera.project((span, 0, z)), FAINT, 0.8, 10, 7)


def scale_bar(sheet: Sheet, x: float, y: float, pixels_per_metre: float) -> None:
    length = pixels_per_metre
    sheet.line([(x, y), (x + length, y)], MUTED, 1)
    sheet.line([(x, y - 5), (x, y + 5)], MUTED, 1)
    sheet.line([(x + length, y - 5), (x + length, y + 5)], MUTED, 1)
    sheet.text((x + length / 2, y + 12), "1 m", 13, MUTED, "ma")


def render() -> None:
    sheet = Sheet(2400, 1260)
    sheet.text((70, 53), "TH-6 / TURBINE HOVER MECH", 34, DARK, bold=True)
    sheet.text((72, 101), "SILHOUETTE DRAFT 04  /  RAISED OUTBOARD DUCT LEGS + BALANCED SUPPORT STANCE", 15, MUTED)
    sheet.text((2330, 60), "CONCEPT / 004", 18, INK, "ra", bold=True)
    sheet.line([(70, 132), (2330, 132)], INK, 1.2)

    meshes = mech_meshes()
    front = Camera((0, 2.10, 20), (0, 2.10, 0.2), (365, 720), 96)
    side = Camera((20, 2.10, 0.4), (0, 2.10, 0.4), (1120, 720), 96)
    iso = Camera((11, 7.2, 14), (0, 2.10, 0.30), (1930, 725), 96)

    sheet.text((110, 181), "01 / FRONT", 18, DARK, bold=True)
    sheet.text((770, 181), "02 / RIGHT SIDE", 18, DARK, bold=True)
    sheet.text((1635, 181), "03 / FRONT THREE-QUARTER", 18, DARK, bold=True)
    sheet.text((110, 211), "high cannon / rocket pylons; narrow center body", 14, MUTED)
    sheet.text((770, 211), "two vertical intakes; compact rear nozzle", 14, MUTED)
    sheet.text((1635, 211), "thick duct legs / flat cushion chambers", 14, MUTED)

    for camera in (front, side, iso):
        draw_model(sheet, meshes, camera)

    # Ground references and minimal overall dimensions.
    sheet.dashed((125, 922), (605, 922), MUTED, 0.8, 10, 7)
    sheet.dashed((745, 922), (1495, 922), MUTED, 0.8, 10, 7)
    sheet.dashed((1620, 970), (2240, 970), MUTED, 0.8, 10, 7)
    sheet.text((365, 1005), "BODY 1.72 m  /  CUSHION FOOTPRINT 3.84 m", 14, MUTED, "ma")
    sheet.text((1120, 1005), "HIP MOUNTS +0.20 m / STANCE +0.40 m", 14, MUTED, "ma")
    sheet.text((1930, 1040), "RAISED OUTBOARD LEGS / BROAD FLAT AIR CUSHIONS", 14, MUTED, "ma")
    scale_bar(sheet, 155, 1102, 96)
    scale_bar(sheet, 815, 1102, 96)

    sheet.line([(70, 1175), (2330, 1175)], INK, 1)
    sheet.text((70, 1203), "ORTHOGRAPHIC STUDY / NOT FINAL ENGINEERING GEOMETRY", 13, MUTED)
    sheet.text((2330, 1203), "CODE-DRAWN / NO GAME ASSETS", 13, MUTED, "ra")
    sheet.save(OUT)
    print(f"wrote {OUT.relative_to(ROOT.parent.parent)}")


if __name__ == "__main__":
    render()
