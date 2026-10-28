package com.apontes.tabletmonitor

import android.app.Activity
import android.app.ActivityManager
import android.app.admin.DeviceAdminReceiver
import android.app.admin.DevicePolicyManager
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Build
import android.os.UserManager
import android.provider.Settings
import android.util.Log

/** Receptor de administração exigido para o app ser "dono do dispositivo" (Device Owner). */
class KioskAdminReceiver : DeviceAdminReceiver()

/** Reabre o app após o boot (como dono do dispositivo, ele pode abrir activity em segundo plano). */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        try {
            context.startActivity(
                Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            )
        } catch (e: Exception) {
            Log.w("BootReceiver", "não foi possível abrir: $e")
        }
    }
}

/**
 * Modo dedicado (quiosque). Só funciona por completo quando o app foi definido como dono do
 * dispositivo via adb:  dpm set-device-owner com.apontes.tabletmonitor/.KioskAdminReceiver
 */
object Kiosk {
    private const val TAG = "Kiosk"

    fun admin(ctx: Context) = ComponentName(ctx, KioskAdminReceiver::class.java)
    private fun dpm(ctx: Context) = ctx.getSystemService(Context.DEVICE_POLICY_SERVICE) as DevicePolicyManager

    fun isDeviceOwner(ctx: Context) = dpm(ctx).isDeviceOwnerApp(ctx.packageName)

    /** Aplica as políticas: app vira a tela inicial, sem barra de status, sem tela de bloqueio, tela sempre ligada na energia. */
    fun applyPolicies(ctx: Context) {
        if (!isDeviceOwner(ctx)) return
        val dpm = dpm(ctx)
        val admin = admin(ctx)
        fun safe(what: String, block: () -> Unit) =
            try { block() } catch (e: Exception) { Log.w(TAG, "$what: $e") }

        safe("lockTaskPackages") { dpm.setLockTaskPackages(admin, arrayOf(ctx.packageName)) }
        if (Build.VERSION.SDK_INT >= 28) {
            safe("lockTaskFeatures") { dpm.setLockTaskFeatures(admin, DevicePolicyManager.LOCK_TASK_FEATURE_NONE) }
        }
        safe("home") {
            val filter = IntentFilter(Intent.ACTION_MAIN).apply {
                addCategory(Intent.CATEGORY_HOME)
                addCategory(Intent.CATEGORY_DEFAULT)
            }
            dpm.addPersistentPreferredActivity(admin, filter, ComponentName(ctx, MainActivity::class.java))
        }
        safe("keyguard") { dpm.setKeyguardDisabled(admin, true) }
        safe("statusbar") { dpm.setStatusBarDisabled(admin, true) }
        // Tela sempre ligada quando alimentada por AC/USB/sem fio.
        safe("stayOn") {
            dpm.setGlobalSetting(admin, Settings.Global.STAY_ON_WHILE_PLUGGED_IN, "7")
        }
        // Mantém a depuração USB ativa (necessária para o modo USB).
        safe("adb") {
            @Suppress("DEPRECATION")
            dpm.setGlobalSetting(admin, Settings.Global.ADB_ENABLED, "1")
        }
        // Android 12+: o próprio app se concede a permissão de Bluetooth.
        if (Build.VERSION.SDK_INT >= 31) safe("btPermission") {
            dpm.setPermissionGrantState(admin, ctx.packageName, "android.permission.BLUETOOTH_CONNECT",
                DevicePolicyManager.PERMISSION_GRANT_STATE_GRANTED)
        }
        safe("restrictions") {
            dpm.addUserRestriction(admin, UserManager.DISALLOW_SAFE_BOOT)
        }
    }

    fun enterLockTask(activity: Activity) {
        val dpm = dpm(activity)
        if (!dpm.isLockTaskPermitted(activity.packageName)) return
        val am = activity.getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager
        if (am.lockTaskModeState == ActivityManager.LOCK_TASK_MODE_NONE) {
            try { activity.startLockTask() } catch (e: Exception) { Log.w(TAG, "startLockTask: $e") }
        }
    }

    fun leaveLockTaskTemporarily(activity: Activity) {
        try { activity.stopLockTask() } catch (_: Exception) {}
    }

    /** Remove o modo dedicado por completo e devolve o tablet ao normal. */
    fun removeAll(activity: Activity) {
        val dpm = dpm(activity)
        val admin = admin(activity)
        try { activity.stopLockTask() } catch (_: Exception) {}
        if (!isDeviceOwner(activity)) return
        try { dpm.clearPackagePersistentPreferredActivities(admin, activity.packageName) } catch (_: Exception) {}
        try { dpm.setStatusBarDisabled(admin, false) } catch (_: Exception) {}
        try { dpm.setKeyguardDisabled(admin, false) } catch (_: Exception) {}
        try {
            dpm.clearUserRestriction(admin, UserManager.DISALLOW_SAFE_BOOT)
        } catch (_: Exception) {}
        try { dpm.setLockTaskPackages(admin, arrayOf()) } catch (_: Exception) {}
        try {
            @Suppress("DEPRECATION")
            dpm.clearDeviceOwnerApp(activity.packageName)
        } catch (e: Exception) { Log.w(TAG, "clearDeviceOwner: $e") }
    }
}
