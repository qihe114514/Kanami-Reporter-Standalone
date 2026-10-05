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

    /** 阵营辅助模板的判定阈值余量（与 PC 端 RecognitionEngine 一致）。 */
    const val SideThresholdMargin = 0.05
    const val SideThresholdFloor = 0.80

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

/** KRT v1 模板：32 字节头 + ROI + 灰度像素。ROI 为 1920×1080 顶对齐归一化坐标。 */
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
 * 帧归一化（手机模式）：任意采集帧按宽度缩放到 1920，顶对齐贴入 1080 高的画布。
 *
 * 手机端 HUD 按宽度等比渲染并锚定屏幕顶部，顶对齐保证不同全面屏比例下
 * HUD 落在同一组归一化坐标上（详见 docs/mobile-adaptation.md 第 2 节）。
 *
 * 灰度公式与 PC 端 FrameProcessing.ToGrayscale 完全一致：
 * gray = (29*B + 150*G + 77*R) >> 8（输入 RGBA）。
 */
object FrameProcessing {
    /** 比分数字二值化阈值（实机帧统计得到：数字为亮白 200+，底板/背景远低于此）。 */
    const val BinaryThreshold = 165

    class NormalizedFrame(
        var gray: ByteArray,
        var contentHeight: Int
    )

    private val scale = ReporterStates.FrameWidth.toDouble()

    fun normalize(rgba: ByteArray, srcWidth: Int, srcHeight: Int, out: NormalizedFrame): NormalizedFrame {
        val outW = ReporterStates.FrameWidth
        val outH = Math.round(srcHeight * scale / srcWidth).toInt().coerceAtMost(ReporterStates.FrameHeight)
        if (out.gray.size < outW * ReporterStates.FrameHeight) {
            out.gray = ByteArray(outW * ReporterStates.FrameHeight)
        }
        out.gray.fill(0)

        val stepX = srcWidth.toDouble() / outW
        var y = 0
        while (y < outH) {
            val sy = ((y + 0.5) * stepX - 0.5).coerceIn(0.0, (srcHeight - 1).toDouble())
            val y0f = Math.floor(sy)
            val y0 = y0f.toInt().coerceIn(0, srcHeight - 1)
            val y1 = (y0 + 1).coerceAtMost(srcHeight - 1)
            val fy = sy - y0f
            val row0 = y0 * srcWidth
            val row1 = y1 * srcWidth
            val outRow = y * outW
            var x = 0
            var srcX = 0.5 * stepX - 0.5
            while (x < outW) {
                val xf = Math.floor(srcX)
                val xs = xf.toInt().coerceIn(0, srcWidth - 1)
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
                srcX += stepX
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
