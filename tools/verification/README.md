# 模板裁剪与验证脚本（离线）

这组 Python 脚本复刻了 `KanamiReporter.Core` 的识别管线（归一化 + ZNCC），
用于从手机录屏裁剪 `.mobile.krt` 模板并做全程验证。

- `kanami_core.py`：KRT 解析、帧归一化（宽度等比 + 顶对齐）、ZNCC（13 偏移）
- `cut_mobile.py` / `cut_more.py`：从指定时间点的抽帧裁剪模板（自动亮字包围盒）
- `scan_final.py`：对录屏 1fps 全程扫描，输出状态时间线（`timeline_final.txt`）
- 依赖：numpy、Pillow；录屏帧用 ffmpeg 抽取（crop=380:300:1200:0 / 全帧 PNG）

录屏：`Screenrecorder-2026-10-05-16-07-56-964.mp4`（2772×1280@60fps，24min）。
脚本内的 WORK 路径为当时临时目录，复用时改为本机路径即可。

## data/ —— 实机参考素材（2026-10-06 抢救）

原录屏 mp4 已不存在，以下是从当时的临时目录 `%TEMP%/kanami_work` 抢救出来的**唯一实机证据**，
全部来自同一台手机（2772×1280，19.5:9）。请勿删除。

- `score_plates.npz`：1456 帧（1fps，全程 24min）里左右两个比分板的归一化灰度区域，
  形状 `left/right = [N, 48, 44]`，`t = [N]` 为秒。切比分数字模板与逐帧验证都基于它，
  所以不必保留整帧（整目录数百 MB，这个只有 3.7MB）。
- `score_plates_sheet.png`：每 40 帧抽一张的联系表（上=左板，下=右板），人工核对比分用。
- `frames/t80 t115 t480 t515 t1447.png`：原始整帧（购买阶段 / 开局 / 对局中 1:2 / 炸弹已安装 / 胜利结算）。
- `zoom/raw_t*.png`：原帧 (1050,10) 起 680×300 区域的 2 倍放大图，看比分与计时数字最清楚。
- `topscan/s112..s116.png`：`round_ingame.d112..d116` 五个秒值模板的切割来源帧。
- `topscan/f_1443.png`：终局 7:4 + 「时间耗尽 / 回合获胜」横幅。
- `gaps/g224 g756 g760 g868.png`：`round_end_lose` / `side_defender` / `side_attacker` / `round_end_win` 的切割来源。
- `sheets/*.png`：当时的核对图（模板联系表、数字检查、胜利/选人检查、PC 模板对照）。

`extract_score_plates.py` 负责生成 `score_plates.npz`（`python extract_score_plates.py [topscan 目录] [输出路径]`），
需要重跑时把临时目录里的 `topscan/` 指给它即可。
