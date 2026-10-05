import os
import struct
import sys
import numpy as np
from PIL import Image
sys.path.insert(0, r'C:\Users\VOS-User\AppData\Local\Temp\kanami_work')
from kanami_core import normalize_region, FRAME_W, FRAME_H

WORK = r'C:\Users\VOS-User\AppData\Local\Temp\kanami_work'
OUT = rf'{WORK}\templates_mobile'
ORIGIN = (1200, 0)   # topscan 裁剪原点
GORIGIN = (890, 0)   # gaps 裁剪原点


def load(path, origin, full=(2772, 1280)):
    img = np.asarray(Image.open(path).convert('RGB'))
    return img, origin, full


def cut_krt(name, img, origin, full, x0n, y0n, x1n, y1n, thr=170, min_run=3):
    """在窗口内自动找亮字 bbox，裁剪并保存 KRT。"""
    crop, _ = normalize_region(img, x0n, x1n, y0n, y1n, origin=origin, full_size=full)
    bright = crop > thr
    rows = bright.sum(axis=1); cols = bright.sum(axis=0)
    ry = np.where(rows >= min_run)[0]; rx = np.where(cols >= min_run)[0]
    if len(ry) == 0 or len(rx) == 0:
        print(f'{name}: no text found'); return None
    by0, by1 = ry[0], ry[-1]; bx0, bx1 = rx[0], rx[-1]
    # 外扩 3px
    bx0 = max(0, bx0 - 3); by0 = max(0, by0 - 3)
    bx1 = min(crop.shape[1], bx1 + 4); by1 = min(crop.shape[0], by1 + 4)
    px = crop[by0:by1, bx0:bx1]
    gx = x0n + bx0; gy = y0n + by0
    w = bx1 - bx0; h = by1 - by0
    mean = float(px.mean())
    energy = float((px * px).sum() - px.sum() ** 2 / px.size)
    print(f'{name:34s} roi=({gx},{gy},{w},{h}) mean={mean:6.1f} e/n={energy/px.size:7.1f}')
    with open(rf'{OUT}\{name}.mobile.krt', 'wb') as f:
        f.write(struct.pack('<8I', 0x3154524B, 1, FRAME_W, FRAME_H, gx, gy, w, h))
        f.write(px.astype(np.uint8).tobytes())
    return (px, (gx, gy, w, h))


def score(model, img, origin, full, x0n, y0n, x1n, y1n):
    from cut_mobile import score_crop
    crop, _ = normalize_region(img, x0n, x1n, y0n, y1n, origin=origin, full_size=full)
    px, (gx, gy, w, h) = model
    return score_crop(px, (gx, gy, w, h), crop, x0n, y0n)


results = {}

# 1) 守方副标题（g756 / g760）
img756 = load(rf'{WORK}\gaps\g756.png', GORIGIN)
results['side_defender'] = cut_krt('side_defender', *img756, 884, 96, 1036, 122)
img760 = load(rf'{WORK}\gaps\g760.png', GORIGIN)
print('  side_defender self760:', score(results['side_defender'], *img760, 860, 90, 1060, 130))

# 2) 回合战败（g224 / g228 大字「回合战败」）
img224 = load(rf'{WORK}\gaps\g224.png', GORIGIN)
img228 = load(rf'{WORK}\gaps\g228.png', GORIGIN)
results['round_end_lose'] = cut_krt('round_end_lose', *img228, 820, 130, 1100, 200, thr=200, min_run=5)
print('  round_end_lose self224:', score(results['round_end_lose'], *img224, 800, 120, 1120, 210))

# 3) 回合获胜（g868 / f1441 大字「回合获胜」）
img868 = load(rf'{WORK}\gaps\g868.png', GORIGIN)
results['round_end_win'] = cut_krt('round_end_win', *img868, 820, 130, 1100, 200, thr=200, min_run=5)

# 4) 选人界面（frames/t20.png 整帧）「选择中」行
img20 = load(rf'{WORK}\frames\t20.png', (0, 0))
results['game_choose_character'] = cut_krt('game_choose_character', *img20, 140, 330, 260, 420, thr=170, min_run=3)

# 5) 对局胜利（frames/t1447.png 整帧）大字「胜利」
img1447 = load(rf'{WORK}\frames\t1447.png', (0, 0))
results['game_end_win'] = cut_krt('game_end_win', *img1447, 880, 400, 1060, 500, thr=200, min_run=5)

# 6) 开局计时数字（s112-s116 找 01:55/54/53）
for t in [112, 113, 114, 115, 116]:
    img = load(rf'{WORK}\topscan\s{t}.png', ORIGIN)
    m = cut_krt(f'round_ingame.d{t}', *img, 920, 30, 1005, 62, thr=170, min_run=2)
