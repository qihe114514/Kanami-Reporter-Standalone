"""从 score_plates.npz 切出左右比分板的数字模板（score_us.d0..d7 / score_enemy.d0..d4）并验证。

实机证据与结论（1456 帧 1fps，2772x1280 录屏）：
- 比分数字是白字深底，包围盒稳定在窗口内 y0=13、高 23（1224/1235 帧一致，其余 ±1px）；
- **灰度 ZNCC 区分度不够**：同一数字跨帧 0.97，但 0 与 3/6 之间能到 0.77~0.80，
  单靠灰度相关会把不同数字混起来。改用**二值形状 ZNCC**（两侧都按亮度二值化再算相关），
  不同数字之间降到 0.54~0.86、同一数字 0.94~0.98，配合"每个数字多个实例取最高分"才够用；
- 窗口顶部偶尔有亮带、赛前画面（t≈41）根本不是比分板、还有滚动动画/空白帧（t≈331、338），
  所以必须：连通域筛选 + 非平坦 + 按包围盒左上角锚定对齐。

标定用的标准答案是**人工逐帧核对**（见 GROUND_TRUTH 注释）得到的，不是自动推的：
自动分段在这个 HUD 上不可靠（同一数字跨帧 IoU 会掉到 0.8，0 与 6 又能到 0.85，两段区间重叠）。

用法：python cut_score.py [npz 路径] [输出模板目录]
"""
import os
import struct
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from kanami_core import FRAME_W, FRAME_H, MATCH_OFFSETS  # noqa: E402

DATA_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data")
DEFAULT_NPZ = os.path.join(DATA_DIR, "score_plates.npz")
DEFAULT_OUT = os.path.abspath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..",
                 "app", "src", "main", "assets", "templates")
)

BRIGHT = 165
MIN_AREA = 25
H_RANGE = (16, 32)
W_RANGE = (4, 26)
ASPECT = (0.8, 4.0)
MIN_STD = 8.0
PAD = 1
ALIGN = (20, 24)
INSTANCES_PER_DIGIT = 4
TRANSITION_MARGIN = 20    # 复核时排除段两端各 20s（翻牌动画区）

# ── 标准答案（人工逐帧核对 data/_gt_part0.png / _gt_part1.png，2026-10-06）──
# 录屏这局 7:4 结束（首胜 7 回合制）。人工核对点：
#   121→0:0  241→0:1  361→0:2  481→1:2  601→2:2  721→3:2
#   841→4:2  961→5:2  1081→5:3  1201→6:3  1321→6:4  1441→7:4
# (start, end, value) —— 区间两端留了余量以排除翻牌过渡帧。
GROUND_TRUTH = {
    'score_us': [
        (61, 400, 0),
        (440, 545, 1),
        (570, 690, 2),
        (700, 800, 3),
        (820, 930, 4),
        (950, 1130, 5),
        (1180, 1340, 6),
        (1360, 1456, 7),
    ],
    'score_enemy': [
        (61, 215, 0),
        (235, 330, 1),
        (380, 990, 2),
        (1010, 1260, 3),
        (1290, 1456, 4),
    ],
}
# 人工核对点：(秒, 我方读数, 对方读数)。这是唯一可信的标准答案来源——
# 两个核对点之间（约 120s）具体哪一刻翻牌无法确定，插值猜测会把相邻数字混进来。
CHECKPOINTS = [
    (121, 0, 0),
    (241, 0, 1),
    (361, 0, 2),
    (481, 1, 2),
    (601, 2, 2),
    (721, 3, 2),
    (841, 4, 2),
    (961, 5, 2),
    (1081, 5, 3),
    (1201, 6, 3),
    (1321, 6, 4),
    (1441, 7, 4),
]
CHECKPOINT_HALF_WINDOW = 2      # 取模板用的秒数半径
VERIFY_HALF_WINDOW = 10         # 验证用的秒数半径


