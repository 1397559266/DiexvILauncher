"""Generate original 64x64 Minecraft-style skins for ILauncher's local skin library."""
from PIL import Image
import os

OUT = os.path.join("src", "ILauncher", "Assets", "Skins")
os.makedirs(OUT, exist_ok=True)


def px(img, x, y, color):
    if 0 <= x < 64 and 0 <= y < 64:
        img.putpixel((x, y), color)


def rect(img, x, y, w, h, color):
    for yy in range(y, y + h):
        for xx in range(x, x + w):
            px(img, xx, yy, color)


def shade(color, factor):
    return tuple(max(0, min(255, int(c * factor))) for c in color)


PALETTES = {
    "Steve": dict(skin=(198, 149, 112), hair=(56, 40, 28), shirt=(43, 166, 165),
                  shirt2=(35, 135, 135), pants=(58, 66, 148), pants2=(39, 45, 110),
                  shoe=(70, 70, 70), eye=(74, 96, 150), mouth=(112, 70, 55)),
    "Alex": dict(skin=(238, 190, 150), hair=(207, 132, 58), shirt=(104, 180, 84),
                 shirt2=(77, 142, 62), pants=(138, 102, 62), pants2=(105, 76, 45),
                 shoe=(70, 60, 52), eye=(67, 110, 160), mouth=(150, 90, 75)),
    "Ari": dict(skin=(235, 190, 150), hair=(60, 43, 35), shirt=(228, 112, 88),
                shirt2=(190, 89, 69), pants=(61, 76, 118), pants2=(45, 57, 92),
                shoe=(55, 55, 60), eye=(70, 100, 150), mouth=(130, 75, 60)),
    "Efe": dict(skin=(225, 178, 140), hair=(30, 28, 26), shirt=(242, 201, 76),
                shirt2=(204, 165, 55), pants=(70, 70, 74), pants2=(50, 50, 54),
                shoe=(40, 40, 42), eye=(60, 95, 145), mouth=(120, 70, 55)),
    "Kai": dict(skin=(205, 155, 115), hair=(210, 182, 100), shirt=(70, 130, 200),
                shirt2=(54, 106, 168), pants=(43, 48, 82), pants2=(32, 36, 65),
                shoe=(32, 32, 36), eye=(68, 120, 170), mouth=(115, 70, 55)),
    "Makena": dict(skin=(150, 105, 75), hair=(28, 22, 18), shirt=(220, 80, 110),
                   shirt2=(180, 62, 89), pants=(70, 85, 120), pants2=(50, 63, 92),
                   shoe=(35, 35, 40), eye=(70, 100, 150), mouth=(90, 50, 40)),
    "Noor": dict(skin=(230, 185, 148), hair=(24, 22, 30), shirt=(120, 95, 190),
                 shirt2=(95, 74, 155), pants=(48, 51, 78), pants2=(36, 38, 60),
                 shoe=(30, 30, 36), eye=(60, 90, 140), mouth=(120, 70, 60)),
    "Sunny": dict(skin=(242, 200, 160), hair=(230, 196, 90), shirt=(90, 190, 180),
                  shirt2=(67, 155, 148), pants=(222, 170, 80), pants2=(185, 137, 59),
                  shoe=(90, 75, 55), eye=(60, 110, 160), mouth=(150, 90, 70)),
    "Zuri": dict(skin=(120, 82, 60), hair=(22, 18, 16), shirt=(70, 170, 120),
                 shirt2=(52, 135, 94), pants=(60, 65, 100), pants2=(44, 48, 75),
                 shoe=(30, 30, 34), eye=(70, 105, 155), mouth=(80, 45, 38)),
}


