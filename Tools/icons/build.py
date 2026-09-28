#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Значок Salamander для всех редакторов из ОДНОГО мастер-рисунка.

Рисунок — саламандра полукольцом, вид сверху — задан геометрией ниже
(дуга-хребет, ширина тела по длине, голова, лапы, пятна), а не нарисован
руками: так его можно подправить числом и пересобрать всё разом.

    python Tools/icons/build.py

Что делает:
  1. пишет мастер-SVG: salamander.svg (полный, от 32 px) и
     salamander-small.svg (для 16 px — без пятен и глаз, лапы толще);
  2. рендерит PNG через Chromium (render.js, нужен Node и пакет playwright:
     npm i -g playwright && npx playwright install chromium);
  3. раскладывает результат по пакетам:
       Tools/vscode-salamander/icons/   salamander.svg, salamander-128.png
       Tools/sublime-salamander/icons/  file_type_salamander.png (+@2x, @3x)
       Tools/rider/icons/               salamander.svg (16x16, для плагина)
       Dsl.Unity/Editor/SalamanderIcon.cs  PNG 64 px, вшитый в код base64
  С флагом --svg-only делает только шаг 1 (Chromium не нужен).
"""
import base64
import math
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))

BODY = "#F0641E"   # огненно-оранжевый: читается и на тёмной, и на светлой теме
SPOT = "#FFD23F"
EYE = "#2A1A10"

# ---------------------------------------------------------------------------
# геометрия (единицы — условные, рамку подгоняем по факту в конце)
# ---------------------------------------------------------------------------
CX, CY = 16.0, 17.0


def radius(t):
    # хвост к концу закручивается внутрь — полукольцо, а не ровная дуга
    return 10.2 - 2.6 * max(0.0, t - 0.62) / 0.38


def angle(t):
    # от 205° до -45°: голова слева внизу, хребет идёт через верх, хвост справа
    return math.radians(205 - t * 250)


def pt(t):
    a = angle(t)
    r = radius(t)
    return (CX + r * math.cos(a), CY - r * math.sin(a))


def tangent(t, h=1e-3):
    t = min(max(t, h), 1 - h)
    (x1, y1), (x2, y2) = pt(t - h), pt(t + h)
    d = math.hypot(x2 - x1, y2 - y1)
    return ((x2 - x1) / d, (y2 - y1) / d)


def width(t):
    if t < 0.10:                       # шея
        return 3.3 + (t / 0.10) * 0.9
    if t < 0.42:                       # туловище, чуть шире посередине
        return 4.2 + 0.4 * math.sin((t - 0.10) / 0.32 * math.pi)
    # хвост сходит на нет
    return max(0.35, 4.2 * max(0.0, 1 - (t - 0.42) / 0.58) ** 1.15)


def build_geometry():
    n = 120
    left, right = [], []
    for i in range(n + 1):
        t = i / n
        x, y = pt(t)
        tx, ty = tangent(t)
        nx, ny = -ty, tx
        w = width(t) / 2
        left.append((x + nx * w, y + ny * w))
        right.append((x - nx * w, y - ny * w))
    poly = left + right[::-1]
    body = "M" + " L".join(f"{x:.2f},{y:.2f}" for x, y in poly) + " Z"

    # спинная линия-блик: вдоль хребта от шеи до середины хвоста
    spine = "M" + " L".join(f"{pt(i / 60)[0]:.2f},{pt(i / 60)[1]:.2f}" for i in range(2, 44))

    # голова — широкая и тупая, как у саламандры: эллипс поперёк больше, чем вдоль
    hx, hy = pt(0)
    tx, ty = tangent(0)
    fx, fy = -tx, -ty
    head = (hx + fx * 1.7, hy + fy * 1.7)
    head_deg = math.degrees(math.atan2(fy, fx))
    nx, ny = -fy, fx
    # глаза — на боках головы, чуть впереди середины (вид сверху)
    eyes = [(head[0] + fx * 0.7 + nx * s * 1.75, head[1] + fy * 0.7 + ny * s * 1.75) for s in (1, -1)]

    ends = []

    def rot(vx, vy, deg):
        a = math.radians(deg)
        return (vx * math.cos(a) - vy * math.sin(a), vx * math.sin(a) + vy * math.cos(a))

    def norm(vx, vy):
        d = math.hypot(vx, vy)
        return (vx / d, vy / d)

    def leg(t, side, front):
        """Лапа: плечо от бока, локоть, предплечье загнуто вперёд (передние —
        к голове, задние — к хвосту) и кисть из четырёх растопыренных пальцев."""
        x, y = pt(t)
        tx, ty = tangent(t)
        nx, ny = -ty * side, tx * side                  # наружу от тела
        bx, by = (-tx, -ty) if front else (tx, ty)      # куда загибается лапа
        w = width(t) / 2
        sx, sy = x + nx * w * 0.5, y + ny * w * 0.5
        ux, uy = norm(nx + bx * 0.35, ny + by * 0.35)   # плечо: наружу, чуть вперёд
        ex, ey = sx + ux * 3.0, sy + uy * 3.0           # локоть
        fx_, fy_ = norm(nx * 0.25 + bx, ny * 0.25 + by) # предплечье: почти вдоль тела
        hx_, hy_ = ex + fx_ * 2.0, ey + fy_ * 2.0       # кисть
        toes = []
        tips = []
        for deg, ln in ((-58, 1.15), (-20, 1.4), (18, 1.4), (56, 1.15)):
            dx, dy = rot(fx_, fy_, deg)
            px, py = hx_ + dx * ln, hy_ + dy * ln
            toes.append(f"M{hx_:.2f},{hy_:.2f} L{px:.2f},{py:.2f}")
            tips.append((px, py))
            ends.append((px, py))
        ends.extend([(ex, ey), (hx_, hy_)])
        return (f"M{sx:.2f},{sy:.2f} L{ex:.2f},{ey:.2f}",       # плечо (толще)
                f"M{ex:.2f},{ey:.2f} L{hx_:.2f},{hy_:.2f}",     # предплечье (тоньше)
                " ".join(toes), tips)

    parts = [leg(t, s, front) for t, front in ((0.17, True), (0.47, False)) for s in (1, -1)]
    upper = " ".join(p[0] for p in parts)
    fore = " ".join(p[1] for p in parts)
    toes = " ".join(p[2] for p in parts)
    tips = [q for p in parts for q in p[3]]

    # пятна — по обе стороны хребта вразбежку и разного размера, как у живой
    spots = []
    for t, side, r in ((0.06, 1, 0.75), (0.15, -1, 1.0), (0.25, 1, 1.1), (0.34, -1, 0.9),
                       (0.43, 1, 0.95), (0.53, -1, 0.75), (0.62, 1, 0.6), (0.71, -1, 0.45)):
        x, y = pt(t)
        tx, ty = tangent(t)
        off = width(t) * 0.2 * side
        spots.append((x - ty * off, y + tx * off, r))

    # рамка по фактическим точкам, квадрат с полем (поле + толщина лапы и тени)
    pts = poly + ends + [(head[0] + math.cos(k * math.pi / 8) * 3.3,
                          head[1] + math.sin(k * math.pi / 8) * 3.3) for k in range(16)]
    pad = 2.1
    x0 = min(p[0] for p in pts) - pad
    x1 = max(p[0] for p in pts) + pad
    y0 = min(p[1] for p in pts) - pad
    y1 = max(p[1] for p in pts) + pad
    side = max(x1 - x0, y1 - y0)
    vx, vy = (x0 + x1 - side) / 2, (y0 + y1 - side) / 2
    return dict(body=body, spine=spine, head=head, head_deg=head_deg, eyes=eyes,
                upper=upper, fore=fore, toes=toes, tips=tips, spots=spots,
                box=(vx, vy, side), vb=f"{vx:.2f} {vy:.2f} {side:.2f} {side:.2f}")


OUTLINE = "#8A280A"      # тёмный контур: держит силуэт на светлом фоне
LIGHT = "#FF9A45"        # верх-лево градиента
DARK = "#E2401A"         # низ-право градиента
HIGHLIGHT = "#FFC27A"    # спинной блик


def silhouette(g, paint, grow, detail):
    """Вся фигура одним цветом. grow — сколько добавить к толщине: так из
    одного описания получаются и тень, и контур (толще), и заливка (как есть)."""
    hx, hy = g["head"]
    up, fo, to = (1.9, 1.45, 0.62) if detail else (2.4, 2.0, 1.1)
    out = [
        f'<g fill="none" stroke="{paint}" stroke-linecap="round" stroke-linejoin="round">',
        f'<path stroke-width="{up + grow:.2f}" d="{g["upper"]}"/>',
        f'<path stroke-width="{fo + grow:.2f}" d="{g["fore"]}"/>',
        f'<path stroke-width="{to + grow:.2f}" d="{g["toes"]}"/></g>',
    ]
    if detail:   # подушечки на кончиках пальцев
        out.append(f'<g fill="{paint}">' + "".join(
            f'<circle cx="{x:.2f}" cy="{y:.2f}" r="{0.42 + grow / 2:.2f}"/>' for x, y in g["tips"]) + "</g>")
    sw = f' stroke="{paint}" stroke-width="{grow:.2f}" stroke-linejoin="round"' if grow else ""
    out.append(f'<path fill="{paint}"{sw} d="{g["body"]}"/>')
    out.append(f'<ellipse fill="{paint}"{sw} cx="{hx:.2f}" cy="{hy:.2f}" rx="3.0" ry="3.3" '
               f'transform="rotate({g["head_deg"]:.1f} {hx:.2f} {hy:.2f})"/>')
    return "".join(out)


def make_svg(g, detail, size):
    vx, vy, side = g["box"]
    note = ("Полная версия: для размеров от 32 px." if detail else
            "Упрощённая версия для 16 px: без тени, блика, пятен и глаз - на 16 пикселях они превращаются в шум.")
    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{g["vb"]}" width="{size}" height="{size}">',
        f'  <!-- Salamander: саламандра полукольцом, вид сверху. {note}',
        f'       Сгенерировано Tools/icons/build.py - правьте генератор, не файл. -->',
    ]
    if detail:
        parts += [
            f'  <defs><linearGradient id="skin" gradientUnits="userSpaceOnUse" '
            f'x1="{vx:.2f}" y1="{vy:.2f}" x2="{vx + side:.2f}" y2="{vy + side:.2f}">'
            f'<stop offset="0" stop-color="{LIGHT}"/><stop offset="1" stop-color="{DARK}"/>'
            f'</linearGradient></defs>',
            # мягкая тень без фильтров: сдвинутый полупрозрачный силуэт
            f'  <g opacity="0.28" transform="translate(0.45 0.6)">{silhouette(g, "#000", 0.9, True)}</g>',
            f'  {silhouette(g, OUTLINE, 0.9, True)}',
            f'  {silhouette(g, "url(#skin)", 0, True)}',
            f'  <path fill="none" stroke="{HIGHLIGHT}" stroke-width="0.8" stroke-linecap="round" '
            f'opacity="0.55" d="{g["spine"]}"/>',
            '  <g fill="#FFD84A">' + "".join(
                f'<circle cx="{x:.2f}" cy="{y:.2f}" r="{r}"/>' for x, y, r in g["spots"]) + "</g>",
        ]
        (e1x, e1y), (e2x, e2y) = g["eyes"]
        parts.append('  <g fill="#1F130C">' + "".join(
            f'<circle cx="{x:.2f}" cy="{y:.2f}" r="0.85"/>' for x, y in g["eyes"]) + "</g>")
        parts.append('  <g fill="#FFFFFF" opacity="0.85">' + "".join(
            f'<circle cx="{x - 0.25:.2f}" cy="{y - 0.3:.2f}" r="0.28"/>' for x, y in g["eyes"]) + "</g>")
    else:
        parts += [f'  {silhouette(g, OUTLINE, 0.8, False)}', f'  {silhouette(g, BODY, 0, False)}']
    parts.append('</svg>')
    return "\n".join(parts) + "\n"


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    mode = "wb" if isinstance(data, bytes) else "w"
    kw = {} if mode == "wb" else {"encoding": "utf-8", "newline": "\n"}
    with open(path, mode, **kw) as f:
        f.write(data)
    print("записан", os.path.relpath(path, REPO).replace("\\", "/"))


UNITY_CS = '''using UnityEngine;

namespace Dsl.Unity.Editor
{{
    /// <summary>
    /// Значок .sal в окне Project. PNG вшит в код, а не лежит рядом файлом:
    /// Dsl.Unity копируют в проекты папкой, и путь или GUID картинки в каждом
    /// проекте свой, а строка в коде едет вместе с импортёром без настроек.
    ///
    /// СГЕНЕРИРОВАНО Tools/icons/build.py из Tools/icons/salamander.svg —
    /// руками не править: значок меняется в генераторе.
    /// </summary>
    internal static class SalamanderIcon
    {{
        private const string PngBase64 =
{chunks};

        /// <summary>Новая текстура значка ({size}x{size}, RGBA, без мипмапов).</summary>
        public static Texture2D Create()
        {{
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {{
                name = "SalamanderIcon",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideInHierarchy,
            }};
            texture.LoadImage(System.Convert.FromBase64String(PngBase64));
            return texture;
        }}
    }}
}}
'''


def main():
    g = build_geometry()
    big = make_svg(g, True, 32)
    small = make_svg(g, False, 16)
    write(os.path.join(HERE, "salamander.svg"), big)
    write(os.path.join(HERE, "salamander-small.svg"), small)
    if "--svg-only" in sys.argv:
        return

    out = os.path.join(HERE, "png")
    os.makedirs(out, exist_ok=True)
    subprocess.run(["node", os.path.join(HERE, "render.js"), out], check=True, cwd=HERE)

    def png(size):
        return os.path.join(out, f"salamander-{size}.png")

    # VS Code: SVG масштабируется сам; PNG 128 — значок расширения в списке
    write(os.path.join(REPO, "Tools/vscode-salamander/icons/salamander.svg"), big)
    shutil.copyfile(png(128), os.path.join(REPO, "Tools/vscode-salamander/icons/salamander-128.png"))

    # Sublime: 100 / 200 / 300 % масштаба
    sub = os.path.join(REPO, "Tools/sublime-salamander/icons")
    os.makedirs(sub, exist_ok=True)
    for size, suffix in ((16, ""), (32, "@2x"), (48, "@3x")):
        shutil.copyfile(png(size), os.path.join(sub, f"file_type_salamander{suffix}.png"))

    # Rider/IntelliJ: значок типа файла — SVG 16x16 (IDE сама масштабирует под HiDPI)
    write(os.path.join(REPO, "Tools/rider/icons/salamander.svg"), small)

    # Unity: PNG 64 px в base64 внутри кода импортёра
    b64 = base64.b64encode(open(png(64), "rb").read()).decode("ascii")
    parts = [b64[i:i + 100] for i in range(0, len(b64), 100)]
    chunks = "\n".join(f'            "{p}"' + (" +" if i < len(parts) - 1 else "") for i, p in enumerate(parts))
    write(os.path.join(REPO, "Dsl.Unity/Editor/SalamanderIcon.cs"), UNITY_CS.format(chunks=chunks, size=64))


if __name__ == "__main__":
    main()
