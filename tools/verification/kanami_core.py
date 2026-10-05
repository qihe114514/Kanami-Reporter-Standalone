"""Kanami 模板匹配验证：复刻 KanamiReporter.Core 的 KRT 加载与 ZNCC 匹配。"""
import os
import struct
import numpy as np
from PIL import Image

FRAME_W, FRAME_H = 1920, 1080
MATCH_OFFSETS = [
    (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1),
    (-0.5, 0), (0.5, 0), (0, -0.5), (0, 0.5),
    (-0.5, -0.5), (-0.5, 0.5), (0.5, -0.5), (0.5, 0.5),
]


class Template:
    def __init__(self, path):
        self.name = os.path.basename(path)[:-4]
        with open(path, 'rb') as f:
            header = f.read(32)
            magic, ver, w, h, x, y, rw, rh = struct.unpack('<8I', header)
            assert magic == 0x3154524B and ver == 1 and w == FRAME_W and h == FRAME_H, self.name
            self.roi = (x, y, rw, rh)
            n = rw * rh
            px = np.frombuffer(f.read(n), dtype=np.uint8).astype(np.float64)
        self.pixels = px.reshape(rh, rw)
        self.mean = px.mean()
        self.energy = (px * px).sum() - px.sum() ** 2 / n
        assert self.energy / n >= 16.0, f'{self.name}: too flat'


def normalize_frame(img_rgb, top_align=False):
    """RGB uint8 (H,W,3) -> 1920x1080 灰度帧。top_align=False 与 PC 端一致（垂直居中），
    True 为手机模式（顶对齐，适配宽度等比+顶部锚定的手机 HUD）。"""
    sh, sw = img_rgb.shape[:2]
    scale = FRAME_W / sw
    out_w = FRAME_W
    out_h = int(round(sh * scale))
    # 双线性缩放（与 C# 像素中心对齐公式一致）
    src_x = np.clip((np.arange(out_w) + 0.5) / scale - 0.5, 0, sw - 1)
    src_y = np.clip((np.arange(out_h) + 0.5) / scale - 0.5, 0, sh - 1)
    x0 = np.floor(src_x).astype(int); x1 = np.minimum(x0 + 1, sw - 1); fx = src_x - x0
    y0 = np.floor(src_y).astype(int); y1 = np.minimum(y0 + 1, sh - 1); fy = src_y - y0
    r = img_rgb[:, :, 0].astype(np.float64)
    g = img_rgb[:, :, 1].astype(np.float64)
    b = img_rgb[:, :, 2].astype(np.float64)
    top = r[np.ix_(y0, x0)] * (1 - fx) + r[np.ix_(y0, x1)] * fx
    bot = r[np.ix_(y1, x0)] * (1 - fx) + r[np.ix_(y1, x1)] * fx
    rr = top * (1 - fy[:, None]) + bot * fy[:, None]
    top = g[np.ix_(y0, x0)] * (1 - fx) + g[np.ix_(y0, x1)] * fx
    bot = g[np.ix_(y1, x0)] * (1 - fx) + g[np.ix_(y1, x1)] * fx
    gg = top * (1 - fy[:, None]) + bot * fy[:, None]
    top = b[np.ix_(y0, x0)] * (1 - fx) + b[np.ix_(y0, x1)] * fx
    bot = b[np.ix_(y1, x0)] * (1 - fx) + b[np.ix_(y1, x1)] * fx
    bb = top * (1 - fy[:, None]) + bot * fy[:, None]
    # FrameProcessing.ToGrayscale: gray=(29*B+150*G+77*R)>>8（BGRA 顺序）
    gray_small = (29 * bb + 150 * gg + 77 * rr) / 256.0
    gray = np.zeros((FRAME_H, FRAME_W), dtype=np.float64)
    off_y = 0 if top_align else (FRAME_H - out_h) // 2
    gray[off_y:off_y + out_h, :] = gray_small
    return gray, off_y, out_h