def make_skin(p):
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    skin, hair = p["skin"], p["hair"]
    skin_dark = shade(skin, 0.82)
    hair_dark = shade(hair, 0.7)
    hair_light = shade(hair, 1.25)
    shirt, shirt2 = p["shirt"], p["shirt2"]
    pants, pants2 = p["pants"], p["pants2"]
    shoe, eye, mouth = p["shoe"], p["eye"], p["mouth"]

    # ── Head base ─────────────────────────────────────────────
    rect(img, 8, 8, 8, 8, skin)          # front
    rect(img, 16, 8, 8, 8, skin_dark)    # left
    rect(img, 0, 8, 8, 8, skin)          # right
    rect(img, 24, 8, 8, 8, hair)         # back
    rect(img, 8, 0, 8, 8, hair)          # top
    rect(img, 16, 0, 8, 8, skin_dark)    # bottom
    # hair fringe
    for x in range(8, 16):
        px(img, x, 8, hair)
        px(img, x, 9, hair)
    for x in (8, 9, 14, 15):
        px(img, x, 10, hair)
    # face
    px(img, 9, 11, (245, 245, 245)); px(img, 10, 11, eye)
    px(img, 13, 11, (245, 245, 245)); px(img, 14, 11, eye)
    px(img, 11, 12, skin_dark)
    for x in range(10, 14):
        px(img, x, 13, mouth)
    for x in range(8, 16):
        px(img, x, 15, skin_dark)
    # side hair
    for y in (8, 9, 10):
        px(img, 16, y, hair); px(img, 23, y, hair)
        px(img, 0, y, hair); px(img, 7, y, hair)

    # Hat overlay (hair fringe / subtle cap)
    rect(img, 40, 8, 8, 8, (0, 0, 0, 0))
    for x in range(40, 48):
        px(img, x, 8, hair)
        px(img, x, 9, hair)
    for x in (40, 41, 46, 47):
        px(img, x, 10, hair_dark)
        px(img, x, 11, hair_dark)

    # ── Body base ─────────────────────────────────────────────
    rect(img, 20, 20, 8, 12, shirt)
    rect(img, 32, 20, 8, 12, shirt2)
    rect(img, 16, 20, 4, 12, shirt2)
    rect(img, 28, 20, 4, 12, shirt)
    rect(img, 20, 16, 8, 4, shirt2)
    rect(img, 28, 16, 8, 4, shirt)
    for x in range(20, 28):
        px(img, x, 20, skin)  # neck
    for x in range(20, 28):
        px(img, x, 22, shirt2)
    # belt
    for x in range(20, 28):
        px(img, x, 30, shade(pants, 1.1))

    # jacket overlay
    rect(img, 20, 36, 8, 12, (0, 0, 0, 0))
    for x in range(20, 28):
        px(img, x, 36, shade(shirt, 0.88))
    for y in range(36, 40):
        px(img, 20, y, shade(shirt, 0.88))
        px(img, 27, y, shade(shirt, 0.88))

    # ── Arms ──────────────────────────────────────────────────
    for base_x, base_y in ((44, 20), (36, 52)):
        rect(img, base_x, base_y, 4, 12, shirt)
        rect(img, base_x + 4, base_y, 4, 12, shirt2)
        rect(img, base_x - 4, base_y, 4, 12, shirt2)
        rect(img, base_x + 8, base_y, 4, 12, shirt)
        rect(img, base_x, base_y - 4, 4, 4, shirt2)
        rect(img, base_x + 4, base_y - 4, 4, 4, shirt)
        # hands
        for yy in range(base_y + 8, base_y + 12):
            for xx in range(base_x, base_x + 4):
                px(img, xx, yy, skin)
        # sleeve overlay
        rect(img, base_x, base_y + 16, 4, 12, (0, 0, 0, 0))
        for xx in range(base_x, base_x + 4):
            px(img, xx, base_y + 16, shade(shirt, 0.9))

    # ── Legs ──────────────────────────────────────────────────
    for base_x, base_y in ((4, 20), (20, 52)):
        rect(img, base_x, base_y, 4, 12, pants)
        rect(img, base_x + 4, base_y, 4, 12, pants2)
        rect(img, base_x - 4, base_y, 4, 12, pants2)
        rect(img, base_x + 8, base_y, 4, 12, pants)
        rect(img, base_x, base_y - 4, 4, 4, pants2)
        rect(img, base_x + 4, base_y - 4, 4, 4, pants)
        for yy in range(base_y + 9, base_y + 12):
            for xx in range(base_x, base_x + 4):
                px(img, xx, yy, shoe)
        # pants overlay
        rect(img, base_x, base_y + 16, 4, 12, (0, 0, 0, 0))
        for xx in range(base_x, base_x + 4):
            px(img, xx, base_y + 16, shade(pants, 0.9))
            px(img, xx, base_y + 17, shade(pants, 0.9))

    # subtle highlights
    for x in range(9, 11):
        px(img, x, 10, hair_light)
    px(img, 22, 23, shade(shirt, 1.12))
    px(img, 5, 23, shade(pants, 1.12))
    return img


def make_cape(p):
    img = Image.new("RGBA", (64, 32), (0, 0, 0, 0))
    base = p["shirt"]
    for y in range(32):
        for x in range(64):
            img.putpixel((x, y), shade(base, 0.75 if (x // 4 + y // 4) % 2 else 0.68))
    for x in range(6, 58):
        for y in range(4, 28):
            if (x + y) % 7 == 0:
                img.putpixel((x, y), (240, 210, 120, 255))
    return img


for name, palette in PALETTES.items():
    make_skin(palette).save(os.path.join(OUT, f"{name}.png"))
    make_cape(palette).save(os.path.join(OUT, f"{name}-cape.png"))

print("skins written to", OUT)
