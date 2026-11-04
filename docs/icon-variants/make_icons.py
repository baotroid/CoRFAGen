#!/usr/bin/env python3
"""Draws the three proposed sets of ribbon icons (16 and 32 px) and a preview on Revit's light and dark ribbons.

usage: python make_icons.py     writes A-*, B-*, C-* folders and preview.png next to this script.
File names match CoRFAGen/Resources, so a chosen set is copied over them.
"""
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
SS = 8  # supersampling
BLUE, NAVY, SKY, PALE, ORANGE = '#1E6FD9', '#1A4F9C', '#5AA9F5', '#CFE4FB', '#F28C28'


class Pen:
    """Coordinates are 0..100 whatever the final size."""

    def __init__(self, size):
        self.size = size
        self.px = size * SS
        self.im = Image.new('RGBA', (self.px, self.px), (0, 0, 0, 0))
        self.d = ImageDraw.Draw(self.im)

    def p(self, x, y):
        return x * self.px / 100, y * self.px / 100

    def w(self, v):
        return max(1, int(v * self.px / 100))

    def line(self, pts, col, width):
        q = [self.p(*a) for a in pts]
        self.d.line(q, fill=col, width=self.w(width), joint='curve')
        r = self.w(width) / 2
        for x, y in (q[0], q[-1]):
            self.d.ellipse((x - r, y - r, x + r, y + r), fill=col)

    def poly(self, pts, fill):
        self.d.polygon([self.p(*a) for a in pts], fill=fill)

    def round(self, x0, y0, x1, y1, r, fill=None, outline=None, width=0):
        self.d.rounded_rectangle((*self.p(x0, y0), *self.p(x1, y1)), radius=self.w(r), fill=fill, outline=outline, width=self.w(width) if width else 1)

    def dot(self, x, y, r, fill, outline=None, width=0):
        cx, cy = self.p(x, y)
        R = self.w(r)
        self.d.ellipse((cx - R, cy - R, cx + R, cy + R), fill=fill, outline=outline, width=self.w(width) if width else 1)

    def done(self):
        return self.im.resize((self.size, self.size), Image.LANCZOS)


def star(p, x, y, r, col):
    k = r * 0.28
    p.poly([(x, y - r), (x + k, y - k), (x + r, y), (x + k, y + k), (x, y + r), (x - k, y + k), (x - r, y), (x - k, y - k)], col)


# ---- shapes shared by the sets
CUBE = dict(top=(50, 22), right=(78, 38), mid=(50, 54), left=(22, 38), rb=(78, 70), bottom=(50, 86), lb=(22, 70))


def cube_lines(p, col, w, dx=0, dy=0, s=1.0):
    c = {k: (50 + (v[0] - 50) * s + dx, 54 + (v[1] - 54) * s + dy) for k, v in CUBE.items()}
    p.line([c['left'], c['top'], c['right'], c['mid'], c['left']], col, w)
    p.line([c['left'], c['lb'], c['bottom'], c['rb'], c['right']], col, w)
    p.line([c['mid'], c['bottom']], col, w)


def cube_filled(p, a, b, c_, dx=0, dy=0, s=1.0):
    c = {k: (50 + (v[0] - 50) * s + dx, 54 + (v[1] - 54) * s + dy) for k, v in CUBE.items()}
    p.poly([c['left'], c['top'], c['right'], c['mid']], a)
    p.poly([c['left'], c['mid'], c['bottom'], c['lb']], b)
    p.poly([c['right'], c['mid'], c['bottom'], c['rb']], c_)


def broom(p, handle, head, bristle, w):
    p.line([(80, 14), (47, 52)], handle, w)
    p.poly([(37, 44), (58, 62), (50, 70), (29, 53)], head)
    p.poly([(28, 53), (51, 72), (37, 88), (12, 68)], bristle)


# ---- A: solid blue tile, white symbol
def tile(p):
    p.round(3, 3, 97, 97, 18, fill=BLUE)


