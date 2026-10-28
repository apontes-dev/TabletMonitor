package com.apontes.tabletmonitor

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.content.Context
import android.content.pm.PackageManager
import android.provider.Settings
import android.util.Log

/**
 * Ajustes de sistema que o app aplica sozinho quando tem WRITE_SECURE_SETTINGS
 * (vem com a ROM do TWRP, ou é concedida pelo app do Windows na preparação do tablet).
 */
object SystemTweaks {
    private const val TAG = "SystemTweaks"

    fun canWriteSecure(ctx: Context) =
        ctx.checkSelfPermission(Manifest.permission.WRITE_SECURE_SETTINGS) == PackageManager.PERMISSION_GRANTED

    fun apply(ctx: Context) {
        if (!canWriteSecure(ctx)) return
        val cr = ctx.contentResolver
        fun g(key: String, v: String) = try { Settings.Global.putString(cr, key, v) } catch (e: Exception) { Log.w(TAG, "$key: $e") }
        fun s(key: String, v: String) = try { Settings.Secure.putString(cr, key, v) } catch (e: Exception) { Log.w(TAG, "$key: $e") }

        // Sem o assistente de configuração (ROM), o sistema precisa saber que o aparelho está "configurado",
        // senão o botão Início e as notificações ficam bloqueados.
        g(Settings.Global.DEVICE_PROVISIONED, "1")
        s("user_setup_complete", "1")
        // Tela sempre ligada na energia, sem animações, Wi-Fi nunca dorme, sem protetor de tela.
        g(Settings.Global.STAY_ON_WHILE_PLUGGED_IN, "7")
        g(Settings.Global.WINDOW_ANIMATION_SCALE, "0")
        g(Settings.Global.TRANSITION_ANIMATION_SCALE, "0")
        g(Settings.Global.ANIMATOR_DURATION_SCALE, "0")
        g("wifi_sleep_policy", "2")
        s("screensaver_enabled", "0")
        s("screensaver_activate_on_sleep", "0")
        s("screensaver_activate_on_dock", "0")
        // Depuração USB ligada (modo USB).
        g(Settings.Global.ADB_ENABLED, "1")
    }

    /** Liga o Bluetooth se o modo Bluetooth estiver ativo e o rádio desligado. */
    @Suppress("DEPRECATION", "MissingPermission")
    fun ensureBluetoothOn(ctx: Context) {
        try {
            val adapter = BluetoothAdapter.getDefaultAdapter() ?: return
            if (!adapter.isEnabled) adapter.enable()
        } catch (e: Exception) { Log.w(TAG, "bluetooth: $e") }
    }
}
