package com.apontes.tabletmonitor

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.ActivityInfo
import android.content.res.Configuration
import android.graphics.Color
import android.graphics.Typeface
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.text.InputType
import android.util.DisplayMetrics
import android.util.TypedValue
import android.view.Gravity
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.TextView
import java.net.Inet4Address
import java.net.NetworkInterface

class MainActivity : Activity(), SurfaceHolder.Callback, StreamClient.Listener {

    private lateinit var prefs: Prefs
    private lateinit var discovery: Discovery
    private lateinit var client: StreamClient

    private lateinit var root: FrameLayout
    private lateinit var surfaceView: SurfaceView
    private lateinit var overlay: LinearLayout
    private lateinit var statusText: TextView
    private lateinit var infoText: TextView

    private val ui = Handler(Looper.getMainLooper())
    private var surfaceReady = false
    private var resumed = false
    private var videoW = 0
    private var videoH = 0
    private var lastScreen: StreamClient.ScreenInfo? = null
    private var dimmed = false

    private val dimRunnable = Runnable { setDimmed(true) }
    private val infoRefresher = object : Runnable {
        override fun run() {
            refreshInfo()
            ui.postDelayed(this, 5000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = Prefs(this)
        discovery = Discovery(this)
        client = StreamClient(this, discovery, prefs, this)

        window.addFlags(
            WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or
                WindowManager.LayoutParams.FLAG_FULLSCREEN or
                WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED or
                WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON
        )
        applyOrientation()
        Kiosk.applyPolicies(this)
        buildUi()
    }

    // ---------------------------------------------------------------- UI

    private fun dp(v: Float) = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, resources.displayMetrics)

    private fun buildUi() {
        root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }

        surfaceView = SurfaceView(this)
        surfaceView.holder.addCallback(this)
        surfaceView.setOnTouchListener { v, e -> handleTouch(v, e); true }
        root.addView(surfaceView, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT, Gravity.CENTER))

