package com.apontes.tabletmonitor

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.util.Log
import android.view.Surface

/**
 * Decodificador H.264 por hardware (MediaCodec) renderizando direto na Surface.
 * Entrada é alimentada pela thread de rede; saída é drenada numa thread própria
 * e cada quadro é renderizado assim que sai, sem fila (baixa latência).
 */
class VideoDecoder(surface: Surface, width: Int, height: Int) {

    private val codec: MediaCodec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
    @Volatile private var running = true
    private val outputThread: Thread

    @Volatile var framesRendered = 0L
        private set

    init {
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height)
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, maxOf(width * height * 2, 2 * 1024 * 1024))
        format.setInteger(MediaFormat.KEY_PRIORITY, 0) // tempo real
        if (Build.VERSION.SDK_INT >= 30) {
            format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
        }
        val name = codec.name.lowercase()
        // Chaves de baixa latência específicas de fabricante (ignoradas por outros decodificadores).
        if (name.contains("qcom") || name.contains("qti")) {
            format.setInteger("vendor.qti-ext-dec-picture-order.enable", 1)
            format.setInteger("vendor.qti-ext-dec-low-latency.enable", 1)
        } else if (name.contains("exynos")) {
            format.setInteger("vendor.rtc-ext-dec-low-latency.enable", 1)
        } else if (name.contains("hisi")) {
            format.setInteger("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-req", 1)
            format.setInteger("vendor.hisi-ext-low-latency-video-dec.video-scene-for-low-latency-rdy", -1)
        }
        Log.i(TAG, "Decoder ${codec.name} ${width}x$height")
        codec.configure(format, surface, null, 0)
        codec.start()

        outputThread = Thread({ drainLoop() }, "decoder-out").apply {
            priority = Thread.MAX_PRIORITY
            start()
        }
    }

    /** Envia um access unit (ou SPS/PPS com BUFFER_FLAG_CODEC_CONFIG). Descarta se o decodificador estiver cheio. */
    fun queue(data: ByteArray, offset: Int, length: Int, ptsUs: Long, flags: Int): Boolean {
        if (!running) return false
        return try {
            val index = codec.dequeueInputBuffer(100_000)
            if (index < 0) return false
            val buf = codec.getInputBuffer(index) ?: return false
            buf.clear()
            if (length > buf.capacity()) {
                Log.w(TAG, "Quadro de $length bytes maior que o buffer (${buf.capacity()})")
                codec.queueInputBuffer(index, 0, 0, ptsUs, 0)
                return false
            }
            buf.put(data, offset, length)
            codec.queueInputBuffer(index, 0, length, ptsUs, flags)
            true
        } catch (e: IllegalStateException) {
            Log.w(TAG, "queue: $e")
            false
        }
    }

    private fun drainLoop() {
        val info = MediaCodec.BufferInfo()
        while (running) {
            try {
                val index = codec.dequeueOutputBuffer(info, 20_000)
                if (index >= 0) {
                    codec.releaseOutputBuffer(index, true)
                    framesRendered++
                }
            } catch (e: IllegalStateException) {
                if (running) Log.w(TAG, "drain: $e")
                break
            }
        }
    }

    fun release() {
        running = false
        try { outputThread.join(300) } catch (_: InterruptedException) {}
        try { codec.stop() } catch (_: Exception) {}
        try { codec.release() } catch (_: Exception) {}
    }

    companion object { private const val TAG = "VideoDecoder" }
}
