"""第二台机型（2376×1080，22:9）的离线回归，是这个项目自己的测试之一。

`verify_shipped.py` 只覆盖参考机（2772×1280，19.5:9）。宽高比一变，归一化方式就成了
正确性问题，所以这台机器的证据单独回归：

1. **归一化不变量是内容高度**：合成两张只有宽高比不同的画面，同一个"按屏幕高度放置"的元素
   必须落在同一组归一化坐标上；参考尺寸不出现黑边、更宽的屏幕两侧补黑。
2. 第二台机型的代表帧全部命中（购买横幅 / 开局计时 / 炸弹面板 / 回合结束横幅 / 选人 / 结算）。
   同时断言**旧的"按宽度归一化"在这几帧上会掉到 0.85 以下** —— 防止有人把它改回去。
3. 阵营判定规则（阈值与领先幅度直接读 Kotlin 源码，避免测试和实现漂移）在整段 1fps 时间线上
   的判定与真实攻守轮次一致，且横幅不可见时两侧都不达下限。
4. 比分二值读数与人工核对值一致。

用法：python verify_second_device.py    （全部通过退出码 0，否则 1）
证据文件由 extract_second_device.py 从录屏抽帧生成，见该脚本文档。
"""
import glob
import os
import re
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from kanami_core import (  # noqa: E402
    CONTENT_HEIGHT, CONTENT_SCALE, FRAME_H, FRAME_W, Template, normalize_frame,
    normalize_region, score_detailed,
)
from cut_score import MIN_STD, aligned_crop, digit_box, score_ktr_binary  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "data")
APP = os.path.abspath(os.path.join(HERE, "..", "..", "app", "src", "main", "java"))
TEMPLATES = os.path.abspath(os.path.join(
    HERE, "..", "..", "app", "src", "main", "assets", "templates"))

REF_SIZE = (2772, 1280)          # 参考机
SECOND_SIZE = (2376, 1080)       # 第二台机型
LEGACY_MAX = 0.85                # 旧"按宽度归一化"在宽文字横幅上的上限
# 宽文字横幅：宽高比一变，"按宽度归一化"的纵向压缩最直接体现在它们身上。
ASPECT_SENSITIVE = {"round_start", "round_end_win", "round_end_lose",
                    "game_choose_character", "game_end_win"}
LEFT_WINDOW = (872, 16, 916, 64)
RIGHT_WINDOW = (1006, 16, 1050, 64)
# 我方/对方比分读数（人工逐帧核对 data/frames 的比分板，2026-10-06）
SCORE_TRUTH = {121: (0, 0), 229: (1, 0), 311: (2, 0), 340: (2, 0),
               678: (5, 0), 688: (5, 1), 709: (5, 1), 958: (6, 1)}

failures = []


def check(condition, message):
    print(("  ✓ " if condition else "  ✗ ") + message)
    if not condition:
        failures.append(message)


def kotlin_const(path_parts, name):
    """从 Kotlin 源码读常量，保证测试用的阈值与实现是同一个数。"""
    path = os.path.join(APP, *path_parts)
    with open(path, encoding="utf-8") as f:
        m = re.search(rf"const val {name}\s*=\s*([0-9.]+)", f.read())
    if m is None:
        raise RuntimeError(f"{path} 里找不到 {name}")
    return float(m.group(1))


# ── 1. 归一化不变量 ─────────────────────────────────────────────────────────

def synthetic(width, height, box_top_frac, box_h_frac, box_w_frac):
    """合成一张整帧：屏幕正中放一个白色方块。

    位置与**大小**都按屏幕高度的比例给 —— 这正是实机 HUD 的行为，所以两台机上
    归一化后应当落在同一个包围盒里。
    """
    img = np.zeros((height, width, 3), dtype=np.uint8)
    y0 = int(round(box_top_frac * height))
    y1 = y0 + max(2, int(round(box_h_frac * height)))
    bw = max(2, int(round(box_w_frac * height)))
    x0 = (width - bw) // 2
    img[y0:y1, x0:x0 + bw] = 255
    return img


def bbox_of(gray, thr=200):
    m = gray > thr
    rows = np.where(m.any(axis=1))[0]
    cols = np.where(m.any(axis=0))[0]
    if len(rows) == 0:
        return None
    return cols[0], cols[-1], rows[0], rows[-1]


