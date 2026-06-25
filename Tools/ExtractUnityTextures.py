#!/usr/bin/env python3
"""
Extract Texture2D assets from Unity serialized assets / CAB files.

RimWorld official UI art is often packed into Unity asset files instead of
expanded Textures folders. Keep the .resource and .resS files next to the main
CAB/assets file, then point this script at the main file.
"""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path

try:
    import UnityPy
except ImportError as exc:
    raise SystemExit(
        "UnityPy is required. Install it with: python -m pip install --user UnityPy"
    ) from exc

from PIL import Image, ImageDraw, ImageFont


@dataclass(frozen=True)
class TextureEntry:
    path_id: int
    name: str
    width: int
    height: int
    format_name: str
    object_ref: object


def safe_name(value: str) -> str:
    cleaned = re.sub(r'[<>:"/\\|?*\x00-\x1f]+', "_", value).strip(" ._")
    return cleaned or "texture"


def load_textures(asset_path: Path) -> list[TextureEntry]:
    env = UnityPy.load(str(asset_path))
    textures: list[TextureEntry] = []
    for obj in env.objects:
        if obj.type.name != "Texture2D":
            continue

        data = obj.read()
        textures.append(
            TextureEntry(
                path_id=int(obj.path_id),
                name=str(getattr(data, "m_Name", "") or ""),
                width=int(getattr(data, "m_Width", 0) or 0),
                height=int(getattr(data, "m_Height", 0) or 0),
                format_name=str(getattr(data, "m_TextureFormat", "")),
                object_ref=obj,
            )
        )

    textures.sort(key=lambda item: (item.name.lower(), item.path_id))
    return textures


def filter_textures(
    textures: list[TextureEntry],
    contains: list[str],
    regex: str | None,
    min_size: int,
) -> list[TextureEntry]:
    result = textures
    if contains:
        lowered = [term.lower() for term in contains]
        result = [
            entry
            for entry in result
            if any(term in entry.name.lower() for term in lowered)
        ]
    if regex:
        pattern = re.compile(regex, re.IGNORECASE)
        result = [entry for entry in result if pattern.search(entry.name)]
    if min_size > 0:
        result = [
            entry
            for entry in result
            if entry.width >= min_size and entry.height >= min_size
        ]
    return result


def print_list(textures: list[TextureEntry]) -> None:
    print(f"{'name':48} {'size':>12} {'format':>8} {'path_id'}")
    print("-" * 90)
    for entry in textures:
        print(
            f"{entry.name[:48]:48} "
            f"{entry.width}x{entry.height: <7} "
            f"{entry.format_name:>8} "
            f"{entry.path_id}"
        )


def read_image(entry: TextureEntry) -> Image.Image:
    data = entry.object_ref.read()
    image = data.image
    if image.mode != "RGBA":
        image = image.convert("RGBA")
    return image


def export_images(textures: list[TextureEntry], out_dir: Path) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    for entry in textures:
        image = read_image(entry)
        target = out_dir / f"{safe_name(entry.name)}_{entry.path_id}.png"
        image.save(target)
        print(target)


def make_sheet(textures: list[TextureEntry], out_path: Path, cell_size: int) -> None:
    if not textures:
        raise SystemExit("No textures matched; sheet was not created.")

    cols = min(6, max(1, len(textures)))
    rows = (len(textures) + cols - 1) // cols
    label_h = 28
    sheet = Image.new("RGBA", (cols * cell_size, rows * (cell_size + label_h)), (28, 28, 28, 255))
    draw = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("arial.ttf", 11)
    except OSError:
        font = ImageFont.load_default()

    for index, entry in enumerate(textures):
        col = index % cols
        row = index // cols
        x = col * cell_size
        y = row * (cell_size + label_h)
        draw.rectangle((x, y, x + cell_size - 1, y + cell_size + label_h - 1), outline=(72, 72, 72, 255))

        image = read_image(entry)
        image.thumbnail((cell_size - 12, cell_size - 12), Image.Resampling.LANCZOS)
        px = x + (cell_size - image.width) // 2
        py = y + (cell_size - image.height) // 2
        sheet.alpha_composite(image, (px, py))

        label = entry.name
        if len(label) > 22:
            label = label[:19] + "..."
        draw.text((x + 6, y + cell_size + 7), label, fill=(210, 210, 210, 255), font=font)

    out_path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out_path)
    print(out_path)


def main() -> int:
    parser = argparse.ArgumentParser(description="List or export Unity Texture2D assets.")
    parser.add_argument("asset", type=Path, help="Main CAB/assets file to read.")
    parser.add_argument("--contains", action="append", default=[], help="Case-insensitive name fragment. Can be repeated.")
    parser.add_argument("--regex", help="Case-insensitive name regex.")
    parser.add_argument("--min-size", type=int, default=0, help="Minimum width and height.")
    parser.add_argument("--limit", type=int, default=0, help="Limit matched textures.")
    parser.add_argument("--out", type=Path, help="Directory for exported PNG files.")
    parser.add_argument("--sheet", type=Path, help="Create a contact sheet PNG.")
    parser.add_argument("--cell-size", type=int, default=128, help="Sheet cell size.")
    parser.add_argument("--list", action="store_true", help="Print matched texture list.")
    args = parser.parse_args()

    if not args.asset.exists():
        raise SystemExit(f"Asset file not found: {args.asset}")

    textures = load_textures(args.asset)
    matches = filter_textures(textures, args.contains, args.regex, args.min_size)
    if args.limit > 0:
        matches = matches[: args.limit]

    if args.list or not (args.out or args.sheet):
        print_list(matches)
        print(f"\nMatched {len(matches)} of {len(textures)} Texture2D assets.")
    if args.out:
        export_images(matches, args.out)
    if args.sheet:
        make_sheet(matches, args.sheet, args.cell_size)

    return 0


if __name__ == "__main__":
    sys.exit(main())
