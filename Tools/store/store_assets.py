#!/usr/bin/env python3
"""Store listings and images for Plural, built from Marketing/.

    python Tools/store/store_assets.py build --version 1.2.3 --build-number 1020300 --out out/store
    python Tools/store/store_assets.py check            # validate the text only
    python Tools/store/store_assets.py logo             # re-render Marketing/brand/icon.png from the vector shape

Output (all under --out):
    android/   fastlane supply layout (metadata/android/<locale>/..., images/...)
    ios/       fastlane deliver layout (metadata/, screenshots/) + AppIcon-1024.png
    macos/     fastlane deliver layout for the Mac App Store
    windows/   Microsoft Store listing text and images per locale
    msix-assets/     scale-qualified MSIX logos (consumed by the Windows packaging step)
    macos.iconset/   PNGs for `iconutil -c icns` (consumed by the macOS packaging step)
    preview/   placeholder screenshots for missing captures (never packaged)
    REPORT.md  what was built, what is missing, warnings

Needs Python 3.11+ and Pillow 10+.
"""
from __future__ import annotations

import argparse
import os
import shutil
import sys
import tomllib
import xml.etree.ElementTree as ET
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parents[2]
MARKETING = ROOT / "Marketing"

# Store text limits (characters). The lowest limit across stores wins for shared fields.
LIMITS = {
    "name": 30,               # Google Play title, Microsoft Store name (App Store unless app_store_name is set)
    "app_store_name": 30,     # App Store / Mac App Store name, if different from name
    "subtitle": 30,           # App Store subtitle
    "short_description": 80,  # Google Play short description
    "description": 4000,      # App Store, Google Play (Microsoft allows 10000)
    "promotional_text": 170,  # App Store
    "keywords": 100,          # App Store, comma-separated
    "release_notes": 500,     # Google Play "What's new" (App Store allows 4000)
}
REQUIRED = ("name", "short_description", "description")
MS_MAX_TERMS, MS_TERM_LEN = 7, 30
MS_MAX_FEATURES, MS_FEATURE_LEN = 20, 200
CAPTION_SOFT_LEN = 40
SCREENSHOT_MAX = {"ios": 10, "mac": 10, "play": 8, "windows": 10}
TRADEMARKS = ("smartmike", "sabinetek")  # allowed in the description, not in name/subtitle/keywords

IMAGE_EXT = {".png", ".jpg", ".jpeg"}


class Report:
    def __init__(self) -> None:
        self.errors: list[str] = []
        self.warnings: list[str] = []
        self.built: list[str] = []
        self.missing: list[str] = []

    def markdown(self, version: str | None) -> str:
        out = [f"# Store assets{f' {version}' if version else ''}", ""]
        for title, items in (("Errors", self.errors), ("Missing before submission", self.missing),
                             ("Warnings", self.warnings), ("Built", self.built)):
            if items:
                out += [f"## {title}", ""] + [f"- {i}" for i in items] + [""]
        return "\n".join(out)


# ---------------------------------------------------------------- config & text

def load_config() -> dict:
    with open(MARKETING / "store.toml", "rb") as f:
        return tomllib.load(f)


def locales() -> list[str]:
    return sorted(p.name for p in MARKETING.iterdir() if p.is_dir() and (p / "name.txt").exists())


def read_text(locale: str, field: str) -> str:
    path = MARKETING / locale / f"{field}.txt"
    if not path.exists():
        return ""
    return path.read_text(encoding="utf-8").replace("\r\n", "\n").strip()


def read_lines(locale: str, field: str) -> list[str]:
    return [line.strip() for line in read_text(locale, field).split("\n") if line.strip()]