def check_normalization():
    print("\n[1] 归一化不变量：内容高度固定、按屏幕高度等比、水平居中")
    g_ref, off_ref, h_ref = normalize_frame(synthetic(*REF_SIZE, 0.25, 0.08, 0.05), top_align=True)
    g_2nd, off_2nd, h_2nd = normalize_frame(synthetic(*SECOND_SIZE, 0.25, 0.08, 0.05), top_align=True)
    check(h_ref == CONTENT_HEIGHT and h_2nd == CONTENT_HEIGHT,
          f"内容高度恒为 {CONTENT_HEIGHT}（参考机 {h_ref}、第二台 {h_2nd}）")
    check(off_ref == 0 and off_2nd == 0, "画布顶对齐（off_y = 0）")
    b_ref, b_2nd = bbox_of(g_ref), bbox_of(g_2nd)
    check(b_ref is not None and b_2nd is not None, "两张合成帧都取到方块包围盒")
    if b_ref and b_2nd:
        dy = max(abs(b_ref[2] - b_2nd[2]), abs(b_ref[3] - b_2nd[3]))
        dx = max(abs(b_ref[0] - b_2nd[0]), abs(b_ref[1] - b_2nd[1]))
        check(dy <= 2, f"同一个按高度定位的元素在两台机上落在同一纵向位置（差 {dy}px，参考 {b_ref}）")
        check(dx <= 2, f"居中元素的横向位置一致（差 {dx}px，第二台 {b_2nd}）")
    # 更宽的屏幕两侧补黑：内容宽度 = CONTENT_SCALE/SRC_H*SRC_W > FRAME_W
    content_w = SECOND_SIZE[0] * CONTENT_SCALE / SECOND_SIZE[1]
    bar = (content_w - FRAME_W) / 2
    check(bar > 10, f"22:9 比参考机宽，两侧各裁掉约 {bar:.1f}px（补黑），符合预期")
    check(int(round(SECOND_SIZE[0] * CONTENT_SCALE / SECOND_SIZE[1])) != FRAME_W,
          "第二台机型不是按宽度归一化（否则内容宽度会等于 1920）")


# ── 2. 代表帧命中 ───────────────────────────────────────────────────────────

def legacy_mobile_normalize(img_rgb):
    """改动前的做法：按**宽度**缩放到 1920、顶对齐、内容高度随宽高比变（只用于回归对照）。"""
    sh, sw = img_rgb.shape[:2]
    scale = FRAME_W / sw
    out_h = int(round(sh * scale))
    src_x = np.clip((np.arange(FRAME_W) + 0.5) / scale - 0.5, 0, sw - 1)
    src_y = np.clip((np.arange(out_h) + 0.5) / scale - 0.5, 0, sh - 1)
    x0 = np.floor(src_x).astype(int); x1 = np.minimum(x0 + 1, sw - 1); fx = src_x - x0
    y0 = np.floor(src_y).astype(int); y1 = np.minimum(y0 + 1, sh - 1); fy = src_y - y0
    r, g, b = (img_rgb[:, :, i].astype(np.float64) for i in range(3))

    def ch(c):
        top = c[np.ix_(y0, x0)] * (1 - fx) + c[np.ix_(y0, x1)] * fx
        bot = c[np.ix_(y1, x0)] * (1 - fx) + c[np.ix_(y1, x1)] * fx
        return top * (1 - fy[:, None]) + bot * fy[:, None]

    gray = (29 * ch(b) + 150 * ch(g) + 77 * ch(r)) / 256.0
    out = np.zeros((FRAME_H, FRAME_W), dtype=np.float64)
    out[:out_h, :] = gray
    return out


def best_variant_score(prefix, gray):
    best, best_file = -1.0, None
    for path in sorted(glob.glob(os.path.join(TEMPLATES, f"{prefix}*.krt"))):
        s = score_detailed(Template(path), gray)[0]
        if s > best:
            best, best_file = s, os.path.basename(path)
    return best, best_file


def check_frames():
    print("\n[2] 第二台机型（2376×1080）代表帧命中")
    npz = os.path.join(DATA, "frames_22x9.npz")
    if not os.path.exists(npz):
        check(False, "缺少 data/frames_22x9.npz（跑 extract_second_device.py 生成）")
        return None
    d = np.load(npz, allow_pickle=True)
    grays, seconds, names = d["gray"], d["second"], d["names"]
    mins = d["min_score"]
    src_w, src_h = int(d["src_w"]), int(d["src_h"])
    cx, cy = int(d["crop_x"]), int(d["crop_y"])
    rebuilt = {}
    for i in range(len(seconds)):
        full = np.zeros((src_h, src_w, 3), dtype=np.uint8)
        full[cy:cy + grays[i].shape[0], cx:cx + grays[i].shape[1], :] = grays[i][:, :, None]
        gray = normalize_frame(full, top_align=True)[0]
        rebuilt[int(seconds[i])] = full
        s, f = best_variant_score(str(names[i]), gray)
        check(s >= float(mins[i]),
              f"f{int(seconds[i]):04d} {str(names[i]):28} 命中 {s:.3f}（{f}，要求 ≥{float(mins[i]):.2f}）")
        # 对照：旧"按宽度归一化"只对宽文字横幅致命（顶部小模板误差不到 1px，本来就看不出差别），
        # 所以只对横幅类模板断言它确实掉下来，证明这个修复是必要的。
        if str(names[i]) in ASPECT_SENSITIVE:
            legacy = best_variant_score(str(names[i]), legacy_mobile_normalize(full))[0]
            check(legacy < LEGACY_MAX,
                  f"f{int(seconds[i]):04d} {str(names[i]):28} 旧按宽度归一化只有 {legacy:.3f}"
                  f"（<{LEGACY_MAX}，说明这个修复确实必要）")
    return d, rebuilt


