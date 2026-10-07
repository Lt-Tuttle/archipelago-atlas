"""Makes the program's Windows icon and its start-up splash from Assets/Graphics/icon.png.

- Assets/Graphics/icon.ico: Windows shows a program's icon at 16, 32, 48, 64, 128 and 256 pixels, and Godot's export
  writes each size the icon file has into the exe; a size the file lacks keeps Godot's own icon there (and the export
  warns, which fails the release build).
- Assets/Graphics/boot_splash.png: what Atlas shows while it starts (project.godot, application/boot_splash): the icon
  on the dark theme's surface colour, drawn at its own size in the middle of the window, in place of Godot's logo.

Needs Pillow (pip install pillow). Both files are committed, so this runs only when icon.png changes.
"""
from pathlib import Path

from PIL import Image

GRAPHICS = Path(__file__).resolve().parent.parent / 'Assets' / 'Graphics'
# Largest first: Pillow keeps only the sizes up to the first image's.
ICON_SIZES = [256, 128, 64, 48, 32, 16]
SPLASH_SIZE = 192
SURFACE = (0x1E, 0x1E, 0x1E, 255)  # ThemeColors' Dark palette, Surface; the splash's background colour in project.godot


def main():
    image = Image.open(GRAPHICS / 'icon.png').convert('RGBA')
    frames = [image.resize((size, size), Image.Resampling.LANCZOS) for size in ICON_SIZES]
    frames[0].save(GRAPHICS / 'icon.ico', format='ICO', sizes=[(size, size) for size in ICON_SIZES], append_images=frames[1:], bitmap_format='png')
    print(f'{GRAPHICS / "icon.ico"} written with {len(ICON_SIZES)} sizes')

    splash = Image.new('RGBA', (SPLASH_SIZE, SPLASH_SIZE), SURFACE)
    splash.alpha_composite(image.resize((SPLASH_SIZE, SPLASH_SIZE), Image.Resampling.LANCZOS))
    splash.convert('RGB').save(GRAPHICS / 'boot_splash.png', format='PNG', optimize=True)
    print(f'{GRAPHICS / "boot_splash.png"} written ({SPLASH_SIZE} px on the dark surface)')


if __name__ == '__main__':
    main()
