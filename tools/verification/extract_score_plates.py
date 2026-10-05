"""从 1fps topscan 帧里抽出左右两个比分板的归一化灰度区域，压成一个 npz 长期保存。

背景：原始录屏 mp4 已不存在，`topscan/f_*.png`（380x300，原帧原点 (1200,0)）是唯一的
实机证据。整帧/整目录太大（数百 MB），而切比分数字模板 + 验证只需要比分板那一小块，
所以这里把 1456 帧的比分板区域抽成一个几 MB 的 npz。

用法：
    python extract_score_plates.py [topscan 目录] [输出 npz 路径]
默认读 `%TEMP%/kanami_work/topscan`，写到 `./data/score_plates.npz`，
并在同目录输出 `score_plates_sheet.png` 供人工核对。
"""
import glob
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from kanami_core import normalize_region  # noqa: E402

ORIGIN = (1200, 0)          # topscan 裁剪原点（原始 2772x1280 帧坐标）
FULL_SIZE = (2772, 1280)    # 原始帧尺寸
# 归一化(1920x1080)坐标下的两个比分板窗口（含少量余量，切模板时再按亮度收 bbox）
LEFT_WINDOW = (872, 16, 916, 64)
RIGHT_WINDOW = (1006, 16, 1050, 64)
THR = 150                   # 白色数字的亮度阈值


def extract_window(img, win):
    x0, y0, x1, y1 = win
    crop, _ = normalize_region(img, x0, x1, y0, y1, origin=ORIGIN, full_size=FULL_SIZE)
    return np.clip(np.rint(crop), 0, 255).astype(np.uint8)


def main():
    top_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.environ.get("TEMP", ""), "kanami_work", "topscan")
    out_path = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "data", "score_plates.npz")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)

    files = sorted(glob.glob(os.path.join(top_dir, "f_*.png")))
    if not files:
        print(f"no frames found under {top_dir}")
        return 1

    ts, lefts, rights = [], [], []
    for path in files:
        img = np.asarray(Image.open(path).convert("RGB"))
        ts.append(int(os.path.basename(path)[2:6]))
        lefts.append(extract_window(img, LEFT_WINDOW))
        rights.append(extract_window(img, RIGHT_WINDOW))

    np.savez_compressed(
        out_path,
        t=np.asarray(ts, dtype=np.int32),
        left=np.stack(lefts),
        right=np.stack(rights),
        left_window=np.asarray(LEFT_WINDOW, dtype=np.int32),
        right_window=np.asarray(RIGHT_WINDOW, dtype=np.int32),
    )
    size_mb = os.path.getsize(out_path) / 1048576
    print(f"{len(ts)} frames -> {out_path} ({size_mb:.2f} MB)")

    # 人工核对用联系表：每 40 帧抽一张，左右板并排
    picks = list(range(0, len(ts), 40))
    tile_h, tile_w = lefts[0].shape[0] * 2, lefts[0].shape[1]
    cols = 8
    rows = (len(picks) + cols - 1) // cols
    sheet = np.zeros((rows * (tile_h + 4), cols * (tile_w + 4)), dtype=np.uint8)
    for i, idx in enumerate(picks):
        r, c = divmod(i, cols)
        y = r * (tile_h + 4)
        x = c * (tile_w + 4)
        sheet[y:y + lefts[idx].shape[0], x:x + tile_w] = lefts[idx]
        sheet[y + lefts[idx].shape[0]:y + tile_h, x:x + tile_w] = rights[idx]
    sheet_path = os.path.join(os.path.dirname(out_path), "score_plates_sheet.png")
    Image.fromarray(sheet).save(sheet_path)
    print(f"contact sheet -> {sheet_path} ({[ts[i] for i in picks]})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
