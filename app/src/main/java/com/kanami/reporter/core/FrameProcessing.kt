package com.kanami.reporter.core

import java.io.IOException
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** 识别基准尺寸（与 PC 端一致：模板 ROI 都定义在这套坐标系里）。 */
object ReporterStates {
    const val Count = 15
    const val FrameWidth = 1920
    const val FrameHeight = 1080
    const val DefaultThreshold = 0.90

    /**
     * 阵营副标题（购买横幅的「攻方：安放炸弹 / 守方：歼灭敌军」）模板的命中下限。
     *
     * 这两个模板的 ROI 里混着半透明横幅的背景，背景透出场景，分数会随场景亮度整体漂移
     * （同一台机器上亮天光场景 1.000、暗场景 0.78），所以不能沿用状态模板那套 0.90 阈值。
     * 实测（2026-10-06 录屏 979 帧全量扫描）：横幅不可见时两侧最高 0.44，可见时正确一侧
     * 0.69~0.80、错误一侧 ≤0.54，分离度很清楚。这里只设一个宽松下限，胜负交给
     * [ReporterStateMachine.SideScoreMargin] 做相对比较。
     */
    const val SideTemplateFloor = 0.60

    const val AttackerSideTemplateName = "side_attacker"
    const val DefenderSideTemplateName = "side_defender"

    /** 比分数字模板前缀：`score_us.d0..d7`（我方/左侧板）与 `score_enemy.d0..d7`（对方/右侧板）。 */
    const val ScoreUsTemplatePrefix = "score_us"
    const val ScoreEnemyTemplatePrefix = "score_enemy"

    val Names = arrayOf(
        "game_choose_character",
        "game_start_attacker",
        "game_start_defender",
        "game_switch_side",
        "round_start",
        "round_ingame",
        "round_ingame_bomb_planted",
        "round_end_win",
        "round_end_lose",
        "round_win_five_kill",
        "round_win_all_alive",
        "round_win_alive_1",
        "game_end_win",
        "game_end_lose",
        "game_end_draw"
    )

    val DisplayNames = arrayOf(
        "选择角色", "进攻方开局", "防守方开局", "攻守互换", "回合开始",
        "回合进行中", "炸弹已安装", "回合胜利", "回合失败", "五杀胜利",
        "全员存活胜利", "最后一人胜利", "对局胜利", "对局失败", "对局平局"
    )
}

enum class StateId(val id: Int) {
    GameChooseCharacter(0),
    GameStartAttacker(1),
    GameStartDefender(2),
    GameSwitchSide(3),
    RoundStart(4),
    RoundIngame(5),
    RoundIngameBombPlanted(6),
    RoundEndWin(7),
    RoundEndLose(8),
    RoundWinFiveKill(9),
    RoundWinAllAlive(10),
    RoundWinAlive1(11),
    GameEndWin(12),
    GameEndLose(13),
    GameEndDraw(14);

    companion object {
        fun from(index: Int): StateId? = entries.firstOrNull { it.id == index }
    }
}

/** KRT v1 模板：32 字节头 + ROI + 灰度像素。ROI 为 1920×1080 归一化画布坐标（水平居中、顶对齐）。 */
class TemplateModel(
    val name: String,
    val roiX: Int,
    val roiY: Int,
    val roiWidth: Int,
    val roiHeight: Int,
    val pixels: ByteArray,
    val mean: Double,
    val energy: Double
) {
    val pixelCount: Int get() = roiWidth * roiHeight

    /**
     * 二值化版本（阈值 [FrameProcessing.BinaryThreshold]）。
     *
     * 比分数字这类模板用灰度 ZNCC 分辨率不够：实测同一数字跨帧 0.97，而 0 与 3/6 之间
     * 也能到 0.77~0.80，会互相混。二值形状相关把"数字形状"和"半透明底板+背景"分开，
     * 异数字降到 0.54~0.86、同数字仍 0.94~0.98。
     */
    val binaryPixels: DoubleArray = DoubleArray(pixelCount) { i ->
        if ((pixels[i].toInt() and 0xFF) > FrameProcessing.BinaryThreshold) 255.0 else 0.0
    }

    val binaryMean: Double = binaryPixels.average()

    val binaryEnergy: Double = binaryPixels.sumOf { d -> val v = d - binaryMean; v * v }
}

object KrtTemplateStore {
    private const val MAGIC = 0x3154524B
    private const val VERSION = 1