        overlay = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setBackgroundColor(Color.rgb(16, 20, 24))
            val pad = dp(32f).toInt()
            setPadding(pad, pad, pad, pad)
        }
        val title = TextView(this).apply {
            text = "TabletMonitor"
            setTextColor(Color.rgb(232, 238, 242))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 34f)
            typeface = Typeface.create("sans-serif-light", Typeface.NORMAL)
            gravity = Gravity.CENTER
        }
        statusText = TextView(this).apply {
            text = "Iniciando…"
            setTextColor(Color.rgb(61, 220, 151))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 20f)
            gravity = Gravity.CENTER
            setPadding(0, dp(16f).toInt(), 0, dp(24f).toInt())
        }
        infoText = TextView(this).apply {
            setTextColor(Color.rgb(150, 160, 170))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
            gravity = Gravity.CENTER
            setLineSpacing(0f, 1.35f)
        }
        val hint = TextView(this).apply {
            text = "Toque e segure por 3 segundos para abrir as configurações"
            setTextColor(Color.rgb(90, 100, 110))
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            gravity = Gravity.CENTER
            setPadding(0, dp(32f).toInt(), 0, 0)
        }
        overlay.addView(title)
        overlay.addView(statusText)
        overlay.addView(infoText)
        overlay.addView(hint)

        // Pressão longa de 3 s abre as configurações (só aparece quando não há PC conectado).
        var downAt = 0L
        val openSettings = Runnable { showSettings() }
        overlay.setOnTouchListener { _, e ->
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downAt = System.currentTimeMillis()
                    if (dimmed) setDimmed(false)
                    ui.postDelayed(openSettings, 3000)
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> ui.removeCallbacks(openSettings)
            }
            true
        }
        root.addView(overlay, FrameLayout.LayoutParams(
            FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))

        root.addOnLayoutChangeListener { _, l, t, r, b, ol, ot, or, ob ->
            if (r - l != or - ol || b - t != ob - ot) layoutVideo()
        }
        setContentView(root)
        refreshInfo()
    }

    private fun refreshInfo() {
        val ip = localIp()
        val owner = Kiosk.isDeviceOwner(this)
        val sb = StringBuilder()
        sb.append("USB: ligue o cabo no PC (depuração USB ativada)\n")
        if (prefs.useWifi) {
            sb.append("Wi-Fi: mesma rede do PC")
            if (ip != null) sb.append(" — IP do tablet: ").append(ip)
            if (prefs.manualHost.isNotBlank()) sb.append(" — PC fixo: ").append(prefs.manualHost)
            sb.append('\n')
        }
        if (prefs.useBluetooth) {
            val pcs = try { client.pairedComputers() } catch (_: Exception) { emptyList() }
            sb.append("Bluetooth: ")
            sb.append(if (pcs.isEmpty()) "nenhum PC pareado (pareie nas configurações)" else "pareado com " + pcs.joinToString(", "))
            sb.append('\n')
        }
        sb.append(if (owner) "Modo dedicado: ativo" else "Modo dedicado: desativado (veja o guia)")
        infoText.text = sb.toString()
    }

    private fun hideSystemUi() {
        @Suppress("DEPRECATION")
        window.decorView.systemUiVisibility = (
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or
                View.SYSTEM_UI_FLAG_FULLSCREEN or
                View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemUi()
    }

    private fun applyOrientation() {
        requestedOrientation = when (prefs.orientation) {
            1 -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
            2 -> ActivityInfo.SCREEN_ORIENTATION_FULL_SENSOR
            else -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        }
    }

    /** Ajusta a SurfaceView para caber na tela mantendo a proporção do vídeo. */
    private fun layoutVideo() {
        val cw = root.width
        val ch = root.height
        if (cw == 0 || ch == 0) return
        val lp = surfaceView.layoutParams as FrameLayout.LayoutParams
        if (videoW <= 0 || videoH <= 0) {
            lp.width = FrameLayout.LayoutParams.MATCH_PARENT
            lp.height = FrameLayout.LayoutParams.MATCH_PARENT
        } else {
            val scale = minOf(cw.toFloat() / videoW, ch.toFloat() / videoH)
            lp.width = (videoW * scale).toInt()
            lp.height = (videoH * scale).toInt()
        }
        lp.gravity = Gravity.CENTER
        surfaceView.layoutParams = lp
    }

    private fun setDimmed(dim: Boolean) {
        dimmed = dim
        val lp = window.attributes
        lp.screenBrightness = if (dim) 0.01f else WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
        window.attributes = lp
        overlay.alpha = if (dim) 0.25f else 1f
    }

    // ---------------------------------------------------------------- ciclo de vida

    override fun onResume() {
        super.onResume()
        resumed = true
        hideSystemUi()
        Kiosk.applyPolicies(this)
        SystemTweaks.apply(this)
        if (prefs.useBluetooth) SystemTweaks.ensureBluetoothOn(this)
        Kiosk.enterLockTask(this)
        discovery.start()
        ui.post(infoRefresher)
        scheduleDim()
        maybeStartClient()
    }

    override fun onPause() {
        super.onPause()
        resumed = false
        ui.removeCallbacks(infoRefresher)
        ui.removeCallbacks(dimRunnable)
        client.stop()
        discovery.stop()
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        val info = screenInfo()
        if (info != lastScreen) {
            lastScreen = info
            if (client.isConnected) client.reconnect(info)
        }
    }

    override fun onBackPressed() {
        // Desativado: o tablet é dedicado.
    }

    private var capsCache: Pair<Pair<Int, Int>, DecoderCaps.Result>? = null

    private fun screenInfo(): StreamClient.ScreenInfo {
        val m = DisplayMetrics()
        @Suppress("DEPRECATION")
        windowManager.defaultDisplay.getRealMetrics(m)
        val key = m.widthPixels to m.heightPixels
        val caps = capsCache?.takeIf { it.first == key }?.second
            ?: DecoderCaps.bestSize(m.widthPixels, m.heightPixels).also { capsCache = key to it }
        return StreamClient.ScreenInfo(m.widthPixels, m.heightPixels, m.densityDpi, caps.width, caps.height)
    }

    private fun maybeStartClient() {
        if (resumed && surfaceReady && !client.isRunning) {
            val info = screenInfo()
            lastScreen = info
            client.start(surfaceView.holder.surface, info)
        }
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        surfaceReady = true
        maybeStartClient()
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        surfaceReady = false
        client.stop()
    }

    // ---------------------------------------------------------------- StreamClient.Listener

    override fun onStatus(text: String) {
        ui.post { statusText.text = text }
    }

    override fun onConnected(peer: String, transport: StreamClient.Transport) {
        ui.post {
            ui.removeCallbacks(dimRunnable)
            if (dimmed) setDimmed(false)
        }
    }

    override fun onVideoConfig(width: Int, height: Int) {
        ui.post {
            videoW = width
            videoH = height
            layoutVideo()
            overlay.visibility = View.GONE
        }
    }

    override fun onDisconnected() {
        ui.post {
            overlay.visibility = View.VISIBLE
            statusText.text = "PC desconectado"
            videoW = 0
            videoH = 0
            layoutVideo()
            scheduleDim()
        }
    }

    private fun scheduleDim() {
        ui.removeCallbacks(dimRunnable)
        if (prefs.dimWhenIdle) ui.postDelayed(dimRunnable, 60_000)
    }

    // ---------------------------------------------------------------- toque

    private fun handleTouch(v: View, e: MotionEvent) {
        val w = v.width.toFloat().coerceAtLeast(1f)
        val h = v.height.toFloat().coerceAtLeast(1f)
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> {
                val i = e.actionIndex
                client.sendTouch(Protocol.ACTION_DOWN, e.getPointerId(i), e.getX(i) / w, e.getY(i) / h)
            }
            MotionEvent.ACTION_MOVE -> {
                for (i in 0 until e.pointerCount) {
                    client.sendTouch(Protocol.ACTION_MOVE, e.getPointerId(i), e.getX(i) / w, e.getY(i) / h)
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_POINTER_UP -> {
                val i = e.actionIndex
                client.sendTouch(Protocol.ACTION_UP, e.getPointerId(i), e.getX(i) / w, e.getY(i) / h)
            }
            MotionEvent.ACTION_CANCEL -> {
                for (i in 0 until e.pointerCount) {
                    client.sendTouch(Protocol.ACTION_CANCEL, e.getPointerId(i), e.getX(i) / w, e.getY(i) / h)
                }
            }
        }
    }

    // ---------------------------------------------------------------- configurações

    private fun showSettings() {
        val pad = dp(20f).toInt()
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad / 2, pad, 0)
        }
        fun label(t: String) = TextView(this).apply {
            text = t
            setPadding(0, pad / 2, 0, pad / 4)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
        }

        box.addView(label("IP do PC (opcional — só se a descoberta automática no Wi-Fi falhar)"))
        val hostEdit = EditText(this).apply {
            setText(prefs.manualHost)
            hint = "ex.: 192.168.0.10"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            isSingleLine = true
        }
        box.addView(hostEdit)

        box.addView(label("Orientação da tela"))
        val group = RadioGroup(this).apply { orientation = RadioGroup.HORIZONTAL }
        listOf("Paisagem", "Retrato", "Automática").forEachIndexed { i, t ->
            group.addView(RadioButton(this).apply { text = t; id = 1000 + i })
        }
        group.check(1000 + prefs.orientation)
        box.addView(group)

        val useWifi = CheckBox(this).apply {
            text = "Conectar por Wi-Fi"
            isChecked = prefs.useWifi
        }
        box.addView(useWifi)
        val useBt = CheckBox(this).apply {
            text = "Conectar por Bluetooth (mais lento — usado quando não há cabo nem Wi-Fi)"
            isChecked = prefs.useBluetooth
        }
        box.addView(useBt)

        val dim = CheckBox(this).apply {
            text = "Escurecer a tela quando o PC estiver desconectado"
            isChecked = prefs.dimWhenIdle
        }
        box.addView(dim)

        val wifiBtn = Button(this).apply { text = "Configurar Wi-Fi do tablet" }
        box.addView(wifiBtn)
        val btBtn = Button(this).apply { text = "Parear Bluetooth com o PC" }
        box.addView(btBtn)
        val exitBtn = Button(this).apply { text = "Sair do modo dedicado" }
        box.addView(exitBtn)

        val dialog = AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
            .setTitle("TabletMonitor — configurações")
            .setView(box)
            .setPositiveButton("Salvar") { _, _ ->
                prefs.manualHost = hostEdit.text.toString().trim()
                prefs.orientation = (group.checkedRadioButtonId - 1000).coerceIn(0, 2)
                prefs.dimWhenIdle = dim.isChecked
                prefs.useWifi = useWifi.isChecked
                prefs.useBluetooth = useBt.isChecked
                if (prefs.useBluetooth) SystemTweaks.ensureBluetoothOn(this)
                applyOrientation()
                refreshInfo()
                scheduleDim()
            }
            .setNegativeButton("Cancelar", null)
            .create()

        wifiBtn.setOnClickListener {
            dialog.dismiss()
            Kiosk.leaveLockTaskTemporarily(this)
            try { startActivity(Intent(Settings.ACTION_WIFI_SETTINGS)) } catch (_: Exception) {}
        }
        btBtn.setOnClickListener {
            dialog.dismiss()
            SystemTweaks.ensureBluetoothOn(this)
            Kiosk.leaveLockTaskTemporarily(this)
            AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                .setTitle("Parear com o PC")
                .setMessage("1. No PC: Configurações > Bluetooth e dispositivos > Adicionar dispositivo > Bluetooth.\n" +
                    "2. Na próxima tela do tablet, toque no nome do PC e confirme o código nos dois aparelhos.\n" +
                    "3. Volte ao TabletMonitor (botão Voltar).\n\n" +
                    "Só precisa fazer isso uma vez.")
                .setPositiveButton("Abrir Bluetooth") { _, _ ->
                    try { startActivity(Intent(Settings.ACTION_BLUETOOTH_SETTINGS)) } catch (_: Exception) {}
                }
                .setNegativeButton("Cancelar") { _, _ -> Kiosk.enterLockTask(this) }
                .show()
        }
        exitBtn.setOnClickListener {
            AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert)
                .setTitle("Sair do modo dedicado?")
                .setMessage("O tablet volta a funcionar normalmente (tela inicial, barra de status, outros apps). " +
                    "Para reativar, rode a preparação do tablet de novo pelo app do Windows.")
                .setPositiveButton("Sair") { _, _ ->
                    dialog.dismiss()
                    Kiosk.removeAll(this)
                    try {
                        startActivity(Intent(Settings.ACTION_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
                    } catch (_: Exception) {}
                }
                .setNegativeButton("Cancelar", null)
                .show()
        }
        dialog.show()
    }

    private fun localIp(): String? = try {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.inetAddresses.toList() }
            .firstOrNull { it is Inet4Address && !it.isLoopbackAddress }
            ?.hostAddress
    } catch (_: Exception) { null }
}
