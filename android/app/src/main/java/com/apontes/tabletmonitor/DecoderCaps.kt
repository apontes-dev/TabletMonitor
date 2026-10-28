package com.apontes.tabletmonitor

import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.util.Log

/**
 * Descobre o maior vídeo que o decodificador H.264 por hardware aguenta, mantendo a proporção da tela.
 * Ex.: SM-T520 tem tela 2560x1600, mas o decodificador do Exynos 5420 vai até ~1920x1080 —
 * então pedimos ao PC algo como 1728x1080 em vez de 2560x1600 (que travaria ou cairia no software).
 */
object DecoderCaps {
    private const val TAG = "DecoderCaps"

    data class Result(val width: Int, val height: Int, val decoder: String)

    fun bestSize(screenW: Int, screenH: Int, fps: Double = 30.0): Result {
        val caps = findDecoder() ?: return Result(screenW, screenH, "?").also { Log.w(TAG, "nenhum decodificador AVC") }
        val (name, video) = caps
        val aspect = screenW.toDouble() / screenH
        val upperW = video.supportedWidths.upper
        var w = minOf(screenW, upperW) and 0xFFFE
        while (w >= 320) {
            val h = (w / aspect).toInt() and 0xFFFE
            val ok = try {
                video.isSizeSupported(w, h) && video.areSizeAndRateSupported(w, h, fps)
            } catch (_: Exception) { false }
            if (ok) {
                Log.i(TAG, "$name: melhor tamanho ${w}x$h (tela ${screenW}x$screenH)")
                return Result(w, h, name)
            }
            w -= 16
        }
        return Result(minOf(screenW, 1280), minOf(screenH, 800), name)
    }

    /** Prefere decodificador por hardware; cai no de software se for o único. */
    private fun findDecoder(): Pair<String, MediaCodecInfo.VideoCapabilities>? {
        val list = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
            .filter { !it.isEncoder && it.supportedTypes.any { t -> t.equals(MediaFormat.MIMETYPE_VIDEO_AVC, true) } }
        val hw = list.firstOrNull { isHardware(it) } ?: list.firstOrNull() ?: return null
        val vc = hw.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC).videoCapabilities ?: return null
        return hw.name to vc
    }

    private fun isHardware(info: MediaCodecInfo): Boolean {
        if (Build.VERSION.SDK_INT >= 29) return info.isHardwareAccelerated
        val n = info.name.lowercase()
        return !(n.startsWith("omx.google.") || n.startsWith("c2.android.") || n.contains(".sw."))
    }
}