    fun isValidRoi(x: Int, y: Int, w: Int, h: Int): Boolean =
        x >= 0 && y >= 0 && w >= 4 && h >= 4 &&
            x + w <= ReporterStates.FrameWidth &&
            y + h <= ReporterStates.FrameHeight &&
            w.toLong() * h <= MaxTemplatePixels

    const val MaxTemplatePixels = 262144

    /** @throws IOException 格式不合法或区域过平。 */
    fun load(name: String, bytes: ByteArray): TemplateModel {
        if (bytes.size < 32) throw IOException("KRT 文件头不完整: $name")
        val header = ByteBuffer.wrap(bytes, 0, 32).order(ByteOrder.LITTLE_ENDIAN)
        val magic = header.int
        val version = header.int
        val width = header.int
        val height = header.int
        val x = header.int
        val y = header.int
        val w = header.int
        val h = header.int
        if (magic != MAGIC || version != VERSION ||
            width != ReporterStates.FrameWidth || height != ReporterStates.FrameHeight ||
            !isValidRoi(x, y, w, h)
        ) {
            throw IOException("KRT 文件格式或分辨率不兼容: $name")
        }
        val count = w * h
        if (bytes.size < 32 + count) throw IOException("KRT 像素数据不完整: $name")
        val pixels = bytes.copyOfRange(32, 32 + count)
        var sum = 0.0
        var squared = 0.0
        for (p in pixels) {
            val v = p.toInt() and 0xFF
            sum += v
            squared += (v * v).toDouble()
        }
        val mean = sum / count
        val energy = squared - sum * sum / count
        if (energy / count < 16.0) throw IOException("模板区域过于均匀，无法可靠匹配: $name")
        return TemplateModel(name, x, y, w, h, pixels, mean, energy)
    }
}

/**
 * 帧归一化（手机模式）：任意采集帧按**屏幕高度**等比缩放，水平居中、顶对齐贴入 1920×1080 画布。
 *
 * 实测依据（2026-10-06 第二台实机录屏 2376×1080 / 22:9，与参考机 2772×1280 / 19.5:9 同一游戏对比）：
 * 游戏 HUD 的屏幕坐标只随屏幕**高度**等比变化，与宽度、宽高比无关 —— 同一个元素在两台机器上的
 * 屏幕 y 之比恰为 1080/1280，屏幕 x 到屏幕中心的距离之比也是 1080/1280。因此归一化的不变量
 * 必须是内容高度而不是内容宽度。旧实现按宽度缩放到 1920，宽高比一变 HUD 就在归一化坐标里被
 * 纵向压扁（19.5:9 → 22:9 压 1.6%）：计时数字、炸弹面板这类小模板误差不到 1px，仍能命中；
 * 宽文字横幅整体掉到 0.09~0.70，阈值 0.90 下完全不命中 —— 购买阶段、回合获胜/战败、
 * 选人画面、对局结算全部失效。
 *
 * 内容高度取参考机 2772×1280 在"按宽度归一化"下的内容高度 1280×1920/2772 ≈ 886.58 → 887，
 * 于是参考机上的结果与旧实现逐像素一致，既有模板无需重切。水平按屏幕中心对齐（模板覆盖的
 * HUD 元素都是居中锚定的）；比参考机更宽或更窄的屏幕在两侧补黑边。
 *
 * 灰度公式与 PC 端 FrameProcessing.ToGrayscale 完全一致：
 * gray = (29*B + 150*G + 77*R) >> 8（输入 RGBA）。
 */
object FrameProcessing {
    /** 比分数字二值化阈值（实机帧统计得到：数字为亮白 200+，底板/背景远低于此）。 */
    const val BinaryThreshold = 165

    /** 归一化内容高度：与采集机宽高比无关的常量（1280×1920/2772 四舍五入）。 */
    const val ContentHeight = 887

    /** HUD 缩放基准（参考机内容高度，保留小数以免逐行累积偏移）。 */
    private const val ReferenceContentHeight = 1920.0 * 1280.0 / 2772.0

    class NormalizedFrame(
        var gray: ByteArray,
        var contentHeight: Int
    )

