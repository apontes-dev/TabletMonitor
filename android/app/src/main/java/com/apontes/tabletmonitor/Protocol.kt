package com.apontes.tabletmonitor

/**
 * Protocolo TabletMonitor v1 (TCP, big-endian).
 *
 * Cliente (tablet) -> servidor (PC):
 *   "TMON" (4 bytes, uma vez) e depois pacotes [tipo u8][tamanho u32][payload]
 *   HELLO  0x00: versão u8, largura u16, altura u16, dpi u16, maxLargura u16, maxAltura u16 (v2), nome (utf-8 até o fim)
 *   TOUCH  0x10: ação u8 (0 down, 1 move, 2 up, 3 cancel), id u8, x u16, y u16 (0..65535)
 *   PING   0x20: timestamp u64
 *
 * Servidor -> cliente:
 *   CONFIG 0x01: largura u16, altura u16, fps u16
 *   CSD    0x02: SPS+PPS em Annex-B
 *   FRAME  0x03: flags u8 (1 = keyframe), pts u64 (µs), access unit em Annex-B
 *   PONG   0x21: timestamp u64 (eco)
 */
object Protocol {
    const val PORT = 27183
    const val DISCOVERY_PORT = 27184
    val MAGIC = byteArrayOf('T'.code.toByte(), 'M'.code.toByte(), 'O'.code.toByte(), 'N'.code.toByte())
    const val VERSION = 2

    /** Serviço RFCOMM do app do Windows (BluetoothServer.cs). */
    val BLUETOOTH_UUID: java.util.UUID = java.util.UUID.fromString("5b8c9e3a-1f47-4d2e-a6b1-7c0e3d9f2a64")

    const val HELLO = 0x00
    const val CONFIG = 0x01
    const val CSD = 0x02
    const val FRAME = 0x03
    const val TOUCH = 0x10
    const val PING = 0x20
    const val PONG = 0x21

    const val ACTION_DOWN = 0
    const val ACTION_MOVE = 1
    const val ACTION_UP = 2
    const val ACTION_CANCEL = 3

    const val MAX_PACKET = 16 * 1024 * 1024
}
