from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "picture"
OUT.mkdir(parents=True, exist_ok=True)

size = 1024
image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)

# Deep navy tile with an aqua accent, designed to remain legible at 16 px.
draw.rounded_rectangle((48, 48, 976, 976), radius=230, fill=(10, 37, 64, 255))
draw.rounded_rectangle((112, 112, 912, 912), radius=180, fill=(16, 59, 91, 255))
draw.rounded_rectangle((210, 168, 790, 856), radius=92, fill=(242, 249, 252, 255))
draw.rounded_rectangle((284, 96, 716, 250), radius=72, fill=(46, 197, 190, 255))

for y in (350, 472, 594):
    draw.rounded_rectangle((318, y, 690, y + 48), radius=24, fill=(104, 132, 151, 255))

# Confirmation mark represents a ticket detected and handled successfully.
draw.line((330, 700, 438, 796), fill=(46, 197, 190, 255), width=64)
draw.line((438, 796, 686, 548), fill=(46, 197, 190, 255), width=64)

png_path = OUT / "ftms-app.png"
ico_path = OUT / "ftms-app.ico"
image.save(png_path)
image.save(ico_path, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

print(png_path)
print(ico_path)