def load_texts(locale: str, release_notes_fallback: str, rep: Report) -> dict:
    t = {f: read_text(locale, f) for f in LIMITS}
    t["features"] = read_lines(locale, "features")
    t["captions"] = read_lines(locale, "captions")
    if not t["release_notes"]:
        t["release_notes"] = release_notes_fallback.strip() or "Bug fixes and improvements."
        limit = LIMITS["release_notes"]
        if len(t["release_notes"]) > limit:
            prefix = t["release_notes"][:limit - 3].rsplit(" ", 1)[0].rstrip()
            t["release_notes"] = prefix + "..."
            rep.warnings.append(f"{locale}: GitHub release notes shortened to {limit} characters for stores; "
                                "use release_notes.txt for a custom store summary")
        if not release_notes_fallback.strip():
            rep.warnings.append(f"{locale}: release_notes.txt is empty and no release body was given; "
                                "using \"Bug fixes and improvements.\"")
    return t


def validate(locale: str, t: dict, rep: Report) -> None:
    for field in REQUIRED:
        if not t[field]:
            rep.errors.append(f"{locale}/{field}.txt is required")
    for field, limit in LIMITS.items():
        if len(t[field]) > limit:
            rep.errors.append(f"{locale}/{field}.txt is {len(t[field])} characters; the limit is {limit}")
    for field in ("name", "app_store_name", "subtitle", "keywords"):
        low = t[field].lower()
        if any(tm in low for tm in TRADEMARKS):
            rep.errors.append(f"{locale}/{field}.txt uses a third-party trademark (SmartMike/Sabinetek); "
                              "stores reject that outside the description")
    terms = [k.strip() for k in t["keywords"].split(",") if k.strip()]
    if t["keywords"] and t["name"].lower() in (k.lower() for k in terms):
        rep.warnings.append(f"{locale}: keywords repeat the app name, which the App Store already indexes")
    if ", " in t["keywords"]:
        rep.warnings.append(f"{locale}: keywords contain \", \" - spaces after commas waste App Store characters")
    if len(terms) > MS_MAX_TERMS:
        rep.warnings.append(f"{locale}: Microsoft Store uses only the first {MS_MAX_TERMS} keywords as search terms")
    for term in terms[:MS_MAX_TERMS]:
        if len(term) > MS_TERM_LEN:
            rep.errors.append(f"{locale}: keyword \"{term}\" is longer than {MS_TERM_LEN} characters (Microsoft Store)")
    if len(t["features"]) > MS_MAX_FEATURES:
        rep.errors.append(f"{locale}/features.txt has {len(t['features'])} lines; Microsoft Store allows {MS_MAX_FEATURES}")
    for feat in t["features"]:
        if len(feat) > MS_FEATURE_LEN:
            rep.errors.append(f"{locale}/features.txt: \"{feat[:40]}...\" is longer than {MS_FEATURE_LEN} characters")
    for cap in t["captions"]:
        if len(cap) > CAPTION_SOFT_LEN:
            rep.warnings.append(f"{locale}: caption \"{cap}\" is long; under {CAPTION_SOFT_LEN} characters reads "
                                "best on a phone-sized thumbnail")


# ---------------------------------------------------------------- drawing helpers

def hex_rgb(value: str) -> tuple[int, int, int]:
    value = value.lstrip("#")
    return tuple(int(value[i:i + 2], 16) for i in (0, 2, 4))  # type: ignore[return-value]