def a_generate(size):
    p = Pen(size)
    tile(p)
    if size >= 32:
        cube_lines(p, 'white', 6.5, dx=-6, dy=4, s=0.78)
        star(p, 76, 25, 15, '#FFFFFF')
    else:
        cube_lines(p, 'white', 9, dx=-3, dy=2, s=0.86)
    return p.done()


def a_purge(size):
    p = Pen(size)
    tile(p)
    broom(p, 'white', 'white', PALE, 7 if size >= 32 else 9)
    return p.done()


def a_typegen(size):
    p = Pen(size)
    tile(p)
    w = 7 if size >= 32 else 9
    p.round(18, 22, 82, 78, 6, outline='white', width=w)
    p.d.line([p.p(18, 41), p.p(82, 41)], fill='white', width=p.w(w))
    p.d.line([p.p(18, 60), p.p(82, 60)], fill='white', width=p.w(w))
    p.d.line([p.p(42, 41), p.p(42, 78)], fill='white', width=p.w(w))
    return p.done()


def a_settings(size):
    p = Pen(size)
    tile(p)
    w = 7 if size >= 32 else 9
    for y, x in ((28, 64), (50, 36), (72, 58)):
        p.line([(18, y), (82, y)], 'white', w)
        p.dot(x, y, 10 if size >= 32 else 12, 'white')
    return p.done()


# ---- B: blue outline with an orange accent, no background
def b_generate(size):
    p = Pen(size)
    if size >= 32:
        cube_lines(p, BLUE, 7, dx=-7, dy=6, s=0.86)
        star(p, 80, 20, 17, ORANGE)
    else:
        cube_lines(p, BLUE, 10, dx=-4, dy=4, s=0.9)
        star(p, 84, 16, 16, ORANGE)
    return p.done()


def b_purge(size):
    p = Pen(size)
    w = 7.5 if size >= 32 else 10
    p.line([(84, 10), (50, 50)], BLUE, w)
    p.line([(36, 44), (60, 64)], ORANGE, w)
    p.line([(36, 44), (12, 70), (38, 92), (60, 64)], BLUE, w)
    if size >= 32:
        p.line([(28, 62), (42, 74)], BLUE, 5)
    return p.done()


def b_typegen(size):
    p = Pen(size)
    w = 7.5 if size >= 32 else 10
    p.round(10, 16, 90, 84, 7, outline=BLUE, width=w)
    p.d.line([p.p(10, 62), p.p(90, 62)], fill=BLUE, width=p.w(w))
    p.d.line([p.p(38, 39), p.p(38, 84)], fill=BLUE, width=p.w(w))
    p.d.line([p.p(10, 39), p.p(90, 39)], fill=ORANGE, width=p.w(w))
    return p.done()


def b_settings(size):
    p = Pen(size)
    w = 7.5 if size >= 32 else 10
    for y, x, col in ((22, 66, BLUE), (50, 34, ORANGE), (78, 60, BLUE)):
        p.line([(8, y), (92, y)], BLUE, w)
        p.dot(x, y, 12 if size >= 32 else 14, 'white' if size >= 32 else col, outline=col, width=w)
    return p.done()


# ---- C: three shades of blue, solid shapes without a background
def c_generate(size):
    p = Pen(size)
    cube_filled(p, SKY, BLUE, NAVY, dx=-5, dy=4, s=0.95 if size >= 32 else 1.0)
    star(p, 82, 18, 17, ORANGE)
    return p.done()


def c_purge(size):
    p = Pen(size)
    broom(p, NAVY, BLUE, SKY, 9 if size >= 32 else 11)
    return p.done()


def c_typegen(size):
    p = Pen(size)
    p.round(10, 14, 90, 36, 5, fill=NAVY)
    for i, y in enumerate((42, 66)):
        p.round(10, y, 42, y + 20, 4, fill=BLUE)
        p.round(47, y, 90, y + 20, 4, fill=SKY)
    return p.done()