def components(mask):
    """8 邻域连通域，返回 [(x0, y0, x1, y1, area)]。窗口很小，纯 python 足够快。"""
    h, w = mask.shape
    labels = np.zeros((h, w), dtype=int)
    parent = [0]

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[max(ra, rb)] = min(ra, rb)

    nxt = 1
    for y in range(h):
        for x in range(w):
            if not mask[y, x]:
                continue
            neighbours = []
            for dy in (-1, 0):
                for dx in (-1, 0, 1):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < h and 0 <= nx < w and labels[ny, nx]:
                        neighbours.append(labels[ny, nx])
            if not neighbours:
                labels[y, x] = nxt
                parent.append(nxt)
                nxt += 1
            else:
                labels[y, x] = min(neighbours)
                for n in neighbours[1:]:
                    union(neighbours[0], n)

    boxes = {}
    for y in range(h):
        for x in range(w):
            l = labels[y, x]
            if not l:
                continue
            r = find(l)
            b = boxes.setdefault(r, [x, y, x, y, 0])
            b[0] = min(b[0], x)
            b[1] = min(b[1], y)
            b[2] = max(b[2], x)
            b[3] = max(b[3], y)
            b[4] += 1
    return [tuple(b) for b in boxes.values()]


def digit_box(crop):
    mask = crop > BRIGHT
    best = None
    for x0, y0, x1, y1, area in components(mask):
        w = x1 - x0 + 1
        h = y1 - y0 + 1
        if not (H_RANGE[0] <= h <= H_RANGE[1] and W_RANGE[0] <= w <= W_RANGE[1]):
            continue
        if area < MIN_AREA or not (ASPECT[0] <= h / max(w, 1) <= ASPECT[1]):
            continue
        if best is None or area > best[4]:
            best = (x0, y0, x1, y1, area)
    return None if best is None else best[:4]


def aligned_crop(crop, bbox, size=ALIGN):
    """以包围盒左上角为锚点取固定窗口。

    不能用中心对齐：包围盒宽度有 ±1px 抖动，中心会跟着漂半个像素，
    同一数字的二值掩码就会错位、IoU 掉到 0.8 以下。
    """
    x0, y0 = bbox[0], bbox[1]
    fw, fh = size
    ox = x0 - 2
    oy = y0 - 2
    out = np.zeros((fh, fw), dtype=np.float64)
    sx0 = max(0, ox)
    sy0 = max(0, oy)
    sx1 = min(crop.shape[1], ox + fw)
    sy1 = min(crop.shape[0], oy + fh)
    if sx1 > sx0 and sy1 > sy0:
        out[sy0 - oy:sy1 - oy, sx0 - ox:sx1 - ox] = crop[sy0:sy1, sx0:sx1]
    return out


def iou(a, b):
    inter = int((a & b).sum())
    union = int((a | b).sum())
    return inter / union if union else -1.0


def iou_best_shift(a, b, rng=2):
    best = -1.0
    h, w = a.shape
    for dy in range(-rng, rng + 1):
        for dx in range(-rng, rng + 1):
            ys0, ys1 = max(0, dy), min(h, h + dy)
            xs0, xs1 = max(0, dx), min(w, w + dx)
            if ys1 <= ys0 or xs1 <= xs0:
                continue
            sub_a = a[ys0:ys1, xs0:xs1]
            sub_b = b[ys0 - dy:ys1 - dy, xs0 - dx:xs1 - dx]
            v = iou(sub_a, sub_b)
            if v > best:
                best = v
    return best


