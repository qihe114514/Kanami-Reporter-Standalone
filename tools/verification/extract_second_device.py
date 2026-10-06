"""从**第二台机型**的录屏抽帧里生成紧凑的离线回归证据（供 `verify_second_device.py` 用）。

背景：2026-10-06 的第二台实机是 2376×1080（22:9），与参考机 2772×1280（19.5:9）宽高比
不同。录屏本身 2.6GB 不便入库，所以只保留两类证据：

- `data/frames_22x9.npz`：代表帧的**源分辨率灰度裁剪**（源坐标 x 980..1420、y 0..624，
  足以覆盖全部模板 ROI 映射到的源区域）。验证脚本把它贴回黑底整帧再走真实归一化管线，
  因此回归的是"归一化 + 模板匹配"整条链路，而不只是存下来的分数。
- `data/side_timeline_22x9.npz`：整段 1fps（979 帧）上阵营副标题两侧的 ZNCC 分数与购买窗口
  标注（5 个守方窗口 + 3 个攻方窗口），用于回归阵营判定规则。

用法：
    python extract_second_device.py <1fps 帧目录> [输出目录]
帧目录这样抽（本机 ffmpeg 不在 PATH，用任意一份即可）：
    ffmpeg -i <录屏> -vf fps=1 frames/f%04d.png
"""
import glob
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from kanami_core import Template, normalize_frame, score_detailed  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "data")
TEMPLATES = os.path.abspath(os.path.join(
    HERE, "..", "..", "app", "src", "main", "assets", "templates"))

SRC_W, SRC_H = 2376, 1080
CROP_X, CROP_Y, CROP_W, CROP_H = 980, 0, 440, 624

# 代表帧 → 应命中的状态模板前缀与最低分。
# 状态模板要求 ≥0.90（引擎阈值）；阵营副标题只要求过 SideTemplateFloor。
REPRESENTATIVE = [
    (55, "game_choose_character", 0.90),
    (121, "round_start", 0.90),
    (229, "round_ingame", 0.90),
    (311, "round_end_win", 0.90),
    (340, "side_defender", 0.60),
    (678, "round_ingame", 0.90),
    (688, "round_end_lose", 0.90),
    (709, "side_attacker", 0.60),
    (958, "round_ingame_bomb_planted", 0.90),
    (968, "game_end_win", 0.90),
]

# 阵营购买窗口（1fps 秒索引，含端点）→ 真实阵营：2=守方 1=攻方。
# 人工依据：副标题「守方：歼灭敌军」一直出现到第 6 回合（上半场），第 7 回合起翻成
# 「攻方：安放炸弹」——即 7:4 这局的攻守互换点。
SIDE_WINDOWS = [
    (87, 127, 2), (197, 225, 2), (324, 345, 2), (392, 421, 2), (497, 525, 2),
    (698, 738, 1), (821, 821, 1), (834, 850, 1),
]


def load_rgb(path):
    return np.asarray(Image.open(path).convert("RGB"))


def template_paths(prefix):
    return sorted(glob.glob(os.path.join(TEMPLATES, f"{prefix}*.krt")))


def best_score(prefix, gray):
    best, best_file = -1.0, None
    for path in template_paths(prefix):
        s = score_detailed(Template(path), gray)[0]
        if s > best:
            best, best_file = s, os.path.basename(path)
    return best, best_file


def main():
    frames_dir = sys.argv[1] if len(sys.argv) > 1 else r"E:/tmp/kanami_2209/frames"
    out_dir = sys.argv[2] if len(sys.argv) > 2 else DATA
    os.makedirs(out_dir, exist_ok=True)

    files = sorted(glob.glob(os.path.join(frames_dir, "*.png")))
    if not files:
        print(f"no frames under {frames_dir}")
        return 1

    print("[1] 代表帧（源裁剪 + 命中分）")
    seconds, names, mins, crops = [], [], [], []
    for second, name, min_score in REPRESENTATIVE:
        path = os.path.join(frames_dir, f"f{second:04d}.png")
        if not os.path.exists(path):
            print(f"  缺少帧 {path}")
            return 1
        rgb = load_rgb(path).astype(np.float64)
        sub = rgb[CROP_Y:CROP_Y + CROP_H, CROP_X:CROP_X + CROP_W]
        # 存成管线同一套灰度（灰度是 RGB 的线性组合、双线性缩放也是线性的，
        # 所以"先转灰度再缩放"与真实管线"先缩放再转灰度"结果一致，只差一次取整）。
        crops.append(np.clip(np.rint(
            (29 * sub[:, :, 2] + 150 * sub[:, :, 1] + 77 * sub[:, :, 0]) / 256.0),
            0, 255).astype(np.uint8))
        seconds.append(second)
        names.append(name)
        mins.append(min_score)
        s, f = best_score(name, normalize_frame(rgb, top_align=True)[0])
        flag = "✓" if s >= min_score else "✗"
        print(f"  {flag} f{second:04d} {name:30} {s:.3f}（{f}，要求 ≥{min_score}）")

    np.savez_compressed(
        os.path.join(out_dir, "frames_22x9.npz"),
        gray=np.stack(crops), second=np.asarray(seconds, dtype=np.int32),
        names=np.asarray(names), min_score=np.asarray(mins, dtype=np.float64),
        src_w=SRC_W, src_h=SRC_H, crop_x=CROP_X, crop_y=CROP_Y)
    print(f"  -> frames_22x9.npz：{len(crops)} 帧")

    print("[2] 阵营副标题全时间线分数")
    attacker, defender = [], []
    for path in files:
        gray = normalize_frame(load_rgb(path), top_align=True)[0]
        attacker.append(best_score("side_attacker", gray)[0])
        defender.append(best_score("side_defender", gray)[0])
    np.savez_compressed(
        os.path.join(out_dir, "side_timeline_22x9.npz"),
        attacker=np.asarray(attacker, dtype=np.float32),
        defender=np.asarray(defender, dtype=np.float32),
        windows=np.asarray(SIDE_WINDOWS, dtype=np.int32))
    print(f"  -> side_timeline_22x9.npz：{len(files)} 帧、{len(SIDE_WINDOWS)} 个购买窗口")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
