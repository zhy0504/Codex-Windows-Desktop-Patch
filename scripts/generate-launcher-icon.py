from math import cos, radians, sin
from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "resources" / "launcher"
SVG = OUTPUT / "CodexPatchLauncher.svg"
PNG = OUTPUT / "CodexPatchLauncher.png"
ICO = OUTPUT / "CodexPatchLauncher.ico"
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

CANVAS = 1024
CENTER = (512, 512)
DARK = "#172027"
TEAL = "#35bea9"
GOLD = "#f5b942"


def polar(radius, angle, tangent=0):
    theta = radians(angle)
    radial = (cos(theta), sin(theta))
    tangent_vector = (-sin(theta), cos(theta))
    return (
        CENTER[0] + radial[0] * radius + tangent_vector[0] * tangent,
        CENTER[1] + radial[1] * radius + tangent_vector[1] * tangent,
    )


def arc_points(radius, start, end, steps):
    return [
        polar(radius, start + (end - start) * index / steps)
        for index in range(steps + 1)
    ]


def sector_points(outer_radius, inner_radius, start, end, steps=96):
    points = arc_points(outer_radius, start, end, steps)
    points.extend(arc_points(inner_radius, end, start, steps))
    return points


def patch_points(detail="full"):
    points = [polar(370, 245)]
    points.extend(arc_points(370, 245, 285, 44)[1:])
    points.append(polar(230, 285))
    points.extend(arc_points(230, 285, 245, 32)[1:])
    points.append(polar(230, 245))

    if detail == "full":
        points.extend(
            (
                polar(255, 245),
                polar(275, 245, -28),
                polar(305, 245, -28),
                polar(325, 245),
            )
        )
    elif detail == "medium":
        points.extend((polar(258, 245), polar(286, 245, -24), polar(314, 245)))

    return points


def scaled_points(points, scale):
    return [(round(x * scale), round(y * scale)) for x, y in points]


def ellipse_at(center, radius, scale):
    x, y = center
    return tuple(
        round(value * scale)
        for value in (x - radius, y - radius, x + radius, y + radius)
    )


def render(size, detail=None):
    if detail is None:
        detail = "simple" if size <= 20 else "medium" if size <= 32 else "full"

    supersample = max(4, min(16, 256 // max(size, 1)))
    work_size = size * supersample
    scale = work_size / CANVAS
    image = Image.new("RGBA", (work_size, work_size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    draw.polygon(scaled_points(sector_points(370, 230, 335, 605), scale), fill=TEAL)
    draw.ellipse(ellipse_at(polar(300, 335), 70, scale), fill=TEAL)
    draw.polygon(scaled_points(patch_points(detail), scale), fill=GOLD)
    draw.ellipse(ellipse_at(polar(300, 285), 70, scale), fill=GOLD)

    rim_width = 42 if size <= 32 else 34
    rim_outer = 230
    rim_inner = rim_outer - rim_width
    rim_center = (rim_outer + rim_inner) / 2
    rim_radius = rim_width / 2
    draw.polygon(
        scaled_points(sector_points(rim_outer, rim_inner, 335, 645), scale),
        fill=DARK,
    )
    draw.ellipse(ellipse_at(polar(rim_center, 335), rim_radius, scale), fill=DARK)
    draw.ellipse(ellipse_at(polar(rim_center, 645), rim_radius, scale), fill=DARK)

    return image.resize((size, size), Image.Resampling.LANCZOS)


def svg_point(point):
    return f"{point[0]:.2f} {point[1]:.2f}"


def sector_path(outer_radius, inner_radius, start, end):
    large_arc = 1 if abs(end - start) > 180 else 0
    return " ".join(
        (
            f"M{svg_point(polar(outer_radius, start))}",
            f"A{outer_radius} {outer_radius} 0 {large_arc} 1 {svg_point(polar(outer_radius, end))}",
            f"L{svg_point(polar(inner_radius, end))}",
            f"A{inner_radius} {inner_radius} 0 {large_arc} 0 {svg_point(polar(inner_radius, start))}",
            "Z",
        )
    )


def svg_patch_path():
    commands = [
        f"M{svg_point(polar(370, 245))}",
        f"A370 370 0 0 1 {svg_point(polar(370, 285))}",
        f"L{svg_point(polar(230, 285))}",
        f"A230 230 0 0 0 {svg_point(polar(230, 245))}",
    ]
    commands.extend(
        f"L{svg_point(point)}"
        for point in (
            polar(255, 245),
            polar(275, 245, -28),
            polar(305, 245, -28),
            polar(325, 245),
        )
    )
    commands.append("Z")
    return " ".join(commands)


def svg_document():
    teal_cap = polar(300, 335)
    gold_cap = polar(300, 285)
    rim_start = polar(213, 335)
    rim_end = polar(213, 645)
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024">
  <path d="{sector_path(370, 230, 335, 605)}" fill="{TEAL}"/>
  <circle cx="{teal_cap[0]:.2f}" cy="{teal_cap[1]:.2f}" r="70" fill="{TEAL}"/>
  <path d="{svg_patch_path()}" fill="{GOLD}"/>
  <circle cx="{gold_cap[0]:.2f}" cy="{gold_cap[1]:.2f}" r="70" fill="{GOLD}"/>
  <path d="{sector_path(230, 196, 335, 645)}" fill="{DARK}"/>
  <circle cx="{rim_start[0]:.2f}" cy="{rim_start[1]:.2f}" r="17" fill="{DARK}"/>
  <circle cx="{rim_end[0]:.2f}" cy="{rim_end[1]:.2f}" r="17" fill="{DARK}"/>
</svg>
"""


def write_ico(path, frames):
    frames[-1].save(
        path,
        "ICO",
        sizes=[frame.size for frame in frames],
        append_images=frames[:-1],
    )


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    master = render(1024, "full")
    frames = [render(size) for size in ICO_SIZES]
    SVG.write_text(svg_document(), encoding="utf-8")
    master.save(PNG, "PNG", optimize=True)
    write_ico(ICO, frames)
    with Image.open(ICO) as icon:
        saved_sizes = sorted(icon.ico.sizes())
    print(f"SVG: {SVG}")
    print(f"PNG: {PNG}")
    print(f"ICO: {ICO}")
    print(f"Frames: {saved_sizes}")


if __name__ == "__main__":
    main()