def score_ktr_binary(px, roi, crop, cx0, cy0):
    """二值形状 ZNCC，KTR 的 13 个偏移（px 为灰度模板像素，crop 为该板窗口）。"""
    x, y, w, h = roi
    tmask = (px > BRIGHT).astype(np.float64)
    tm = tmask.mean()
    t_energy = ((tmask - tm) ** 2).sum()
    if t_energy <= 0:
        return -1.0
    best = -1.0
    n = w * h
    for ox, oy in MATCH_OFFSETS:
        xs = x + ox - cx0
        ys = y + oy - cy0
        if ox == int(ox) and oy == int(oy):
            xi, yi = int(xs), int(ys)
            if xi < 0 or yi < 0 or xi + w > crop.shape[1] or yi + h > crop.shape[0]:
                continue
            win = crop[yi:yi + h, xi:xi + w]
        else:
            if xs < 0 or ys < 0 or xs + w > crop.shape[1] or ys + h > crop.shape[0]:
                continue
            xi = np.arange(xs, xs + w)
            yi = np.arange(ys, ys + h)
            xg, yg = np.meshgrid(xi, yi)
            x0f = np.floor(xg).astype(int)
            y0f = np.floor(yg).astype(int)
            fxx = xg - x0f
            fyy = yg - y0f
            x1f = np.minimum(x0f + 1, crop.shape[1] - 1)
            y1f = np.minimum(y0f + 1, crop.shape[0] - 1)
            top = crop[y0f, x0f] * (1 - fxx) + crop[y0f, x1f] * fxx
            bot = crop[y1f, x0f] * (1 - fxx) + crop[y1f, x1f] * fxx
            win = top * (1 - fyy) + bot * fyy
        wmask = (win > BRIGHT).astype(np.float64)
        wm = wmask.mean()
        energy = ((wmask - wm) ** 2).sum()
        if energy < 1.0:
            continue
        score = float(((wmask - wm) * (tmask - tm)).sum() / np.sqrt(energy * t_energy))
        if score > best:
            best = score
    return best


def write_krt(path, crop, window, bbox):
    x0, y0, x1, y1 = bbox
    rx0 = max(0, x0 - PAD)
    ry0 = max(0, y0 - PAD)
    rx1 = min(crop.shape[1], x1 + 1 + PAD)
    ry1 = min(crop.shape[0], y1 + 1 + PAD)
    px = crop[ry0:ry1, rx0:rx1].astype(np.uint8)
    h, w = px.shape
    gx = window[0] + rx0
    gy = window[1] + ry0
    with open(path, 'wb') as f:
        f.write(struct.pack('<8I', 0x3154524B, 1, FRAME_W, FRAME_H, gx, gy, w, h))
        f.write(px.tobytes())
    return (gx, gy, w, h)


def truth_at(prefix, second):
    for start, end, value in GROUND_TRUTH[prefix]:
        if start <= second <= end:
            return (value, start <= second <= end - TRANSITION_MARGIN or second <= start + TRANSITION_MARGIN)
    return (None, False)


def candidates_for(prefix, value, ts, detections, window_sec=CHECKPOINT_HALF_WINDOW):
    """取"人工确认该读数"的核对点 ±window_sec 内的帧作为候选。"""
    us_index = 1 if prefix == 'score_us' else 2
    out = []
    for cp in CHECKPOINTS:
        if cp[us_index] != value:
            continue
        for i, det in enumerate(detections):
            if det is not None and abs(int(ts[i]) - cp[0]) <= window_sec:
                out.append(i)
    return sorted(set(out))


def checkpoint_truth(prefix, second, window_sec=VERIFY_HALF_WINDOW):
    """若该秒落在某个核对点 ±window_sec 内，返回 (我方读数, 对方读数)，否则 None。"""
    for cp in CHECKPOINTS:
        if abs(second - cp[0]) <= window_sec:
            return cp[1], cp[2]
    return None


