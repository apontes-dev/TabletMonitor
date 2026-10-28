package com.apontes.tabletmonitor

import android.content.Context

class Prefs(context: Context) {
    private val sp = context.getSharedPreferences("tabletmonitor", Context.MODE_PRIVATE)

    /** IP do PC para conexão Wi-Fi manual (opcional — normalmente a descoberta automática resolve). */
    var manualHost: String
        get() = sp.getString("manualHost", "") ?: ""
        set(v) = sp.edit().putString("manualHost", v).apply()

    /** 0 = paisagem, 1 = retrato, 2 = automático */
    var orientation: Int
        get() = sp.getInt("orientation", 0)
        set(v) = sp.edit().putInt("orientation", v).apply()

    /** Procurar o PC pela rede Wi-Fi. */
    var useWifi: Boolean
        get() = sp.getBoolean("useWifi", true)
        set(v) = sp.edit().putBoolean("useWifi", v).apply()

    /** Conectar ao PC pareado via Bluetooth (mais lento; usado quando não há USB nem Wi-Fi). */
    var useBluetooth: Boolean
        get() = sp.getBoolean("useBluetooth", true)
        set(v) = sp.edit().putBoolean("useBluetooth", v).apply()

    /** Escurece a tela quando o PC está desconectado há mais de 1 minuto. */
    var dimWhenIdle: Boolean
        get() = sp.getBoolean("dimWhenIdle", true)
        set(v) = sp.edit().putBoolean("dimWhenIdle", v).apply()
}
