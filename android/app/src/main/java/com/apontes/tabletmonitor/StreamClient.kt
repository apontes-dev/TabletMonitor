package com.apontes.tabletmonitor

import android.content.Context
import android.media.MediaCodec
import android.net.wifi.WifiManager
import android.os.Build
import android.util.Log
import android.view.Surface
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.Closeable
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.Executors

/**
 * Laço de conexão. Ordem de tentativa:
 *   1. USB — 127.0.0.1, que o "adb reverse" do PC encaminha pelo cabo (menor atraso);
 *   2. Wi-Fi — IP fixo das configurações e PCs que se anunciaram na rede;
 *   3. Bluetooth — PCs pareados que publicam o serviço TabletMonitor (banda menor, perfil leve).
 * Fica tentando enquanto estiver ativo.
 */
class StreamClient(
    context: Context,
    private val discovery: Discovery,
    private val prefs: Prefs,
    private val listener: Listener,
) {
    enum class Transport(val label: String) { USB("USB"), WIFI("Wi-Fi"), BLUETOOTH("Bluetooth") }

    interface Listener {
        fun onStatus(text: String)
        fun onConnected(peer: String, transport: Transport)
        fun onVideoConfig(width: Int, height: Int)
        fun onDisconnected()
    }

    /** maxW/maxH: maior vídeo que o decodificador aguenta na proporção da tela. */
    data class ScreenInfo(val width: Int, val height: Int, val dpi: Int, val maxW: Int, val maxH: Int)

    private val appContext = context.applicationContext
    private val bluetooth = BluetoothLink(appContext)
    private val wifi = appContext.getSystemService(Context.WIFI_SERVICE) as WifiManager

    @Volatile private var running = false
    private var thread: Thread? = null
    @Volatile private var connection: Closeable? = null
    @Volatile private var output: DataOutputStream? = null
    private val sender = Executors.newSingleThreadExecutor { r -> Thread(r, "touch-sender").apply { priority = Thread.MAX_PRIORITY } }

    private var surface: Surface? = null
    @Volatile private var screen: ScreenInfo? = null

    val isConnected: Boolean get() = output != null
    val isRunning: Boolean get() = running
    fun pairedComputers(): List<String> = bluetooth.pairedComputers().map { bluetooth.displayName(it) }

    fun start(surface: Surface, screen: ScreenInfo) {
        stop()
        this.surface = surface
        this.screen = screen
        running = true
        thread = Thread({ loop() }, "stream-client").apply { priority = Thread.MAX_PRIORITY; start() }
    }

    fun stop() {
        running = false
        closeConnection()
        thread?.let { try { it.join(1500) } catch (_: InterruptedException) {} }
        thread = null
    }

    /** Fecha a conexão atual para que seja renegociada (ex.: mudou a orientação). */
    fun reconnect(screen: ScreenInfo) {
        this.screen = screen
        closeConnection()
    }

    private fun closeConnection() {
        try { connection?.close() } catch (_: Exception) {}
    }

    // ------------------------------------------------------------------ tentativas

    private fun loop() {
        var cycle = 0
        while (running) {
            cycle++
            var ran = tryTcp("127.0.0.1", Protocol.PORT, 400, Transport.USB)

            if (!ran && running && prefs.useWifi) {
                val hosts = LinkedHashMap<String, Int>()
                prefs.manualHost.takeIf { it.isNotBlank() }?.let { hosts[it.trim()] = Protocol.PORT }
                for (h in discovery.recentHosts()) hosts.putIfAbsent(h.address, h.port)
                for ((host, port) in hosts) {
                    if (!running) break
                    if (tryTcp(host, port, 1200, Transport.WIFI)) { ran = true; break }
                }
            }

            // Bluetooth é mais lento para descobrir que o PC não está lá: tenta a cada 3 ciclos.
            if (!ran && running && prefs.useBluetooth && cycle % 3 == 1) {
                for (dev in bluetooth.pairedComputers()) {
                    if (!running) break
                    listener.onStatus("Tentando Bluetooth: ${bluetooth.displayName(dev)}…")
                    val sock = bluetooth.connect(dev) ?: continue
                    ran = true
                    endSession(runSession(sock.inputStream, sock.outputStream, bluetooth.displayName(dev), Transport.BLUETOOTH, sock))
                    break
                }
            }

            if (!ran && running) {
                listener.onStatus("Procurando o PC" + ".".repeat(cycle % 4))
                sleep(1000)
            }
        }
    }

    private fun tryTcp(host: String, port: Int, timeoutMs: Int, transport: Transport): Boolean {
        val s = Socket()
        try {
            s.tcpNoDelay = true
            s.receiveBufferSize = 512 * 1024
            s.connect(InetSocketAddress(host, port), timeoutMs)
            s.soTimeout = 10_000
        } catch (e: IOException) {
            try { s.close() } catch (_: Exception) {}
            return false
        }
        val lock = if (transport == Transport.WIFI) acquireWifiLock() else null
        try {
            endSession(runSession(s.getInputStream(), s.getOutputStream(), host, transport, s))
        } finally {
            try { lock?.release() } catch (_: Exception) {}
        }
        return true
    }

    /** Sem economia de energia no Wi-Fi durante a transmissão (evita engasgos de 100–300 ms). */
    @Suppress("DEPRECATION")
    private fun acquireWifiLock(): WifiManager.WifiLock? = try {
        val mode = if (Build.VERSION.SDK_INT >= 29) WifiManager.WIFI_MODE_FULL_LOW_LATENCY else WifiManager.WIFI_MODE_FULL_HIGH_PERF
        wifi.createWifiLock(mode, "tabletmonitor").apply { setReferenceCounted(false); acquire() }
    } catch (e: Exception) { null }

    private fun endSession(gotVideo: Boolean) {
        // PC respondeu mas não mandou vídeo (ex.: erro no ffmpeg): espera mais antes de tentar de novo.
        if (!gotVideo && running) {
            listener.onStatus("O PC não enviou vídeo — veja o log no app do Windows")
            sleep(3000)
        } else if (running) {
            sleep(300)
        }
    }

    // ------------------------------------------------------------------ sessão

    /** @return true se chegou a receber vídeo. */
    private fun runSession(rawIn: InputStream, rawOut: OutputStream, peer: String, transport: Transport, conn: Closeable): Boolean {
        connection = conn
        var decoder: VideoDecoder? = null
        var gotVideo = false
        try {
            val input = DataInputStream(BufferedInputStream(rawIn, 256 * 1024))
            val out = DataOutputStream(BufferedOutputStream(rawOut, 4096))

            val sc = screen ?: return false
            val name = (Build.MANUFACTURER + " " + Build.MODEL).trim().toByteArray(Charsets.UTF_8)
            out.write(Protocol.MAGIC)
            out.writeByte(Protocol.HELLO)
            out.writeInt(1 + 2 * 5 + name.size)
            out.writeByte(Protocol.VERSION)
            out.writeShort(sc.width)
            out.writeShort(sc.height)
            out.writeShort(sc.dpi)
            out.writeShort(sc.maxW)
            out.writeShort(sc.maxH)
            out.write(name)
            out.flush()
            output = out

            listener.onConnected(peer, transport)
            listener.onStatus("Conectado via ${transport.label}" + if (transport == Transport.USB) "" else " ($peer)")

            var buf = ByteArray(512 * 1024)
            while (running) {
                val type = input.readUnsignedByte()
                val len = input.readInt()
                if (len < 0 || len > Protocol.MAX_PACKET) throw IOException("pacote inválido ($len)")
                if (buf.size < len) buf = ByteArray(len + len / 2)
                input.readFully(buf, 0, len)

                when (type) {
                    Protocol.CONFIG -> {
                        gotVideo = true
                        val w = ((buf[0].toInt() and 0xFF) shl 8) or (buf[1].toInt() and 0xFF)
                        val h = ((buf[2].toInt() and 0xFF) shl 8) or (buf[3].toInt() and 0xFF)
                        decoder?.release()
                        listener.onVideoConfig(w, h)
                        val surf = surface ?: throw IOException("sem superfície")
                        decoder = VideoDecoder(surf, w, h)
                    }
                    Protocol.CSD -> decoder?.queue(buf, 0, len, 0, MediaCodec.BUFFER_FLAG_CODEC_CONFIG)
                    Protocol.FRAME -> if (len > 9) {
                        val key = (buf[0].toInt() and 1) != 0
                        var pts = 0L
                        for (i in 1..8) pts = (pts shl 8) or (buf[i].toLong() and 0xFF)
                        decoder?.queue(buf, 9, len - 9, pts, if (key) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
                    }
                    Protocol.PONG -> { /* reservado para medir latência */ }
                    else -> { /* tipo desconhecido: ignora (compatibilidade futura) */ }
                }
            }
        } catch (e: Exception) {
            Log.i(TAG, "sessão ${transport.label} encerrada: $e")
        } finally {
            output = null
            try { conn.close() } catch (_: Exception) {}
            connection = null
            decoder?.release()
            listener.onDisconnected()
        }
        return gotVideo
    }

    /** Envia um evento de toque; coordenadas normalizadas 0..1 em relação à imagem. */
    fun sendTouch(action: Int, pointerId: Int, x: Float, y: Float) {
        val out = output ?: return
        val nx = (x.coerceIn(0f, 1f) * 65535f).toInt()
        val ny = (y.coerceIn(0f, 1f) * 65535f).toInt()
        sender.execute {
            try {
                synchronized(out) {
                    out.writeByte(Protocol.TOUCH)
                    out.writeInt(6)
                    out.writeByte(action)
                    out.writeByte(pointerId and 0xFF)
                    out.writeShort(nx)
                    out.writeShort(ny)
                    out.flush()
                }
            } catch (_: IOException) {
                closeConnection()
            }
        }
    }

    private fun sleep(ms: Long) {
        try { Thread.sleep(ms) } catch (_: InterruptedException) {}
    }

    companion object { private const val TAG = "StreamClient" }
}
