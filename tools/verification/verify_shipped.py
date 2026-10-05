"""对**已装配进 APK**的模板做离线回归（不依赖设备），是这个项目自己的测试。

检查内容：
1. assets/templates 下每个 .krt 都能通过 Kotlin 加载器的全部校验（魔数/版本/1920x1080/
   ROI 合法/平坦度 energy≥16）；比分模板的解析命名能被引擎正确分组。
2. 状态模板在它们的来源帧上命中 ≥0.95（覆盖购买横幅、阵营副标题、开局计时、炸弹面板、胜利横幅）。
3. 比分数字模板在 12 个人工核对点 ±2s 上读数 100% 正确（二值形状相关，与 Kotlin 端同算法）。

用法：python verify_shipped.py    （全部通过退出码 0，否则 1）
"""
import glob
import os
import struct
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from kanami_core import FRAME_W, FRAME_H, normalize_region, Template  # noqa: E402
from cut_mobile import score_crop  # noqa: E402
from cut_score import (  # noqa: E402
    BRIGHT, CHECKPOINTS, score_ktr_binary, digit_box, aligned_crop, MIN_STD,
)

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.abspath(os.path.join(HERE, "..", "..", "app", "src", "main", "assets"))
TEMPLATES = os.path.join(ASSETS, "templates")
DATA = os.path.join(HERE, "data")

MIN_STATE_SCORE = 0.95
failures = []


def check(condition, message):
    print(("  ✓ " if condition else "  ✗ ") + message)
    if not condition:
        failures.append(message)


def load_state_pair(tag, x0, y0, x1, y1, origin=(0, 0), full=(2772, 1280), subdir="frames", name=None):
    """在指定实机帧的窗口里给模板打分（窗口按归一化坐标给）。"""
    path = os.path.join(DATA, subdir, f"{tag}.png")
    if not os.path.exists(path):
        return None
    img = np.asarray(Image.open(path).convert("RGB"))
    crop, _ = normalize_region(img, x0, x1, y0, y1, origin=origin, full_size=full)
    return crop


def self_scores():
    """状态模板用**灰度 ZNCC**（与引擎一致）：它们是横幅/面板/数字，内容各异；
    比分数字才用二值形状相关（炸弹面板这类整块偏暗的模板二值化后全 0，不能那样比）。"""
    print("\n[2] 状态模板在来源帧上的命中（灰度 ZNCC，与引擎同一算法）")
    cases = [
        ("round_start", "t80", 855, 115, 1065, 200, (0, 0), (2772, 1280), "frames"),
        ("side_attacker", "t80", 855, 80, 1065, 125, (0, 0), (2772, 1280), "frames"),
        ("round_ingame", "t480", 890, 10, 1050, 70, (0, 0), (2772, 1280), "frames"),
        ("round_ingame_bomb_planted", "t515", 880, 5, 1080, 70, (0, 0), (2772, 1280), "frames"),
        ("game_end_win", "t1447", 800, 280, 1100, 460, (0, 0), (2772, 1280), "frames"),
        ("side_defender", "g756", 840, 80, 1080, 130, (890, 0), (2772, 1280), "gaps"),
        ("round_end_lose", "g224", 800, 120, 1120, 210, (890, 0), (2772, 1280), "gaps"),
        ("round_end_win", "g868", 800, 120, 1120, 210, (890, 0), (2772, 1280), "gaps"),
    ]
    for name, tag, x0, y0, x1, y1, origin, full, subdir in cases:
        files = sorted(glob.glob(os.path.join(TEMPLATES, f"{name}*.krt")))
        if not files:
            check(False, f"{name}: assets 里找不到模板")
            continue
        crop = load_state_pair(tag, x0, y0, x1, y1, origin, full, subdir)
        if crop is None:
            check(False, f"{name}: 缺少参考帧 {tag}")
            continue
        best, best_file = -1.0, None
        for f in files:
            with open(f, "rb") as fh:
                _, _, _, _, rx, ry, rw, rh = struct.unpack("<8I", fh.read(32))
                px = np.frombuffer(fh.read(rw * rh), dtype=np.uint8).astype(np.float64).reshape(rh, rw)
            s = score_crop(px, (rx, ry, rw, rh), crop, x0, y0)
            if s > best:
                best, best_file = s, os.path.basename(f)
        check(best >= MIN_STATE_SCORE,
              f"{name} @ {tag}: {best:.3f}（{best_file}，要求 ≥{MIN_STATE_SCORE}）")


