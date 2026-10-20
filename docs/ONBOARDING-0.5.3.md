# Onboarding and cancellation verification — 0.5.3

Validated in actual Playnite 10.62 Desktop and Fullscreen instances using isolated synthetic profiles.

- First-run wizard appears automatically; no background synchronization starts before setup completes.
- Close/reopen resumes the saved step. Storage, game selection/save folders, opt-ins and final review are grouped in one walkthrough.
- Optional online HLTB uploads and addon sharing remain off by default.
- A deterministic stalled Rclone process is stopped during a transfer. Requests queued before stop are cancelled.
- Cancellation released the sync gate in 217 ms (Desktop) and 189 ms (Fullscreen). UI heartbeat continued.
- Pause is written to local preferences. Automatic requests stay paused until explicitly resumed.
- Replacing the stalled fixture with the installed real Rclone and resuming succeeds. Repeated syncs create no extra sessions.
- Existing host scenarios passed in both modes: local/cloud conflict choices, cancelled launch review, save restores/backups, stable page navigation and preserved unsaved drafts.
- Launch times: Desktop 660/51 ms, Fullscreen 637/48 ms. Warm metadata checks: 182/195 ms. These measurements use a local Rclone alias, not WAN latency.
- 191 existing automated checks passed across ledger, vault, streaming, protection, setup, activity and HLTB suites.

No new real-cloud OAuth authorization was performed for this UI/cancellation change. Previous real Drive validation belongs to the 0.5.1 acceptance report.