    fun normalize(rgba: ByteArray, srcWidth: Int, srcHeight: Int, out: NormalizedFrame): NormalizedFrame {
        val outW = ReporterStates.FrameWidth
        val outH = ContentHeight
        if (out.gray.size < outW * ReporterStates.FrameHeight) {
            out.gray = ByteArray(outW * ReporterStates.FrameHeight)
        }
        out.gray.fill(0)

        // 源像素 / 归一化像素：由屏幕高度决定，与宽度无关。
        val step = srcHeight.toDouble() / ReferenceContentHeight
        // 归一化坐标 → 源坐标：srcX = x * step + xBias（水平居中）、srcY = (y + 0.5) * step - 0.5。
        val xBias = (0.5 - outW * 0.5) * step + srcWidth * 0.5 - 0.5
        // 先把落在画面内的 x 范围算出来，内层循环就不用逐像素判边界了。
        val xFirst = Math.ceil(-xBias / step).toInt().coerceIn(0, outW)
        val xLast = Math.floor((srcWidth - 1.0 - xBias) / step).toInt().coerceIn(-1, outW - 1)
        var y = 0
        while (y < outH) {
            val sy = ((y + 0.5) * step - 0.5).coerceIn(0.0, (srcHeight - 1).toDouble())
            val y0f = Math.floor(sy)
            val y0 = y0f.toInt().coerceIn(0, srcHeight - 1)
            val y1 = (y0 + 1).coerceAtMost(srcHeight - 1)
            val fy = sy - y0f
            val row0 = y0 * srcWidth
            val row1 = y1 * srcWidth
            val outRow = y * outW
            var x = xFirst
            while (x <= xLast) {
                val srcX = x * step + xBias
                val xf = Math.floor(srcX)
                val xs = xf.toInt()
                val xs1 = (xs + 1).coerceAtMost(srcWidth - 1)
                val fx = srcX - xf
                val gx = 1.0 - fx
                val p00 = (row0 + xs) * 4
                val p10 = (row0 + xs1) * 4
                val p01 = (row1 + xs) * 4
                val p11 = (row1 + xs1) * 4
                val rTop = (rgba[p00 + 0].toInt() and 0xFF) * gx + (rgba[p10 + 0].toInt() and 0xFF) * fx
                val gTop = (rgba[p00 + 1].toInt() and 0xFF) * gx + (rgba[p10 + 1].toInt() and 0xFF) * fx
                val bTop = (rgba[p00 + 2].toInt() and 0xFF) * gx + (rgba[p10 + 2].toInt() and 0xFF) * fx
                val rBot = (rgba[p01 + 0].toInt() and 0xFF) * gx + (rgba[p11 + 0].toInt() and 0xFF) * fx
                val gBot = (rgba[p01 + 1].toInt() and 0xFF) * gx + (rgba[p11 + 1].toInt() and 0xFF) * fx
                val bBot = (rgba[p01 + 2].toInt() and 0xFF) * gx + (rgba[p11 + 2].toInt() and 0xFF) * fx
                val gy = 1.0 - fy
                val r = rTop * gy + rBot * fy
                val g = gTop * gy + gBot * fy
                val b = bTop * gy + bBot * fy
                out.gray[outRow + x] = (((29.0 * b + 150.0 * g + 77.0 * r) / 256.0).toInt().coerceIn(0, 255)).toByte()
                x++
            }
            y++
        }
        out.contentHeight = outH
        return out
    }

    /**
     * ZNCC 打分（与 PC 端 FrameProcessing.ScoreDetailed 一致）：
     * 固定 ROI + 13 个整数/半像素偏移取最高分。
     */
    fun score(model: TemplateModel, gray: ByteArray, offsets: Array<Pair<Double, Double>> = MatchOffsets): Double {
        val frameW = ReporterStates.FrameWidth
        val n = model.pixelCount
        var best = -1.0
        for ((ox, oy) in offsets) {
            val xs = model.roiX + ox
            val ys = model.roiY + oy
            if (xs < 0 || ys < 0 || xs + model.roiWidth > frameW || ys + model.roiHeight > ReporterStates.FrameHeight) continue
            val isInt = ox == Math.floor(ox) && oy == Math.floor(oy)
            var sum = 0.0
            var squared = 0.0
            var product = 0.0
            var index = 0
            if (isInt) {
                val x0 = xs.toInt()
                val y0 = ys.toInt()
                for (yy in 0 until model.roiHeight) {
                    var row = (y0 + yy) * frameW + x0
                    for (xx in 0 until model.roiWidth) {
                        val v = gray[row + xx].toInt() and 0xFF
                        sum += v
                        squared += (v * v).toDouble()
                        product += v * ((model.pixels[index].toInt() and 0xFF) - model.mean)
                        index++
                    }
                }
            } else {
                for (yy in 0 until model.roiHeight) {
                    for (xx in 0 until model.roiWidth) {
                        val v = sampleBilinear(gray, xs + xx, ys + yy)
                        sum += v
                        squared += v * v
                        product += v * ((model.pixels[index].toInt() and 0xFF) - model.mean)
                        index++
                    }
                }
            }
            val energy = squared - sum * sum / n
            if (energy < 1.0) continue
            val score = product / kotlin.math.sqrt(energy * model.energy)
            if (score > best) best = score
        }
        return best
    }