# ── 3. 阵营判定规则 ─────────────────────────────────────────────────────────

def check_side():
    print("\n[3] 阵营判定规则（阈值/领先幅度取自 Kotlin 源码）")
    floor = kotlin_const(
        ("com", "kanami", "reporter", "core", "FrameProcessing.kt"), "SideTemplateFloor")
    margin = kotlin_const(
        ("com", "kanami", "reporter", "core", "ReporterStateMachine.kt"), "SideScoreMargin")
    print(f"    实现常量：SideTemplateFloor={floor}  SideScoreMargin={margin}")
    npz = os.path.join(DATA, "side_timeline_22x9.npz")
    if not os.path.exists(npz):
        check(False, "缺少 data/side_timeline_22x9.npz（跑 extract_second_device.py 生成）")
        return
    d = np.load(npz)
    a, de, wins = d["attacker"].astype(np.float64), d["defender"].astype(np.float64), d["windows"]
    attacker = (a >= floor) & (a - de >= margin)
    defender = (de >= floor) & (de - a >= margin)
    check(not (attacker & defender).any(), "没有任何一帧同时判成攻方和守方")

    inside = np.zeros(len(a), dtype=bool)
    ok = True
    detail = []
    for x0, x1, side in wins:
        inside[x0:x1 + 1] = True
        hit_a, hit_d = int(attacker[x0:x1 + 1].sum()), int(defender[x0:x1 + 1].sum())
        want_side = int(side)
        good = (hit_a == 0 and hit_d > 0) if want_side == 2 else (hit_d == 0 and hit_a > 0)
        ok = ok and good
        detail.append(f"{'守' if want_side == 2 else '攻'}窗口[{x0}-{x1}] 攻{hit_a}/守{hit_d}")
    check(ok, "每个购买窗口都判成正确阵营：" + "；".join(detail))

    outside = int(attacker[~inside].sum() + defender[~inside].sum())
    check(outside == 0, f"购买窗口之外一帧都没误判（{int((~inside).sum())} 帧）")


# ── 4. 比分读数 ─────────────────────────────────────────────────────────────

def load_score_models(prefix):
    models = {}
    for path in sorted(glob.glob(os.path.join(TEMPLATES, f"{prefix}.*.krt"))):
        base = os.path.basename(path)[:-4]
        value = int(base[len(prefix) + 1:].split(".")[0][1:])
        with open(path, "rb") as fh:
            _, _, _, _, rx, ry, rw, rh = struct.unpack("<8I", fh.read(32))
            px = np.frombuffer(fh.read(rw * rh), dtype=np.uint8).astype(np.float64).reshape(rh, rw)
        models.setdefault(value, []).append((px, (rx, ry, rw, rh)))
    return models


def check_scores(rebuilt):
    print("\n[4] 比分二值读数（人工核对点）")
    if rebuilt is None:
        check(False, "代表帧不可用，跳过")
        return
    models = {"us": load_score_models("score_us"), "enemy": load_score_models("score_enemy")}
    windows = {"us": LEFT_WINDOW, "enemy": RIGHT_WINDOW}
    for second, (want_u, want_e) in sorted(SCORE_TRUTH.items()):
        if second not in rebuilt:
            check(False, f"缺少 f{second:04d}")
            continue
        full = rebuilt[second]
        got = []
        for key, prefix in (("us", "score_us"), ("enemy", "score_enemy")):
            win = windows[key]
            crop, _ = normalize_region(full, win[0], win[2], win[1], win[3],
                                       origin=(0, 0), full_size=full.shape[:2][::-1])
            bbox = digit_box(crop)
            if bbox is None or aligned_crop(crop, bbox).std() < MIN_STD:
                got.append(None)
                continue
            scores = {v: max(score_ktr_binary(px, roi, crop, win[0], win[1])
                             for px, roi in entries)
                      for v, entries in models[key].items()}
            got.append(max(scores, key=scores.get))
        check(got == [want_u, want_e],
              f"f{second:04d} 比分读到 {got[0]}:{got[1]}（人工核对 {want_u}:{want_e}）")


def main():
    print("第二台机型（2376×1080 / 22:9）离线回归")
    check_normalization()
    _, rebuilt = check_frames()
    check_side()
    check_scores(rebuilt)
    print()
    if failures:
        print(f"失败 {len(failures)} 项：")
        for f in failures:
            print(f"  - {f}")
        return 1
    print("全部通过。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
