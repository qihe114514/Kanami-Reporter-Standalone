import os
import struct
import sys
import numpy as np
from PIL import Image
sys.path.insert(0, r'C:\Users\VOS-User\AppData\Local\Temp\kanami_work')
from kanami_core import normalize_region, FRAME_W, FRAME_H

WORK = r'C:\Users\VOS-User\AppData\Local\Temp\kanami_work'

# 手机模板（顶对齐归一化坐标）。命名与引擎变体规则一致：状态名.mobile.krt
# (name, frame_tag, x0, y0, w, h)
CUTS = [
    ('round_start',                 't80',  878, 137, 165, 46),   # 购买阶段 大字
    ('side_attacker',               't80',  884,  96, 152, 26),   # 攻方：安放炸弹
    ('round_ingame',                't480', 926,  35,  74, 22),   # 计时数字 01:15
    ('round_ingame_icon',           't480', 928,  19,  68, 19),   # 图标+7（静态）
    ('round_ingame_bomb_planted',   't515', 916,  19,  90, 41),   # 炸弹面板（整块）
]


def load_rgb(tag):
    return np.asarray(Image.open(rf'{WORK}\frames\{tag}.png').convert('RGB'))


def cut_and_save():
    os.makedirs(rf'{WORK}\templates_mobile', exist_ok=True)
    frames = {}
    models = []
    for name, tag, x, y, w, h in CUTS:
        if tag not in frames:
            frames[tag] = load_rgb(tag)
        crop, off_y = normalize_region(frames[tag], x - 2, x + w + 2, y - 2, y + h + 2)
        px = crop[2:-2, 2:-2]
        mean = float(px.mean())
        energy = float((px * px).sum() - px.sum() ** 2 / px.size)
        print(f'{name:32s} roi=({x},{y},{w},{h}) mean={mean:6.1f} energy/n={energy/px.size:7.1f}')
        models.append((name, (x, y, w, h), px.copy()))
        # 存 KRT（v1，帧尺寸 1920x1080，顶对齐坐标）
        path = rf'{WORK}\templates_mobile\{name}.mobile.krt'
        with open(path, 'wb') as f:
            f.write(struct.pack('<8I', 0x3154524B, 1, FRAME_W, FRAME_H, x, y, w, h))
            f.write(px.astype(np.uint8).tobytes())
    return models


def score_crop(model_px, model_roi, crop, cx0, cy0):
    """在归一化区域裁剪上给模板打分（含 13 偏移）。crop 左上角 = (cx0, cy0)。"""
    x, y, w, h = model_roi
    n = w * h
    t = model_px
    tm = t.mean()
    t_energy = (t * t).sum() - t.sum() ** 2 / n
    best = -1.0
    for ox, oy in [(0, 0), (-1, 0), (1, 0), (0, -1), (0, 1),
                   (-0.5, 0), (0.5, 0), (0, -0.5), (0, 0.5),
                   (-0.5, -0.5), (-0.5, 0.5), (0.5, -0.5), (0.5, 0.5)]:
        xs = x + ox - cx0; ys = y + oy - cy0
        is_int = ox == int(ox) and oy == int(oy)
        if is_int:
            win = crop[ys:ys + h, xs:xs + w]
        else:
            xi = np.arange(xs, xs + w); yi = np.arange(ys, ys + h)
            xg, yg = np.meshgrid(xi, yi)
            x0f = np.floor(xg).astype(int); y0f = np.floor(yg).astype(int)
            fxx = xg - x0f; fyy = yg - y0f
            x1f = np.minimum(x0f + 1, crop.shape[1] - 1); y1f = np.minimum(y0f + 1, crop.shape[0] - 1)
            top = crop[y0f, x0f] * (1 - fxx) + crop[y0f, x1f] * fxx
            bot = crop[y1f, x0f] * (1 - fxx) + crop[y1f, x1f] * fxx
            win = top * (1 - fyy) + bot * fyy
        s = win.sum(); sq = (win * win).sum()
        prod = (win * (t - tm)).sum()
        energy = sq - s * s / n
        if energy < 1.0:
            continue
        score = prod / np.sqrt(energy * t_energy)
        best = max(best, score)
    return best


if __name__ == '__main__':
    models = cut_and_save()
    # 自测：各模板在自己帧 + 相邻帧上的分数
    tests = {
        'round_start': ['t80', 't250', 't115', 't480'],
        'side_attacker': ['t80', 't250', 't115'],
        'round_ingame': ['t480', 't115', 't645', 't80', 't515'],
        'round_ingame_icon': ['t480', 't115', 't645', 't80', 't515'],
        'round_ingame_bomb_planted': ['t515', 't715', 't975', 't480', 't80'],
    }
    frame_cache = {}
    for name, roi, px in models:
        if name not in tests:
            continue
        x, y, w, h = roi
        row = []
        for tag in tests[name]:
            if tag not in frame_cache:
                img = load_rgb(tag)
                frame_cache[tag] = normalize_region(img, 860, 1060, 10, 200)[0]
            crop = frame_cache[tag]
            row.append(f'{tag}={score_crop(px, roi, crop, 860, 10):.3f}')
        print(f'{name:32s} ' + '  '.join(row))
