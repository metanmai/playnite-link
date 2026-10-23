# Playnite Link 0.5.12 — device list controls

Removed devices are hidden by default. **Show removed devices** reveals retained history and offers **Enable sync again**. The current device remains visible even when its own sync status is removed. Active and paused devices show **Remove device…** directly on the card; the action publishes the existing cooperative removal status and preserves history.

**Refresh devices** now reads only the device directory. It does not scan saves, synchronize games or resume paused synchronization. Its button shows a busy state, ignores duplicate clicks, and reports success time or an inline error. A failed refresh keeps the last received list and permits retry. A refresh waits for an active sync and respects cancellation or a pending vault switch.

The previous button ran the entire game/save synchronization path without a device-page outcome. A correct but unchanged list therefore looked unresponsive; unrelated game/save work could delay refreshing it. Removed records stayed visible indefinitely after removal.

Validation passed in actual isolated Playnite Desktop/Fullscreen windows with a real Rclone local alias. The device probe checks default hiding, reveal/hide, retained history, visible removal actions, refresh while paused, waiting for busy sync, duplicate-click protection, remote-device arrival, no save captures or cloud uploads, responsive UI, and inline failure with retained data. Results are recorded as `docs/evidence/devices-desktop-host.json` and `devices-fullscreen-host.json`. The eight audit and sixteen streaming/device regression checks also passed. The Desktop screenshot was visually inspected for readable status, filter and removal controls.

This update does not change immutable event schemas, save restore or credential handling. The 0.5.11 audit remains the prior validation baseline. This package is prepared locally; GitHub publication is a separate operation.