FONT_CANDIDATES = {
    "bold": ["Inter-Bold.ttf", "DejaVuSans-Bold.ttf", "segoeuib.ttf", "Arial Bold.ttf", "arialbd.ttf"],
    "semibold": ["Inter-SemiBold.ttf", "DejaVuSans-Bold.ttf", "seguisb.ttf", "Arial Bold.ttf", "arialbd.ttf"],
}
FONT_DIRS = [Path("/usr/share/fonts"), Path("/Library/Fonts"), Path("/System/Library/Fonts"),
             Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts"]
_font_cache: dict[tuple[str, int], ImageFont.FreeTypeFont] = {}
_font_dirs: list[Path] = []


def font(weight: str, size: int) -> ImageFont.FreeTypeFont:
    key = (weight, size)
    if key in _font_cache:
        return _font_cache[key]
    for name in FONT_CANDIDATES[weight]:
        for base in _font_dirs + FONT_DIRS:
            if not base.exists():
                continue
            hit = next(iter(base.rglob(name)), None)
            if hit:
                _font_cache[key] = ImageFont.truetype(str(hit), size)
                return _font_cache[key]
    _font_cache[key] = ImageFont.load_default(size)
    return _font_cache[key]


def gradient(size: tuple[int, int], top: tuple, bottom: tuple) -> Image.Image:
    w, h = size
    column = Image.new("RGB", (1, h))
    for y in range(h):
        a = y / max(1, h - 1)
        column.putpixel((0, y), tuple(round(top[i] + (bottom[i] - top[i]) * a) for i in range(3)))
    return column.resize((w, h))


def render_logo(size: int, color: tuple[int, int, int] | None = None) -> Image.Image:
    """Render the shared rectangle-based SVG, optionally as a monochrome mark without its tile."""
    vector = ET.parse(MARKETING / "brand" / "plural.svg").getroot()
    scale = size * 4 / float(vector.attrib["viewBox"].split()[2])
    image = Image.new("RGBA", (size * 4, size * 4))
    draw = ImageDraw.Draw(image)
    for rect in vector.findall("{http://www.w3.org/2000/svg}rect"):
        if color is not None and "x" not in rect.attrib:
            continue
        left = float(rect.get("x", 0)) * scale
        top = float(rect.get("y", 0)) * scale
        right = left + float(rect.attrib["width"]) * scale
        bottom = top + float(rect.attrib["height"]) * scale
        fill = color if color is not None else rect.attrib["fill"]
        draw.rounded_rectangle((left, top, right, bottom), radius=float(rect.get("rx", 0)) * scale, fill=fill)
    return image.resize((size, size), Image.LANCZOS)


def load_logo(cfg: dict) -> Image.Image:
    path = MARKETING / cfg["brand"]["logo"]
    logo = Image.open(path).convert("RGBA")
    bbox = logo.getchannel("A").getbbox()
    return logo.crop(bbox) if bbox else logo


def paste_logo(canvas: Image.Image, logo: Image.Image, box: tuple[int, int, int, int]) -> None:
    """Fit the cropped logo inside box (l, t, r, b), centred."""
    l, t, r, b = box
    scale = min((r - l) / logo.width, (b - t) / logo.height)
    w, h = max(1, round(logo.width * scale)), max(1, round(logo.height * scale))
    art = logo.resize((w, h), Image.LANCZOS)
    canvas.alpha_composite(art, (l + (r - l - w) // 2, t + (b - t - h) // 2)) if canvas.mode == "RGBA" \
        else canvas.paste(art, (l + (r - l - w) // 2, t + (b - t - h) // 2), art)


def square_icon(logo: Image.Image, size: int, bg: tuple, art: float) -> Image.Image:
    """Full-bleed square (iOS and Play apply their own corner mask): background colour + logo."""
    img = Image.new("RGBA", (size, size), bg + (255,))
    pad = round(size * (1 - art) / 2)
    paste_logo(img, logo, (pad, pad, size - pad, size - pad))
    return img


def shadowed(canvas: Image.Image, shape_box: tuple, radius: int, blur: int, offset: int, alpha: int) -> None:
    shadow = Image.new("L", canvas.size, 0)
    l, t, r, b = shape_box
    ImageDraw.Draw(shadow).rounded_rectangle([l, t + offset, r, b + offset], radius=radius, fill=alpha)
    shadow = shadow.filter(ImageFilter.GaussianBlur(blur))
    black = Image.new("RGBA", canvas.size, (0, 0, 0, 255))
    black.putalpha(shadow)
    canvas.alpha_composite(black)


def mac_icon(logo: Image.Image, bg: tuple) -> Image.Image:
    """macOS icon grid: 824 px rounded body on a 1024 canvas, soft shadow, logo inside."""
    img = Image.new("RGBA", (1024, 1024), (0, 0, 0, 0))
    body = (100, 100, 924, 924)
    shadowed(img, body, 185, 14, 10, 90)
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(layer).rounded_rectangle(body, radius=185, fill=bg + (255,))
    img.alpha_composite(layer)
    paste_logo(img, logo, (250, 250, 774, 774))
    return img


def wrap(draw: ImageDraw.ImageDraw, text: str, fnt, width: int) -> list[str]:
    lines, line = [], ""
    for word in text.split():
        trial = f"{line} {word}".strip()
        if draw.textlength(trial, font=fnt) <= width or not line:
            line = trial
        else:
            lines.append(line)
            line = word
    if line:
        lines.append(line)
    return lines


def draw_caption(img: Image.Image, text: str, box: tuple, color: tuple, max_lines: int = 2) -> None:
    """Largest bold caption (up to 2 lines) that fits box, centred."""
    l, t, r, b = box
    d = ImageDraw.Draw(img)
    size = round((b - t) / (max_lines * 1.15))
    while size > 12:
        fnt = font("bold", size)
        lines = wrap(d, text, fnt, r - l)
        line_h = round(size * 1.15)
        if len(lines) <= max_lines and all(d.textlength(x, font=fnt) <= r - l for x in lines):
            y = t + ((b - t) - line_h * len(lines)) // 2
            for x in lines:
                d.text(((l + r) // 2, y + line_h // 2), x, font=fnt, fill=color, anchor="mm")
                y += line_h
            return
        size -= 2


# ---------------------------------------------------------------- screenshots

def raw_screenshots(cfg: dict, source: str) -> list[Path]:
    folder = MARKETING / cfg["screenshots"]["sources"][source]
    files = sorted(p for p in folder.glob("*") if p.suffix.lower() in IMAGE_EXT) if folder.exists() else []
    if not files and source == "tablet":
        return raw_screenshots(cfg, "phone")
    return files


def placeholder_screen(cfg: dict, logo: Image.Image, portrait: bool, source: str) -> Image.Image:
    size = (1080, 2340) if portrait else (1920, 1200)
    img = Image.new("RGBA", size, (13, 15, 20, 255))
    w, h = size
    paste_logo(img, logo, (w // 2 - w // 8, h // 2 - w // 8, w // 2 + w // 8, h // 2 + w // 8))
    d = ImageDraw.Draw(img)
    hint = f"Add a capture to Marketing/{cfg['screenshots']['sources'][source]}/"
    d.text((w // 2, h // 2 + w // 5), hint, font=font("semibold", w // 40), fill=(142, 151, 171), anchor="mm")
    return img


def rounded(img: Image.Image, radius: int) -> Image.Image:
    mask = Image.new("L", img.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, img.width - 1, img.height - 1], radius=radius, fill=255)
    out = img.convert("RGBA")
    out.putalpha(mask)
    return out


def compose_screenshot(raw: Image.Image, size: tuple[int, int], caption: str, cfg: dict, device: str) -> Image.Image:
    """Brand gradient, caption on top, the capture below in a simple device frame. RGB, no alpha."""
    brand = cfg["brand"]
    w, h = size
    canvas = gradient(size, hex_rgb(brand["background_top"]), hex_rgb(brand["background_bottom"])).convert("RGBA")
    landscape = w > h
    cap_h = round(h * (0.20 if landscape else 0.16))
    margin = round(min(w, h) * 0.06)
    draw_caption(canvas, caption, (margin, round(h * 0.03), w - margin, round(h * 0.03) + cap_h),
                 hex_rgb(brand["caption_color"]))

    area = (margin, round(h * 0.03) + cap_h + margin // 2, w - margin, h - margin)
    bezel = 0 if device == "desktop" else round(min(w, h) * 0.022)
    aw, ah = area[2] - area[0] - 2 * bezel, area[3] - area[1] - 2 * bezel
    shot = raw.convert("RGB")
    scale = min(aw / shot.width, ah / shot.height)
    sw, sh = round(shot.width * scale), round(shot.height * scale)
    shot = shot.resize((sw, sh), Image.LANCZOS)
    x = (w - sw) // 2
    y = area[1] + bezel + (ah - sh) // 2
    radius = round(sw * (0.012 if device == "desktop" else 0.06))
    frame = (x - bezel, y - bezel, x + sw + bezel, y + sh + bezel)
    shadowed(canvas, frame, radius + bezel, round(min(w, h) * 0.02), round(min(w, h) * 0.01), 110)
    if bezel:
        layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
        ImageDraw.Draw(layer).rounded_rectangle(frame, radius=radius + bezel, fill=(16, 18, 24, 255))
        canvas.alpha_composite(layer)
    canvas.alpha_composite(rounded(shot, radius), (x, y))
    return canvas.convert("RGB")


def build_screenshots(cfg: dict, logo: Image.Image, locale: str, captions: list[str], out: Path,
                      rep: Report) -> dict[str, list[Path]]:
    """Returns target name -> written files (real captures only)."""
    result: dict[str, list[Path]] = {}
    reported_missing: set[str] = set()
    for target, (source, w, h) in cfg["screenshots"]["targets"].items():
        files = raw_screenshots(cfg, source)
        store = target.split("_")[0]
        limit = SCREENSHOT_MAX.get(store, 10)
        written: list[Path] = []
        if not files:
            if source not in reported_missing:
                rep.missing.append(f"No {source} screenshots in Marketing/{cfg['screenshots']['sources'][source]}/ "
                                   f"- placeholders written to preview/ show the layout")
                reported_missing.add(source)
            for i, cap in enumerate(captions[:2] or ["Your caption here"], 1):
                img = compose_screenshot(placeholder_screen(cfg, logo, h > w, source), (w, h), cap, cfg, source)
                dest = out / "preview" / locale / f"{target}_{i}.png"
                dest.parent.mkdir(parents=True, exist_ok=True)
                img.save(dest, optimize=True)
            result[target] = []
            continue
        if len(files) > limit:
            rep.warnings.append(f"{target}: {len(files)} captures, the store takes {limit}; extra ones skipped")
        if len(captions) < min(len(files), limit):
            rep.warnings.append(f"{locale}: {len(files)} {source} captures but {len(captions)} captions; "
                                "captures without a caption get none")
        for i, src in enumerate(files[:limit], 1):
            cap = captions[i - 1] if i <= len(captions) else ""
            img = compose_screenshot(Image.open(src), (w, h), cap, cfg, source)
            dest = out / "_shots" / locale / target / f"{i:02d}.png"
            dest.parent.mkdir(parents=True, exist_ok=True)
            img.save(dest, optimize=True)
            written.append(dest)
        result[target] = written
    return result


# ---------------------------------------------------------------- per-store output

def write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text.rstrip("\n") + "\n", encoding="utf-8")


def copy_shots(files: list[Path], folder: Path, prefix: str = "") -> None:
    folder.mkdir(parents=True, exist_ok=True)
    for i, f in enumerate(files, 1):
        shutil.copy2(f, folder / f"{prefix}{i:02d}.png")


def android_listing(cfg, locale, t, shots, logo, out: Path, build_number: int) -> None:
    base = out / "android" / "metadata" / "android" / locale
    write(base / "title.txt", t["name"])
    write(base / "short_description.txt", t["short_description"])
    write(base / "full_description.txt", t["description"])
    write(base / "changelogs" / f"{build_number}.txt", t["release_notes"])
    write(base / "changelogs" / "default.txt", t["release_notes"])
    images = base / "images"
    images.mkdir(parents=True, exist_ok=True)
    bg = hex_rgb(cfg["brand"]["icon_background"])
    square_icon(logo, 512, bg, 0.70).save(images / "icon.png", optimize=True)
    feature_graphic(cfg, logo, t).save(images / "featureGraphic.png", optimize=True)
    copy_shots(shots.get("play_phone", []), images / "phoneScreenshots")
    copy_shots(shots.get("play_tablet_7", []), images / "sevenInchScreenshots")
    copy_shots(shots.get("play_tablet_10", []), images / "tenInchScreenshots")


def feature_graphic(cfg: dict, logo: Image.Image, t: dict) -> Image.Image:
    """Google Play feature graphic: 1024x500, 24-bit (no alpha), key content away from the edges."""
    brand = cfg["brand"]
    img = gradient((1024, 500), hex_rgb(brand["background_top"]), hex_rgb(brand["background_bottom"])).convert("RGBA")
    tile = square_icon(logo, 220, hex_rgb(brand["icon_background"]), 0.70)
    tile = rounded(tile, 50)
    shadowed(img, (110, 140, 330, 360), 50, 12, 8, 100)
    img.alpha_composite(tile, (110, 140))
    d = ImageDraw.Draw(img)
    color = hex_rgb(brand["caption_color"])
    d.text((380, 240), t["name"], font=font("bold", 104), fill=color, anchor="ls")
    tagline = t["subtitle"] or t["short_description"]
    size = 52
    while size > 20 and d.textlength(tagline, font=font("semibold", size)) > 580:
        size -= 2
    d.text((382, 300), tagline, font=font("semibold", size), fill=color, anchor="ls")
    return img.convert("RGB")


def apple_listing(cfg, locale, t, shots, logo, out: Path, platform: str) -> None:
    app = cfg["app"]
    meta = out / platform / "metadata"
    base = meta / locale
    write(base / "name.txt", t["app_store_name"] or t["name"])
    write(base / "subtitle.txt", t["subtitle"])
    write(base / "description.txt", t["description"])
    write(base / "keywords.txt", t["keywords"])
    write(base / "promotional_text.txt", t["promotional_text"])
    write(base / "release_notes.txt", t["release_notes"])
    write(base / "support_url.txt", app["support_url"])
    write(base / "marketing_url.txt", app["website"])
    write(base / "privacy_url.txt", app["privacy_url"])
    write(meta / "copyright.txt", app["copyright"])
    write(meta / "primary_category.txt", cfg["categories"]["app_store"])
    write(meta / "secondary_category.txt", cfg["categories"].get("app_store_secondary", ""))
    shots_dir = out / platform / "screenshots" / locale
    if platform == "ios":
        copy_shots(shots.get("ios_iphone_69", []), shots_dir, "iphone69_")
        copy_shots(shots.get("ios_iphone_65", []), shots_dir, "iphone65_")
        copy_shots(shots.get("ios_ipad_13", []), shots_dir, "ipad13_")
        bg = hex_rgb(cfg["brand"]["icon_background"])
        square_icon(logo, 1024, bg, 0.70).convert("RGB").save(out / "ios" / "AppIcon-1024.png", optimize=True)
    else:
        copy_shots(shots.get("mac", []), shots_dir, "mac_")


def windows_listing(cfg, locale, t, shots, logo, out: Path) -> None:
    brand = cfg["brand"]
    base = out / "windows" / "listing" / locale.lower()
    write(base / "product_name.txt", t["name"])
    write(base / "description.txt", t["description"])
    write(base / "short_description.txt", t["short_description"])
    write(base / "whats_new.txt", t["release_notes"])
    write(base / "product_features.txt", "\n".join(t["features"][:MS_MAX_FEATURES]))
    terms = [k.strip() for k in t["keywords"].split(",") if k.strip()][:MS_MAX_TERMS]
    write(base / "search_terms.txt", "\n".join(terms))
    write(base / "copyright.txt", f"© {cfg['app']['copyright']}")
    write(base / "website.txt", cfg["app"]["website"])
    write(base / "support_contact.txt", cfg["app"]["support_url"])
    write(base / "privacy_policy.txt", cfg["app"]["privacy_url"])
    copy_shots(shots.get("windows", []), base / "screenshots", "desktop_")
    images = out / "windows" / "images"
    if images.exists():
        return
    images.mkdir(parents=True)
    top, bottom = hex_rgb(brand["background_top"]), hex_rgb(brand["background_bottom"])
    store_logo = Image.new("RGBA", (300, 300), (0, 0, 0, 0))                      # 1:1 app tile icon
    paste_logo(store_logo, logo, (30, 30, 270, 270))
    store_logo.save(images / "StoreLogo-300x300.png", optimize=True)
    tile = rounded(square_icon(logo, 480, hex_rgb(brand["icon_background"]), 0.70), 96)
    box = gradient((1080, 1080), top, bottom).convert("RGBA")                     # 1:1 box art
    shadowed(box, (300, 300, 780, 780), 96, 20, 12, 110)
    box.alpha_composite(tile, (300, 300))
    box.convert("RGB").save(images / "BoxArt-1080x1080.png", optimize=True)
    poster = gradient((720, 1080), top, bottom).convert("RGBA")                    # 2:3 poster art
    small = rounded(square_icon(logo, 300, hex_rgb(brand["icon_background"]), 0.70), 60)
    shadowed(poster, (210, 330, 510, 630), 60, 16, 10, 110)
    poster.alpha_composite(small, (210, 330))
    ImageDraw.Draw(poster).text((360, 760), t["name"], font=font("bold", 96),
                                fill=hex_rgb(brand["caption_color"]), anchor="mm")
    poster.convert("RGB").save(images / "Poster-720x1080.png", optimize=True)
    hero = gradient((1920, 1080), top, bottom).convert("RGBA")                     # 16:9 hero, no text
    white = render_logo(1024, (255, 255, 255))
    paste_logo(hero, white.crop(white.getchannel("A").getbbox()), (760, 340, 1160, 740))
    hero.convert("RGB").save(images / "Hero-1920x1080.png", optimize=True)


def msix_assets(cfg: dict, logo: Image.Image, out: Path) -> None:
    """Scale-qualified MSIX logos; makepri bundles them into resources.pri."""
    folder = out / "msix-assets"
    folder.mkdir(parents=True, exist_ok=True)
    specs = {  # name: (base width, base height, logo fraction of the shorter side)
        "Square44x44Logo": (44, 44, 0.84),
        "Square150x150Logo": (150, 150, 0.56),
        "Wide310x150Logo": (310, 150, 0.56),
        "Square310x310Logo": (310, 310, 0.50),
        "StoreLogo": (50, 50, 0.90),
        "SplashScreen": (620, 300, 0.50),
    }
    for name, (bw, bh, frac) in specs.items():
        for scale in (100, 125, 150, 200, 400):
            w, h = round(bw * scale / 100), round(bh * scale / 100)
            img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
            side = round(min(w, h) * frac)
            paste_logo(img, logo, ((w - side) // 2, (h - side) // 2, (w + side) // 2, (h + side) // 2))
            img.save(folder / f"{name}.scale-{scale}.png", optimize=True)
    for size in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):           # taskbar / Start icons
        img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        pad = max(1, round(size * 0.04))
        paste_logo(img, logo, (pad, pad, size - pad, size - pad))
        img.save(folder / f"Square44x44Logo.targetsize-{size}.png", optimize=True)
        img.save(folder / f"Square44x44Logo.targetsize-{size}_altform-unplated.png", optimize=True)
        img.save(folder / f"Square44x44Logo.altform-lightunplated_targetsize-{size}.png", optimize=True)


def mac_iconset(cfg: dict, logo: Image.Image, out: Path) -> None:
    folder = out / "macos.iconset"
    folder.mkdir(parents=True, exist_ok=True)
    master = mac_icon(logo, hex_rgb(cfg["brand"]["icon_background"]))
    master.save(out / "macos" / "AppIcon-1024.png", optimize=True)
    for base in (16, 32, 128, 256, 512):
        master.resize((base, base), Image.LANCZOS).save(folder / f"icon_{base}x{base}.png")
        master.resize((base * 2, base * 2), Image.LANCZOS).save(folder / f"icon_{base}x{base}@2x.png")


# ---------------------------------------------------------------- commands

def cmd_check(args) -> int:
    cfg = load_config()
    rep = Report()
    if not locales():
        rep.errors.append("No locale folders with name.txt under Marketing/")
    for loc in locales():
        validate(loc, load_texts(loc, args.release_notes_text, rep), rep)
    if cfg["app"]["default_locale"] not in locales():
        rep.errors.append(f"default_locale {cfg['app']['default_locale']} has no folder under Marketing/")
    print(rep.markdown(None))
    return 1 if rep.errors else 0


def cmd_build(args) -> int:
    cfg = load_config()
    out = Path(args.out).resolve()
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    if args.font_dir:
        _font_dirs.append(Path(args.font_dir))
    rep = Report()
    logo = load_logo(cfg)
    if max(Image.open(MARKETING / cfg["brand"]["logo"]).size) < 1024:
        rep.warnings.append("brand logo is smaller than 1024 px; large store icons will be soft")

    texts = {}
    for loc in locales():
        texts[loc] = load_texts(loc, args.release_notes_text, rep)
        validate(loc, texts[loc], rep)
    if rep.errors:
        report = rep.markdown(args.version)
        write(out / "REPORT.md", report)
        print(report)
        return 1

    (out / "macos").mkdir(parents=True, exist_ok=True)
    msix_assets(cfg, logo, out)
    mac_iconset(cfg, logo, out)
    for loc, t in texts.items():
        shots = build_screenshots(cfg, logo, loc, t["captions"], out, rep)
        android_listing(cfg, loc, t, shots, logo, out, args.build_number)
        apple_listing(cfg, loc, t, shots, logo, out, "ios")
        apple_listing(cfg, loc, t, shots, logo, out, "macos")
        windows_listing(cfg, loc, t, shots, logo, out)
    shutil.rmtree(out / "_shots", ignore_errors=True)
    write(out / "android" / "version.txt", f"{args.version} ({args.build_number})")

    rep.built += [f"Locales: {', '.join(texts)}",
                  "Google Play: listing text, 512 px icon, 1024x500 feature graphic, phone/7\"/10\" screenshots",
                  "App Store: listing text, 1024 px icon (no alpha), 6.9\"/6.5\" iPhone and 13\" iPad screenshots",
                  "Mac App Store: listing text, macOS icon set, 2880x1800 screenshots",
                  "Microsoft Store: listing text, 300 px logo, box art, poster, hero, 4K screenshots, MSIX logos"]
    report = rep.markdown(args.version)
    write(out / "REPORT.md", report)
    print(report)
    return 0


def cmd_logo(args) -> int:
    cfg = load_config()
    path = MARKETING / cfg["brand"]["logo"]
    path.parent.mkdir(parents=True, exist_ok=True)
    render_logo(args.size).save(path, optimize=True)
    print(f"wrote {path.relative_to(ROOT)} ({args.size} px)")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="command", required=True)
    for name in ("check", "build"):
        s = sub.add_parser(name)
        s.add_argument("--release-notes-file", help="used when <locale>/release_notes.txt is empty "
                                                    "(the workflow passes the GitHub release body)")
        if name == "build":
            s.add_argument("--version", required=True)
            s.add_argument("--build-number", type=int, required=True, help="Android versionCode / iOS build")
            s.add_argument("--out", default="out/store")
            s.add_argument("--font-dir", help="folder searched for Inter-Bold.ttf / Inter-SemiBold.ttf")
    s = sub.add_parser("logo")
    s.add_argument("--size", type=int, default=2048)
    args = p.parse_args()
    if getattr(args, "release_notes_file", None):
        args.release_notes_text = Path(args.release_notes_file).read_text(encoding="utf-8")
    else:
        args.release_notes_text = ""
    return {"check": cmd_check, "build": cmd_build, "logo": cmd_logo}[args.command](args)


if __name__ == "__main__":
    sys.exit(main())
