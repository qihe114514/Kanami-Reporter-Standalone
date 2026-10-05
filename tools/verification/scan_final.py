import glob
import os
import struct
import sys
import numpy as np
from PIL import Image
sys.path.insert(0, r'C:\Users\VOS-User\AppData\Local\Temp\kanami_work')
from kanami_core import normalize_region
from cut_mobile import score_crop

WORK = r'C:\Users\VOS-User\AppData\Local\Temp\kanami_work'
CX0, CY0 = 810, 10  # 扫描区域归一化原点（覆盖横幅/副标题/计时/炸弹面板）

templates = {}
for f in sorted(os.listdir(rf'{WORK}\templates_mobile')):
    if f == 'round_ingame.d115.mobile.krt':
        continue
    name = f.replace('.mobile.krt', '')
    with open(rf'{WORK}\templates_mobile\{f}', 'rb') as fh:
        head = fh.read(32)
        _, _, _, _, x, y, w, h = struct.unpack('<8I', head)
        px = np.frombuffer(fh.read(w * h), dtype=np.uint8).astype(np.float64).reshape(h, w)
    templates[name] = (px, (x, y, w, h))
# 顶部扫描区域只覆盖 y<=210 的 ROI（VS/结算模板在更下方，单独验证过）
templates = {n: (px, roi) for n, (px, roi) in templates.items() if roi[1] + roi[3] + 1 <= 210}
print(f'{len(templates)} templates:', ', '.join(templates))

rows = []
for path in sorted(glob.glob(rf'{WORK}\topscan\f_*.png')):
    img = np.asarray(Image.open(path).convert('RGB'))
    crop, _ = normalize_region(img, 810, 1110, 10, 210, origin=(1200, 0), full_size=(2772, 1280))
    t = int(os.path.basename(path)[2:6])
    scores = {n: score_crop(px, roi, crop, CX0, CY0) for n, (px, roi) in templates.items()}
    rows.append((t, scores))

names = list(templates)
header = 'time  ' + '  '.join(f'{n[:14]:>14}' for n in names)
out = [header]
for t, scores in rows:
    out.append(f'{t:4d}  ' + '  '.join(f'{scores[n]:14.3f}' for n in names))
open(rf'{WORK}\timeline_final.txt', 'w', encoding='utf-8').write('\n'.join(out))

# 状态判定与跨度汇总（手机 FSM 优先级规则）
def state_of(s):
    if s.get('round_start', -1) >= 0.90:
        if s['side_attacker'] >= 0.85: return 'BUY(攻)'
        if s['side_defender'] >= 0.85: return 'BUY(守)'
        return 'BUY(?)'
    if s['round_ingame_bomb_planted'] >= 0.90: return 'BOMB'
    if max(s[f'round_ingame.d{d}'] for d in [112, 113, 114, 116]) >= 0.90: return 'INGAME'
    if s['round_end_win'] >= 0.90: return 'END_WIN'
    if s['round_end_lose'] >= 0.90: return 'END_LOSE'
    return '.'

cur, start = None, None
spans = []
for t, s in rows + [(9999, {})]:
    st = state_of(s) if s else '.'
    if st != cur:
        if cur is not None and cur != '.':
            spans.append((start, t - 1, cur))
        cur, start = st, t
for a, b, st in spans:
    print(f'{a:4d}-{b:4d} ({b-a+1:3d}s)  {st}')
