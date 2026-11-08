"""Generate small pixel-art assets used by the ILauncher UI (no external assets)."""
from PIL import Image
import random
import os

OUT = os.path.join("src", "ILauncher", "Assets")
BLOCKS = os.path.join(OUT, "Blocks")
os.makedirs(BLOCKS, exist_ok=True)
random.seed(20261108)


def upscale(img):
    return img.resize((64, 64), Image.Resampling.NEAREST)


def noise_fill(img, base, variants, rows=range(16), cols=range(16)):
    px = img.load()
    for y in rows:
        for x in cols:
            px[x, y] = random.choice([base] + variants)


def grass():
    img = Image.new("RGBA", (16, 16))
    px = img.load()
    for y in range(3):
        for x in range(16):
            px[x, y] = random.choice([(106, 170, 64), (95, 159, 53), (124, 189, 78)])
    for y in range(3, 16):
        for x in range(16):
            px[x, y] = random.choice([(134, 96, 67), (121, 85, 58), (150, 108, 76), (110, 78, 52)])
    for x in range(0, 16, 3):
        y = random.randint(3, 6)
        px[x, y] = (106, 170, 64)
    return img


def stone():
    img = Image.new("RGBA", (16, 16))
    noise_fill(img, (125, 125, 125), [(112, 112, 112), (140, 140, 140), (103, 103, 103)])
    return img


def bevel(base, light, dark):
    img = Image.new("RGBA", (16, 16), base)
    px = img.load()
    for i in range(16):
        px[i, 0] = light
        px[0, i] = light
        px[i, 15] = dark
        px[15, i] = dark
    return img


def diamond():
    img = bevel((92, 219, 213), (150, 240, 235), (44, 160, 158))
    px = img.load()
    for x, y in [(3, 3), (4, 3), (3, 4), (11, 5), (12, 5), (11, 6), (6, 11), (7, 11), (6, 12)]:
        px[x, y] = (200, 255, 250)
    return img


def redstone():
    img = Image.new("RGBA", (16, 16))
    noise_fill(img, (160, 32, 32), [(120, 20, 20), (200, 45, 45), (90, 15, 15)])
    px = img.load()
    for _ in range(18):
        x, y = random.randint(1, 14), random.randint(1, 14)
        px[x, y] = (245, 90, 70)
    return img


def command_block():
    img = Image.new("RGBA", (16, 16), (198, 146, 90))
    px = img.load()
    for i in range(16):
        px[i, 0] = (230, 186, 130)
        px[0, i] = (230, 186, 130)
        px[i, 15] = (150, 104, 58)
        px[15, i] = (150, 104, 58)
    for x, y in [(5, 5), (6, 5), (9, 5), (10, 5), (7, 6), (8, 6), (6, 8), (7, 8), (8, 8), (9, 8),
                 (5, 10), (6, 10), (9, 10), (10, 10), (7, 11), (8, 11)]:
        px[x, y] = (93, 63, 36)
    return img


def gold():
    img = bevel((247, 210, 74), (255, 240, 150), (198, 154, 32))
    px = img.load()
    for x, y in [(4, 4), (11, 4), (4, 11), (11, 11)]:
        px[x, y] = (255, 249, 190)
    return img


def planks():
    img = Image.new("RGBA", (16, 16), (162, 130, 78))
    px = img.load()
    for y in range(16):
        for x in range(16):
            if y % 5 == 0:
                px[x, y] = (120, 92, 54)
            elif x % 7 == 0 and y % 5 != 0:
                px[x, y] = (140, 108, 64)
    return img


def obsidian():
    img = Image.new("RGBA", (16, 16))
    noise_fill(img, (30, 24, 45), [(45, 35, 66), (20, 16, 32), (58, 44, 80)])
    px = img.load()
    for x, y in [(3, 4), (4, 3), (11, 10), (10, 11), (7, 13), (12, 4)]:
        px[x, y] = (110, 85, 150)
    return img


blocks = {
    "grass": grass(),
    "stone": stone(),
    "diamond": diamond(),
    "redstone": redstone(),
    "command": command_block(),
    "gold": gold(),
    "planks": planks(),
    "obsidian": obsidian(),
}

for name, img in blocks.items():
    upscale(img.convert("RGBA")).save(os.path.join(BLOCKS, f"{name}.png"))

# A friendly Steve-style head/avatar, 8x8 pixels upscaled.
face = Image.new("RGBA", (8, 8))
px = face.load()
skin = (200, 152, 118)
skin_dark = (168, 122, 92)
hair = (58, 40, 28)
white = (245, 245, 245)
iris = (74, 96, 150)
mouth = (110, 70, 55)

for y in range(8):
    for x in range(8):
        px[x, y] = skin
for x in range(8):
    px[x, 0] = hair
    px[x, 1] = hair
px[0, 2] = hair
px[7, 2] = hair
px[1, 2] = hair
px[6, 2] = hair
px[2, 3] = white
px[3, 3] = iris
px[4, 3] = white
px[5, 3] = iris
px[3, 4] = skin_dark
px[4, 4] = skin_dark
px[2, 6] = mouth
px[3, 6] = mouth
px[4, 6] = mouth
px[5, 6] = mouth
for x in range(2, 6):
    px[x, 7] = skin_dark

upscale(face).save(os.path.join(OUT, "avatar.png"))

# App icon: grass block with a subtle rounded white frame.
icon = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
block = upscale(grass()).resize((192, 192), Image.Resampling.NEAREST)
icon.alpha_composite(block, (32, 32))
icon.save(os.path.join(OUT, "app.ico"), sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
icon.save(os.path.join(OUT, "app.png"))

print("assets written to", OUT)
