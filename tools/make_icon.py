# Draws the WinTapeRecorder icon: a cassette with a red record dot on a dark glass tile. Usage: python make_icon.py <output path without extension>
import sys
from PIL import Image, ImageDraw, ImageFilter
size = 1024
transparent = lambda: Image.new("RGBA", (size, size), (0, 0, 0, 0))
mask = Image.new("L", (size, size), 0); ImageDraw.Draw(mask).rounded_rectangle([40, 40, size - 40, size - 40], radius=220, fill=255)
gradient = Image.new("RGBA", (size, size)); gradient_draw = ImageDraw.Draw(gradient)
for row in range(size): blend = row / size; gradient_draw.line([(0, row), (size, row)], fill=(int(26 - 18 * blend), int(30 - 20 * blend), int(40 - 26 * blend), 255))
tile = transparent(); tile.paste(gradient, (0, 0), mask)
highlight = transparent(); ImageDraw.Draw(highlight).ellipse([-200, -560, size + 200, 380], fill=(255, 255, 255, 26))
tile = Image.alpha_composite(tile, Image.composite(highlight, transparent(), mask))
cyan, cyan_dim, red = (94, 231, 255, 255), (94, 231, 255, 70), (255, 80, 64, 255)
art, glow = transparent(), transparent()
for layer in (art, glow):
    draw = ImageDraw.Draw(layer)
    draw.rounded_rectangle([150, 250, 874, 730], radius=60, outline=cyan, width=34)  # cassette body
    draw.rounded_rectangle([280, 360, 744, 520], radius=70, outline=cyan, width=26)  # window
    for centre_x in (380, 644):
        draw.ellipse([centre_x - 58, 382, centre_x + 58, 498], outline=cyan, width=26)  # reels
    draw.polygon([(300, 730), (350, 630), (674, 630), (724, 730)], outline=cyan, width=26)  # head opening
ImageDraw.Draw(art).line([(438, 440), (586, 440)], fill=cyan_dim, width=20)  # tape between the reels
dot = transparent(); ImageDraw.Draw(dot).ellipse([700, 120, 900, 320], fill=red)
tile = Image.alpha_composite(tile, Image.composite(glow.filter(ImageFilter.GaussianBlur(26)), transparent(), mask))
tile = Image.alpha_composite(tile, art)
tile = Image.alpha_composite(tile, Image.composite(dot.filter(ImageFilter.GaussianBlur(30)), transparent(), mask))
tile = Image.alpha_composite(tile, dot)
tile.save(sys.argv[1] + ".png")
tile.resize((256, 256), Image.LANCZOS).save(sys.argv[1] + "-256.png")
tile.save(sys.argv[1] + ".ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
