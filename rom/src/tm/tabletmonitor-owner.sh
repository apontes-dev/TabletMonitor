#!/system/bin/sh
# Ativa o modo dedicado (Device Owner) do TabletMonitor, se ainda não estiver ativo.
PKG=com.apontes.tabletmonitor
LOG=/data/local/tmp/tabletmonitor-owner.log
if dumpsys device_policy 2>/dev/null | grep -A3 -i "Device Owner" | grep -q "$PKG"; then
  exit 0
fi
sleep 8
{
  echo "=== $(date)"
  dpm set-device-owner "$PKG/.KioskAdminReceiver"
} >> "$LOG" 2>&1
am start -n "$PKG/.MainActivity" >> "$LOG" 2>&1
exit 0