def largest_consistent_cluster(indices, detections):
    """把候选帧按二值形状聚类，返回最大的一类（同一数字的多数派）。"""
    if len(indices) <= 1:
        return indices
    parent = list(range(len(indices)))

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for a in range(len(indices)):
        for b in range(a + 1, len(indices)):
            if iou_best_shift(detections[indices[a]][2], detections[indices[b]][2]) >= 0.90:
                ra, rb = find(a), find(b)
                if ra != rb:
                    parent[max(ra, rb)] = min(ra, rb)
    groups = {}
    for k, i in enumerate(indices):
        groups.setdefault(find(k), []).append(i)
    return sorted(max(groups.values(), key=len))


def process(plates, window, label, prefix, out_dir, ts):
    us_mode = prefix == 'score_us'
    values = sorted({cp[1] if us_mode else cp[2] for cp in CHECKPOINTS})
    print(f"\n=== {label}比分板（{prefix}，核对点覆盖读数 {values}） ===")

    detections = []
    for i in range(plates.shape[0]):
        crop = plates[i].astype(np.float64)
        bbox = digit_box(crop)
        if bbox is None:
            detections.append(None)
            continue
        align = aligned_crop(crop, bbox)
        if align.std() < MIN_STD:
            detections.append(None)
            continue
        detections.append((bbox, align, (align > BRIGHT)))
    print(f"检出数字帧 {sum(1 for d in detections if d)}/{plates.shape[0]}")

    # 模板只用核对点 ±2s 的帧（唯一可信的窗口），并按形状聚类取多数派
    templates = {}
    for v in values:
        cand = candidates_for(prefix, v, ts, detections)
        core = largest_consistent_cluster(cand, detections)
        print(f"  读数 {v}：核对点窗口候选 {len(cand)} 帧 → 形状一致 {len(core)} 帧"
              + (f"（丢弃 {len(cand) - len(core)} 帧）" if len(cand) != len(core) else ""))
        if len(core) <= INSTANCES_PER_DIGIT:
            chosen = core
        else:
            sample = core[::max(1, len(core) // 40)]
            scored = []
            for i in sample:
                avg = float(np.mean([iou_best_shift(detections[i][2], detections[j][2])
                                     for j in sample if j != i])) if len(sample) > 1 else 1.0
                scored.append((avg, i))
            scored.sort(reverse=True)
            good = sorted(i for _, i in scored[:max(INSTANCES_PER_DIGIT * 3, 12)])
            step = max(1, len(good) // INSTANCES_PER_DIGIT)
            chosen = good[::step][:INSTANCES_PER_DIGIT]

        entries = []
        for i in chosen:
            crop = plates[i].astype(np.float64)
            bbox = detections[i][0]
            roi = write_krt(os.path.join(out_dir, f'{prefix}.d{v}.i{i}.mobile.krt'),
                            crop, window, bbox)
            gx, gy, w, h = roi
            px = crop[gy - window[1]:gy - window[1] + h, gx - window[0]:gx - window[0] + w]
            entries.append((roi, px.astype(np.uint8), i))
        templates[v] = entries
        print(f"  读数 {v}：实例秒 {[e[2] for e in entries]}  ROI={[e[0] for e in entries][:1]}")

    # 复算：每个数字取自己所有实例的最高分，再 argmax
    correct = wrong = 0
    confusion = {}
    per_value = {}
    min_best = 2.0
    margins = []
    seq = [None] * plates.shape[0]
    for i, det in enumerate(detections):
        if det is None:
            continue
        crop = plates[i].astype(np.float64)
        scores = {v: max(score_ktr_binary(px, roi, crop, window[0], window[1])
                         for roi, px, _ in entries)
                  for v, entries in templates.items() if entries}
        if not scores:
            continue
        ranked = sorted(scores.items(), key=lambda kv: -kv[1])
        best_v, best_s = ranked[0]
        runner = ranked[1][1] if len(ranked) > 1 else -1.0
        min_best = min(min_best, best_s)
        margins.append(best_s - runner)
        seq[i] = best_v
        cp = checkpoint_truth(prefix, int(ts[i]))
        if cp is None:
            continue
        actual = cp[0] if us_mode else cp[1]
        per_value.setdefault(actual, [0, 0])
        if best_v == actual:
            per_value[actual][0] += 1
            correct += 1
        else:
            per_value[actual][1] += 1
            wrong += 1
            key = (actual, best_v)
            confusion[key] = confusion.get(key, 0) + 1
    total = correct + wrong
    rate = correct / total * 100 if total else 0.0
    print(f"复算（核对点 ±{VERIFY_HALF_WINDOW}s 内逐帧比对）：{correct}/{total} 正确（{rate:.2f}%）")
    print(f"  最高分最低值 {min_best:.3f}；最高分与次高分之差 中位数 {np.median(margins):.3f} "
          f"最小 {np.min(margins):.3f}")
    for v in sorted(per_value):
        ok, bad = per_value[v]
        print(f"    读数 {v}: {ok}/{ok + bad} 正确（{ok / max(1, ok + bad) * 100:.1f}%）")
    if confusion:
        print(f"  混淆（标准→读成）：{sorted(confusion.items(), key=lambda kv: -kv[1])[:6]}")

    # 每个核对点的读数
    print("  核对点读数：")
    for cp in CHECKPOINTS:
        actual = cp[1] if us_mode else cp[2]
        i = next((k for k in range(plates.shape[0]) if int(ts[k]) == cp[0]), None)
        got = "—" if (i is None or seq[i] is None) else str(seq[i])
        mark = "✓" if got == str(actual) else "✗"
        print(f"    {cp[0]:4d}s 应为 {actual}，读到 {got} {mark}", end="")
    print()

    # 分类器自己报告的读数变化时刻（用于和"11 个回合"的结构对照）
    changes = []
    prev = None
    for i in range(plates.shape[0]):
        if seq[i] is None:
            continue
        if prev is not None and seq[i] != prev:
            changes.append((int(ts[i]), prev, seq[i]))
        prev = seq[i]
    print(f"  读数变化 {len(changes)} 次：{changes}")

    # 联系表
    rows = []
    for v in sorted(templates):
        tiles = [px for _, px, _ in templates[v]]
        if not tiles:
            continue
        hmax = max(t.shape[0] for t in tiles)
        wsum = sum(t.shape[1] + 3 for t in tiles) + 3
        row = np.full((hmax + 6, wsum), 40, dtype=np.uint8)
        x = 3
        for t in tiles:
            row[3:3 + t.shape[0], x:x + t.shape[1]] = t
            x += t.shape[1] + 3
        rows.append(row)
    if rows:
        wmax = max(r.shape[1] for r in rows)
        sheet = np.full((sum(r.shape[0] + 4 for r in rows), wmax), 20, dtype=np.uint8)
        y = 0
        for r in rows:
            sheet[y:y + r.shape[0], :r.shape[1]] = r
            y += r.shape[0] + 4
        sheet_path = os.path.join(DATA_DIR, f'{prefix}_templates.png')
        Image.fromarray(sheet).save(sheet_path)
        print(f"  模板联系表 -> {sheet_path}（自上而下 读数 0..{len(rows) - 1}）")
    return len(templates), rate


def main():
    npz_path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_NPZ
    out_dir = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_OUT
    os.makedirs(out_dir, exist_ok=True)
    data = np.load(npz_path)
    ts = data['t']
    left_window = tuple(int(v) for v in data['left_window'])
    right_window = tuple(int(v) for v in data['right_window'])
    print(f"窗口：我方 {left_window}，对方 {right_window}；帧数 {data['left'].shape[0]}")
    summary = {}
    summary['score_us'] = process(data['left'], left_window, '我方', 'score_us', out_dir, ts)
    summary['score_enemy'] = process(data['right'], right_window, '对方', 'score_enemy', out_dir, ts)
    print("\n汇总：")
    for k, (values, rate) in summary.items():
        print(f"  {k}: {values} 个数字，复算正确率 {rate:.2f}%")
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