def normalize_region(img_rgb, x0n, x1n, y0n, y1n, top_align=True, origin=(0, 0), full_size=None):
    """只归一化需要的水平/竖直范围（归一化坐标系），供快速扫描。
    origin：img_rgb 在原始整帧中的裁剪原点（原始像素坐标）。
    full_size：原始整帧尺寸 (W,H)；None 表示 img_rgb 本身就是整帧。返回 (gray_crop, off_y)。"""
    sh, sw = img_rgb.shape[:2]
    fw, fh = full_size if full_size else (sw, sh)
    scale = FRAME_W / fw
    out_h = int(round(fh * scale))
    off_y = 0 if top_align else (FRAME_H - out_h) // 2
    # 归一化坐标 -> 源裁剪内坐标
    sx0 = max(0, int(np.floor(x0n / scale - 0.5)) - origin[0])
    sx1 = min(sw, int(np.ceil(x1n / scale + 1.5)) - origin[0])
    yn0 = y0n - off_y; yn1 = y1n - off_y
    sy0 = max(0, int(np.floor(yn0 / scale - 0.5)) - origin[1])
    sy1 = min(sh, int(np.ceil(yn1 / scale + 1.5)) - origin[1])
    sub = img_rgb[sy0:sy1, sx0:sx1].astype(np.float64)
    ssh, ssw = sub.shape[:2]
    ow = x1n - x0n; oh = y1n - y0n
    src_x = (np.arange(ow) + x0n + 0.5) / scale - 0.5 - origin[0] - sx0
    src_y = (np.arange(oh) + yn0 + 0.5) / scale - 0.5 - origin[1] - sy0
    x0 = np.clip(np.floor(src_x).astype(int), 0, ssw - 1); x1 = np.minimum(x0 + 1, ssw - 1); fx = np.clip(src_x - x0, 0, 1)
    y0 = np.clip(np.floor(src_y).astype(int), 0, ssh - 1); y1 = np.minimum(y0 + 1, ssh - 1); fy = np.clip(src_y - y0, 0, 1)
    r, g, b = sub[:, :, 0], sub[:, :, 1], sub[:, :, 2]
    def ch(c):
        top = c[np.ix_(y0, x0)] * (1 - fx) + c[np.ix_(y0, x1)] * fx
        bot = c[np.ix_(y1, x0)] * (1 - fx) + c[np.ix_(y1, x1)] * fx
        return top * (1 - fy[:, None]) + bot * fy[:, None]
    rr, gg, bb = ch(r), ch(g), ch(b)
    gray_crop = (29 * bb + 150 * gg + 77 * rr) / 256.0
    return gray_crop, off_y


def sample_bilinear(gray, x, y):
    x0 = int(np.floor(x)); y0 = int(np.floor(y))
    fx = x - x0; fy = y - y0
    x1 = min(x0 + 1, FRAME_W - 1); y1 = min(y0 + 1, FRAME_H - 1)
    top = gray[y0, x0] * (1 - fx) + gray[y0, x1] * fx
    bot = gray[y1, x0] * (1 - fx) + gray[y1, x1] * fx
    return top * (1 - fy) + bot * fy


def score_detailed(model, gray):
    x, y, rw, rh = model.roi
    n = rw * rh
    best, best_off = -1.0, (0, 0)
    for ox, oy in MATCH_OFFSETS:
        xs, ys = x + ox, y + oy
        if xs < 0 or ys < 0 or xs + rw > FRAME_W or ys + rh > FRAME_H:
            continue
        is_int = ox == int(ox) and oy == int(oy)
        if is_int:
            win = gray[int(ys):int(ys) + rh, int(xs):int(xs) + rw]
        else:
            xi = np.arange(xs, xs + rw)          # 已含小数偏移
            yi = np.arange(ys, ys + rh)
            xg, yg = np.meshgrid(xi, yi)
            win = _bil_grid(gray, xg, yg)
        s = win.sum()
        sq = (win * win).sum()
        prod = (win * (model.pixels - model.mean)).sum()
        energy = sq - s * s / n
        if energy < 1.0:
            continue
        score = prod / np.sqrt(energy * model.energy)
        if score > best:
            best, best_off = score, (ox, oy)
    return best, best_off


def _bil_grid(gray, xg, yg):
    x0 = np.floor(xg).astype(int); y0 = np.floor(yg).astype(int)
    fx = xg - x0; fy = yg - y0
    x1 = np.minimum(x0 + 1, FRAME_W - 1); y1 = np.minimum(y0 + 1, FRAME_H - 1)
    x0 = np.clip(x0, 0, FRAME_W - 1); y0 = np.clip(y0, 0, FRAME_H - 1)
    rows = np.arange(xg.shape[0])[:, None]
    cols = np.arange(xg.shape[1])[None, :]
    top = gray[y0, x0] * (1 - fx) + gray[y0, x1] * fx
    bot = gray[y1, x0] * (1 - fx) + gray[y1, x1] * fx
    return top * (1 - fy) + bot * fy


def load_templates(dirname):
    return [Template(os.path.join(dirname, f))
            for f in sorted(os.listdir(dirname)) if f.endswith('.krt')]


def gray_from_png(path):
    img = np.asarray(Image.open(path).convert('RGB'))
    gray, off_y, out_h = normalize_frame(img)
    return gray, img, off_y, out_h
