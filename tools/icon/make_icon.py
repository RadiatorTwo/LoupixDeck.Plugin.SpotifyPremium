#!/usr/bin/env python3
"""Erzeugt Icon 3a (Spotify Premium Plugin, LoupixDeck) als SVG und optional PNG.

Nutzung:
    python make_icon_3a.py                 # icon_3a.svg
    python make_icon_3a.py --png 256 512   # zusätzlich icon_3a_256.png, icon_3a_512.png
PNG-Export benötigt: pip install cairosvg
"""
import argparse

GREEN = "#2fd47a"
DARK = "#121512"

SVG = f"""<svg xmlns="http://www.w3.org/2000/svg" width="{{size}}" height="{{size}}" viewBox="0 0 100 100">
  <rect width="100" height="100" rx="22" fill="{GREEN}"/>
  <circle cx="38" cy="50" r="19" fill="{DARK}"/>
  <path d="M33 41 L33 59 L47 50 Z" fill="{GREEN}" stroke="{GREEN}" stroke-width="3" stroke-linejoin="round"/>
  <g fill="none" stroke="{DARK}" stroke-width="6" stroke-linecap="round">
    <path d="M62.4 42.3 A10 10 0 0 1 62.4 57.7"/>
    <path d="M67.6 36.2 A18 18 0 0 1 67.6 63.8" opacity=".6"/>
    <path d="M72.7 30.1 A26 26 0 0 1 72.7 69.9" opacity=".3"/>
  </g>
</svg>
"""


def svg(size: int = 256) -> str:
    return SVG.replace("{size}", str(size))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="icon_3a")
    ap.add_argument("--png", type=int, nargs="*", default=[], help="PNG-Größen in px")
    a = ap.parse_args()

    with open(f"{a.out}.svg", "w", encoding="utf-8") as f:
        f.write(svg())
    print(f"{a.out}.svg")

    if a.png:
        import cairosvg
        for s in a.png:
            cairosvg.svg2png(bytestring=svg(s).encode(), write_to=f"{a.out}_{s}.png",
                             output_width=s, output_height=s)
            print(f"{a.out}_{s}.png")


if __name__ == "__main__":
    main()
