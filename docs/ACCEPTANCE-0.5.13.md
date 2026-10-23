# Playnite Link 0.5.13 — device removal and setup controls

Removed devices no longer appear in the connected-device projection or its cached UI list. There is no removed-device count, archive card, or Show removed devices control. A stale profile check-in cannot restore a removed device to the roster. The existing removal status record continues to prevent automatic reactivation. A removed current client receives a disconnected notice and an explicit reconnect action.

Synced saves, playtime and shared cloud-game identities remain available. Games whose only source device was removed can still be selected from **Synced vault history**, without displaying the former device's name. This preserves access to already-synced games while removing the retired client from device management.

This is removal from the active roster, not secure erasure of every historical metadata file. The immutable vault still stores prior device events because their hashes are referenced by later events and older clients. Physical history compaction and revocation of a device's provider credentials are separate operations. No production cloud event files or user saves are deleted by this update.

Setup no longer duplicates the title-bar close action with a **Continue later** button. A short note explains that choices are saved and the window can be closed to resume later. The existing close-and-resume host probe now uses the window close action and verifies the duplicate button is absent and the saved storage step reopens correctly.

Validation: the streaming/device suite includes restart, stale check-in, peer import and retained cloud-game discovery after removal. Actual Desktop and Fullscreen device UI probes verify the removed device and toggle are absent before and after refresh, including a new stale profile from the synthetic peer. The 0.5.12 read-only refresh behavior remains covered by those probes.

Results: 17 streaming/device checks and 8 audit regressions passed. Device UI and onboarding/close-resume probes passed in both Desktop and Fullscreen, including the existing vault-switch/restart isolation checks. A first Fullscreen device probe observed the visual tree before the replacement error page had laid out; the probe now forces layout before asserting error visibility, and the unchanged production UI passed the rerun. Device and setup screenshots were inspected for readable controls and saved-progress guidance.

This version is packaged locally for installation; GitHub publication is separate.