def format_and_naming_checks():
    print("\n[1] 模板格式与命名（复刻 Kotlin 加载器校验）")
    files = sorted(glob.glob(os.path.join(TEMPLATES, "*.krt")))
    state, score_us, score_enemy, other = 0, 0, 0, 0
    bad = []
    for f in files:
        with open(f, "rb") as fh:
            magic, ver, w, h, x, y, rw, rh = struct.unpack("<8I", fh.read(32))
            px = np.frombuffer(fh.read(rw * rh), dtype=np.uint8).astype(np.float64)
        energy = (px * px).sum() - px.sum() ** 2 / px.size
        ok = (magic == 0x3154524B and ver == 1 and w == FRAME_W and h == FRAME_H
              and x >= 0 and y >= 0 and rw >= 4 and rh >= 4
              and x + rw <= FRAME_W and y + rh <= FRAME_H
              and energy / px.size >= 16.0 and px.size == rw * rh)
        if not ok:
            bad.append(os.path.basename(f))
        base = os.path.basename(f)[:-4]
        if base.startswith("score_us.") or base.startswith("score_enemy."):
            prefix = "score_us." if base.startswith("score_us.") else "score_enemy."
            digit = base[len(prefix):].split(".")[0]
            if digit.startswith("d") and digit[1:].isdigit():
                if prefix == "score_us.":
                    score_us += 1
                else:
                    score_enemy += 1
            else:
                bad.append(os.path.basename(f) + "(比分命名)")
        elif base.startswith("side_"):
            other += 1
        else:
            state += 1
    check(not bad, f"{len(files)} 个模板全部通过格式校验" if not bad else f"不合格：{bad[:5]}")
    check(score_us >= 8 and score_enemy >= 5,
          f"比分模板分组：我方 {score_us} 个、对方 {score_enemy} 个（每数字 0..7 / 0..4 至少 1 个实例）")
    check(state >= 11 and other == 2,
          f"状态模板 {state} 个、阵营辅助 {other} 个")


def score_readout_checks():
    print("\n[3] 比分读数（人工核对点，取该秒及其后 2 秒）")
    print("    （不取核对点之前的帧：翻牌动画可能刚好落在核对点前一秒，那是上一个数字）")
    npz = os.path.join(DATA, "score_plates.npz")
    if not os.path.exists(npz):
        check(False, "缺少 data/score_plates.npz")
        return
    data = np.load(npz)
    ts = data["t"]
    boards = {
        "score_us": (data["left"], tuple(int(v) for v in data["left_window"]), 1),
        "score_enemy": (data["right"], tuple(int(v) for v in data["right_window"]), 2),
    }
    models = {}
    for prefix in boards:
        by_value = {}
        for f in sorted(glob.glob(os.path.join(TEMPLATES, f"{prefix}.*.krt"))):
            base = os.path.basename(f)[:-4]
            value = int(base[len(prefix) + 1:].split(".")[0][1:])
            with open(f, "rb") as fh:
                _, _, _, _, rx, ry, rw, rh = struct.unpack("<8I", fh.read(32))
                px = np.frombuffer(fh.read(rw * rh), dtype=np.uint8).astype(np.float64).reshape(rh, rw)
            by_value.setdefault(value, []).append((px, (rx, ry, rw, rh)))
        models[prefix] = by_value

    for prefix, (plates, window, cp_index) in boards.items():
        total = correct = 0
        wrong = []
        for cp in CHECKPOINTS:
            seconds, expected = cp[0], cp[cp_index]
            for i in range(plates.shape[0]):
                delta = int(ts[i]) - seconds
                if delta < 0 or delta > 2:
                    continue
                crop = plates[i].astype(np.float64)
                bbox = digit_box(crop)
                if bbox is None or aligned_crop(crop, bbox).std() < MIN_STD:
                    continue
                scores = {v: max(score_ktr_binary(px, roi, crop, window[0], window[1])
                                 for px, roi in entries)
                          for v, entries in models[prefix].items()}
                best_v = max(scores, key=scores.get)
                total += 1
                if best_v == expected:
                    correct += 1
                else:
                    wrong.append((seconds, expected, best_v))
        rate = correct / total * 100 if total else 0.0
        check(total > 0 and rate == 100.0,
              f"{prefix}: {correct}/{total}（{rate:.1f}%）" + (f" 错误 {wrong[:4]}" if wrong else ""))


def main():
    print("已装配模板离线回归")
    format_and_naming_checks()
    self_scores()
    score_readout_checks()
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
