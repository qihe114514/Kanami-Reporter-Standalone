# 模板裁剪与验证脚本（离线）

这组 Python 脚本复刻了 `KanamiReporter.Core` 的识别管线（归一化 + ZNCC），
用于从手机录屏裁剪 `.mobile.krt` 模板并做全程验证。

- `kanami_core.py`：KRT 解析、帧归一化（**按屏幕高度等比、水平居中、顶对齐**）、ZNCC（13 偏移）
- `cut_mobile.py` / `cut_more.py`：从指定时间点的抽帧裁剪模板（自动亮字包围盒）
- `scan_final.py`：对录屏 1fps 全程扫描，输出状态时间线（`timeline_final.txt`）
- `extract_score_plates.py`：从 1fps 抽帧里抽出左右比分板区域，压成 `data/score_plates.npz`
- `cut_score.py`：切比分数字模板（二值形状相关 + 多实例）并复算准确率
- `extract_second_device.py`：从**第二台机型**的 1fps 抽帧生成 `data/frames_22x9.npz`
  与 `data/side_timeline_22x9.npz`（见下）
- **`verify_shipped.py`：参考机（2772×1280 / 19.5:9）的离线回归**：
  1. 每个 `.krt` 都过一遍 Kotlin 加载器的校验（魔数/版本/尺寸/ROI/平坦度）与命名分组；
  2. 状态模板在来源帧上灰度 ZNCC ≥0.95（购买横幅/阵营副标题/开局计时/炸弹面板/胜利横幅）；
  3. 比分数字在 12 个人工核对点上二值读数 100% 正确。
- **`verify_second_device.py`：第二台机型（2376×1080 / 22:9）的离线回归**：
  1. 归一化不变量是**内容高度**（合成两张只有宽高比不同的画面，同一元素必须落在同一坐标）；
  2. 代表帧全部命中，并断言**旧的"按宽度归一化"在宽文字横幅上只有 0.56~0.68**（防止改回去）；
  3. 阵营判定规则（阈值/领先幅度直接读 Kotlin 源码，避免测试与实现漂移）在整段 979 帧上与
     真实攻守轮次一致，窗口之外零误判；
  4. 比分二值读数与 8 个人工核对点一致。
- 两个 verify 脚本全部通过退出码 0。**改了模板、或改了归一化/匹配算法，两个都要跑。**
- 依赖：numpy、Pillow；录屏帧用 ffmpeg 抽取（crop=380:300:1200:0 / 全帧 PNG）

跑法（本机 python 不在 PATH，用 codex runtime 的那个）：

```bash
PY="C:/Users/VOS-User/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe"
PYTHONIOENCODING=utf-8 "$PY" verify_shipped.py
PYTHONIOENCODING=utf-8 "$PY" verify_second_device.py
```

参考机录屏：`Screenrecorder-2026-10-05-16-07-56-964.mp4`（2772×1280@60fps，24min）。
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

## data/ —— 第二台机型的适配证据（2026-10-06）

第二台实机是 **2376×1080（22:9）**，宽高比与参考机不同 —— 正是它暴露出"按宽度归一化"的错误。
录屏 2.6GB 不入库，只留两类紧凑证据（由 `extract_second_device.py` 生成）：

- `frames_22x9.npz`：10 张代表帧的源分辨率灰度裁剪（源坐标 x 980..1420、y 0..624，
  覆盖全部模板 ROI 映射到的源区域）+ 各自应命中的模板名与最低分 + `second/names/min_score`。
  验证时贴回黑底整帧再走真实归一化管线，所以回归的是整条链路。1MB。
- `side_timeline_22x9.npz`：整段 979 帧（1fps）上阵营副标题两侧的 ZNCC 分数 `attacker/defender`，
  以及 8 个购买窗口的 `windows`（起、止、真实阵营：2=守 1=攻）。8KB。

重跑：`python extract_second_device.py <1fps 帧目录>`（帧用
`ffmpeg -i <录屏> -vf fps=1 frames/f%04d.png` 抽，抽出来是 2376×1080）。
