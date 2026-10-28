package com.apontes.tabletmonitor

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothClass
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log

/** Conexão RFCOMM com o PC pareado que publica o serviço TabletMonitor. */
@SuppressLint("MissingPermission")
class BluetoothLink(private val context: Context) {

    @Suppress("DEPRECATION")
    private val adapter: BluetoothAdapter? = try { BluetoothAdapter.getDefaultAdapter() } catch (_: Exception) { null }

    fun hasPermission(): Boolean =
        Build.VERSION.SDK_INT < 31 ||
            context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED

    val isAvailable: Boolean get() = adapter != null && hasPermission() && (try { adapter.isEnabled } catch (_: Exception) { false })

    /** Computadores pareados com o tablet (só esses são tentados — evita esperar por fones, caixas de som…). */
    fun pairedComputers(): List<BluetoothDevice> {
        if (!isAvailable) return emptyList()
        return try {
            adapter!!.bondedDevices.filter {
                val major = it.bluetoothClass?.majorDeviceClass
                major == BluetoothClass.Device.Major.COMPUTER || major == BluetoothClass.Device.Major.UNCATEGORIZED
            }
        } catch (e: Exception) {
            Log.w(TAG, "bondedDevices: $e")
            emptyList()
        }
    }

    fun displayName(d: BluetoothDevice): String = try { d.name ?: d.address } catch (_: Exception) { d.address }

    /** Conecta (bloqueia alguns segundos se o PC não estiver com o app aberto). */
    fun connect(device: BluetoothDevice): BluetoothSocket? {
        return try {
            adapter?.cancelDiscovery()
            val s = device.createRfcommSocketToServiceRecord(Protocol.BLUETOOTH_UUID)
            s.connect()
            s
        } catch (e: Exception) {
            Log.i(TAG, "bluetooth ${device.address}: $e")
            null
        }
    }

    companion object { private const val TAG = "BluetoothLink" }
}