def c_settings(size):
    p = Pen(size)
    w = 9 if size >= 32 else 11
    for y, x, col in ((22, 66, BLUE), (50, 34, NAVY), (78, 60, BLUE)):
        p.line([(8, y), (92, y)], SKY, w)
        p.dot(x, y, 14 if size >= 32 else 15, col)
    return p.done()


SETS = {
    'A-blue-tile': ('A. Синие плитки', (a_purge, a_generate, a_typegen, a_settings)),
    'B-blue-outline': ('B. Синий контур с оранжевым акцентом', (b_purge, b_generate, b_typegen, b_settings)),
    'C-blue-duotone': ('C. Объёмные оттенки синего', (c_purge, c_generate, c_typegen, c_settings)),
}
NAMES = ('purge', 'pregen', 'typegen', 'settings-sliders')
LABELS = ('Purge', 'Generate', 'TypeGen', 'Settings')


def font(size, bold=False):
    for f in (('segoeuib.ttf' if bold else 'segoeui.ttf'), 'arial.ttf'):
        try:
            return ImageFont.truetype(f, size)
        except OSError:
            pass
    return ImageFont.load_default()


def ribbon(icons, bg, fg, labels):
    """One panel at real size: large icons with labels."""
    cell = 74
    im = Image.new('RGB', (cell * 4 + 12, 74), bg)
    d = ImageDraw.Draw(im)
    for i, ic in enumerate(icons):
        x = 6 + i * cell + (cell - 32) // 2
        im.paste(ic, (x, 8), ic)
        t = labels[i]
        w = d.textlength(t, font=font(12))
        d.text((6 + i * cell + (cell - w) / 2, 46), t, fill=fg, font=font(12))
    return im


def main():
    rows = []
    for folder, (title, fns) in SETS.items():
        path = os.path.join(HERE, folder)
        os.makedirs(path, exist_ok=True)
        big, small = [], []
        for name, fn in zip(NAMES, fns):
            b, s = fn(32), fn(16)
            b.save(os.path.join(path, name + '-32.png'))
            s.save(os.path.join(path, name + '-16.png'))
            big.append(b)
            small.append(s)
        rows.append((title, big, small))

    W, row_h = 1240, 214
    sheet = Image.new('RGB', (W, 56 + row_h * len(rows)), 'white')
    d = ImageDraw.Draw(sheet)
    d.text((24, 16), 'Варианты значков CoRFAGen', fill='#111111', font=font(22, True))
    for r, (title, big, small) in enumerate(rows):
        y = 56 + r * row_h
        d.text((24, y + 4), title, fill=BLUE, font=font(17, True))
        d.text((24, y + 32), 'Светлая тема', fill='#666666', font=font(12))
        sheet.paste(ribbon(big, '#EEF0F2', '#222222', LABELS), (24, y + 50))
        d.text((340, y + 32), 'Тёмная тема', fill='#666666', font=font(12))
        sheet.paste(ribbon(big, '#3B4046', '#EEEEEE', LABELS), (340, y + 50))
        d.text((24, y + 130), '16 px', fill='#666666', font=font(12))
        for i, s in enumerate(small):
            for j, bg in enumerate(('#EEF0F2', '#3B4046')):
                sw = Image.new('RGB', (28, 28), bg)
                sw.paste(s, (6, 6), s)
                sheet.paste(sw, (70 + i * 34 + j * 150, y + 124))
        d.text((660, y + 32), 'Увеличено в 4 раза', fill='#666666', font=font(12))
        for i, b in enumerate(big):
            z = b.resize((128, 128), Image.LANCZOS)
            bg = Image.new('RGB', (136, 136), '#EEF0F2')
            bg.paste(z, (4, 4), z)
            sheet.paste(bg, (660 + i * 142, y + 50))
    sheet.save(os.path.join(HERE, 'preview.png'))


if __name__ == '__main__':
    main()