    /**
     * 二值形状 ZNCC（比分数字专用）：模板与窗口两侧都按 [BinaryThreshold] 二值化后再算相关。
     * 与 [score] 同样做 13 个整数/半像素偏移搜索，取最高分。
     */
    fun scoreBinary(model: TemplateModel, gray: ByteArray, offsets: Array<Pair<Double, Double>> = MatchOffsets): Double {
        val frameW = ReporterStates.FrameWidth
        val n = model.pixelCount
        if (model.binaryEnergy < 1.0) return -1.0
        var best = -1.0
        for ((ox, oy) in offsets) {
            val xs = model.roiX + ox
            val ys = model.roiY + oy
            if (xs < 0 || ys < 0 || xs + model.roiWidth > frameW ||
                ys + model.roiHeight > ReporterStates.FrameHeight
            ) {
                continue
            }
            val isInt = ox == Math.floor(ox) && oy == Math.floor(oy)
            var sum = 0.0
            var squared = 0.0
            var product = 0.0
            var index = 0
            if (isInt) {
                val x0 = xs.toInt()
                val y0 = ys.toInt()
                for (yy in 0 until model.roiHeight) {
                    var row = (y0 + yy) * frameW + x0
                    for (xx in 0 until model.roiWidth) {
                        val v = if ((gray[row + xx].toInt() and 0xFF) > BinaryThreshold) 255.0 else 0.0
                        sum += v
                        squared += v * v
                        product += v * (model.binaryPixels[index] - model.binaryMean)
                        index++
                    }
                }
            } else {
                for (yy in 0 until model.roiHeight) {
                    for (xx in 0 until model.roiWidth) {
                        val raw = sampleBilinear(gray, xs + xx, ys + yy)
                        val v = if (raw > BinaryThreshold) 255.0 else 0.0
                        sum += v
                        squared += v * v
                        product += v * (model.binaryPixels[index] - model.binaryMean)
                        index++
                    }
                }
            }
            val energy = squared - sum * sum / n
            if (energy < 1.0) continue
            val score = product / kotlin.math.sqrt(energy * model.binaryEnergy)
            if (score > best) best = score
        }
        return best
    }

    private fun sampleBilinear(gray: ByteArray, x: Double, y: Double): Double {
        val x0 = Math.floor(x).toInt().coerceIn(0, ReporterStates.FrameWidth - 1)
        val y0 = Math.floor(y).toInt().coerceIn(0, ReporterStates.FrameHeight - 1)
        val x1 = (x0 + 1).coerceAtMost(ReporterStates.FrameWidth - 1)
        val y1 = (y0 + 1).coerceAtMost(ReporterStates.FrameHeight - 1)
        val fx = x - Math.floor(x)
        val fy = y - Math.floor(y)
        val top = (gray[y0 * ReporterStates.FrameWidth + x0].toInt() and 0xFF) * (1 - fx) +
            (gray[y0 * ReporterStates.FrameWidth + x1].toInt() and 0xFF) * fx
        val bottom = (gray[y1 * ReporterStates.FrameWidth + x0].toInt() and 0xFF) * (1 - fx) +
            (gray[y1 * ReporterStates.FrameWidth + x1].toInt() and 0xFF) * fx
        return top * (1 - fy) + bottom * fy
    }

    val MatchOffsets: Array<Pair<Double, Double>> = arrayOf(
        (0.0 to 0.0), (-1.0 to 0.0), (1.0 to 0.0), (0.0 to -1.0), (0.0 to 1.0),
        (-0.5 to 0.0), (0.5 to 0.0), (0.0 to -0.5), (0.0 to 0.5),
        (-0.5 to -0.5), (-0.5 to 0.5), (0.5 to -0.5), (0.5 to 0.5)
    )
}
