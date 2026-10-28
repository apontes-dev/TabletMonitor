package com.apontes.tabletmonitor

import android.content.Context
import android.net.wifi.WifiManager
import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.util.concurrent.ConcurrentHashMap

/**
 * Escuta os anúncios UDP do app do Windows ("TMON1|nome|porta") para achar o PC na rede Wi-Fi
 * sem precisar digitar IP.
 */
class Discovery(context: Context) {

    data class Host(val address: String, val port: Int, val name: String, val lastSeen: Long)

    private val hosts = ConcurrentHashMap<String, Host>()
    private val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
    private var lock: WifiManager.MulticastLock? = null
    @Volatile private var running = false
    private var thread: Thread? = null
    private var socket: DatagramSocket? = null

    fun start() {
        if (running) return
        running = true
        try {
            lock = wifi.createMulticastLock("tabletmonitor").apply { setReferenceCounted(false); acquire() }
        } catch (e: Exception) { Log.w(TAG, "multicast lock: $e") }
        thread = Thread({ loop() }, "discovery").apply { isDaemon = true; start() }
    }

    fun stop() {
        running = false
        try { socket?.close() } catch (_: Exception) {}
        try { lock?.release() } catch (_: Exception) {}
    }

    /** PCs anunciados nos últimos 6 segundos, mais recente primeiro. */
    fun recentHosts(): List<Host> {
        val now = System.currentTimeMillis()
        return hosts.values.filter { now - it.lastSeen < 6000 }.sortedByDescending { it.lastSeen }
    }

    private fun loop() {
        val buf = ByteArray(512)
        while (running) {
            try {
                val s = DatagramSocket(null).apply {
                    reuseAddress = true
                    broadcast = true
                    soTimeout = 1500
                    bind(InetSocketAddress(Protocol.DISCOVERY_PORT))
                }
                socket = s
                while (running) {
                    val p = DatagramPacket(buf, buf.size)
                    try { s.receive(p) } catch (_: SocketTimeoutException) { continue }
                    val text = String(p.data, p.offset, p.length, Charsets.UTF_8)
                    val parts = text.split('|')
                    if (parts.size >= 3 && parts[0] == "TMON1") {
                        val addr = p.address.hostAddress ?: continue
                        val port = parts[2].trim().toIntOrNull() ?: Protocol.PORT
                        hosts[addr] = Host(addr, port, parts[1], System.currentTimeMillis())
                    }
                }
            } catch (e: Exception) {
                if (running) {
                    Log.w(TAG, "discovery: $e")
                    try { Thread.sleep(2000) } catch (_: InterruptedException) {}
                }
            } finally {
                try { socket?.close() } catch (_: Exception) {}
            }
        }
    }

    companion object { private const val TAG = "Discovery" }
}
