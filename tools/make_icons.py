import sys, math
from PIL import Image, ImageDraw
out = sys.argv[1]
S = 512

def bg(d, c1, c2):
    # vertical gradient rounded square
    g = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(g)
    for y in range(S):
        t = y / (S - 1)
        col = tuple(int(c1[i] * (1 - t) + c2[i] * t) for i in range(3)) + (255,)
        gd.line([(0, y), (S, y)], fill=col)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([16, 16, S - 16, S - 16], radius=110, fill=255)
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    img.paste(g, (0, 0), mask)
    return img

def webcam(d, cx, cy, r, fg, lens):
    # body
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=fg)
    d.ellipse([cx - r * .55, cy - r * .55, cx + r * .55, cy + r * .55], fill=lens)
    d.ellipse([cx - r * .25, cy - r * .25, cx + r * .25, cy + r * .25], fill=fg)
    # stand
    d.rounded_rectangle([cx - r * .18, cy + r * .9, cx + r * .18, cy + r * 1.35], radius=int(r * .08), fill=fg)
    d.rounded_rectangle([cx - r * .75, cy + r * 1.3, cx + r * .75, cy + r * 1.52], radius=int(r * .11), fill=fg)

def arrows(d, cx, cy, R, w, fg):
    # two curved arrows forming a swap circle
    box = [cx - R, cy - R, cx + R, cy + R]
    d.arc(box, 200, 330, fill=fg, width=w)
    d.arc(box, 20, 150, fill=fg, width=w)
    for ang, sgn in ((330, 1), (150, 1)):
        a = math.radians(ang)
        x, y = cx + R * math.cos(a), cy + R * math.sin(a)
        # tangent direction (clockwise)
        tx, ty = -math.sin(a), math.cos(a)
        nx, ny = math.cos(a), math.sin(a)
        L = w * 1.9
        tip = (x + tx * L, y + ty * L)
        p1 = (x + nx * L * .8, y + ny * L * .8)
        p2 = (x - nx * L * .8, y - ny * L * .8)
        d.polygon([tip, p1, p2], fill=fg)

def save(img, name, sizes):
    img.save(f"{out}/{name}.ico", sizes=[(s, s) for s in sizes])
    img.resize((256, 256), Image.LANCZOS).save(f"{out}/{name}.png")

white = (255, 255, 255, 255)
# App icon: blue->violet, webcam inside swap arrows
img = bg(None, (37, 99, 235), (124, 58, 237)); d = ImageDraw.Draw(img)
arrows(d, S / 2, S / 2 - 6, 178, 34, white)
webcam(d, S / 2, S / 2 - 40, 88, white, (60, 70, 200, 255))
save(img, "app", [16, 20, 24, 32, 40, 48, 64, 128, 256])

# Tray icons: bold, simple
for name, c1, c2, lens in (("desk", (37, 99, 235), (29, 78, 216), (29, 78, 216, 255)),
                           ("rig", (249, 115, 22), (234, 88, 12), (194, 65, 12, 255)),
                           ("off", (115, 115, 125), (82, 82, 91), (82, 82, 91, 255))):
    img = bg(None, c1, c2); d = ImageDraw.Draw(img)
    webcam(d, S / 2, S / 2 - 50, 150, white, lens)
    save(img, name, [16, 20, 24, 32, 48, 256])
