# Changelog

## 0.5.4 — 2026-10-20

- Add read-only Test connection and local Reset connection controls.
- Replace typed provider booleans with buttons and personal-Drive defaults.
- Verify stable cloud-folder identity when reconnecting through a different local remote name.
- Recover saved sign-ins and surface errors inline with correct dialog ownership.
- Let Playnite finish startup while its first-run walkthrough is open.


## 0.5.3 — 2026-10-20

- Guide first-time users through storage, games/save folders, opt-ins and first sync.
- Save walkthrough progress and defer automatic sync until setup is complete.
- Stop queued transfers, persist pause, and expose Resume sync after cancellation.
- Verify recovery, navigation, conflicts and fast launches in Desktop and Fullscreen Playnite.

## 0.5.2 — 2026-10-20

- Set author attribution to metanmai.


## 0.5.1 — 2026-10-20

- Launch local games without waiting for a full cloud synchronization.
- Batch device, ledger, activity and revision metadata downloads.
- Defer save replacement if a game starts during download or staging.
- Preserve explicit review for known conflicts and existing app protection.
- Avoid an unconditional extra full sync after every GameActivity session.
- Validate launches and convergence on two real Playnite instances using Google Drive.

## 0.5.0

- Integrate GameActivity session history with stable identities and duplicate protection.
- Keep Playnite and HowLongToBeat display totals/counts consistent.
- Add optional absolute progress updates for existing online HLTB entries.
- Add automatic linking for unambiguous provider games and verified Rclone runtime acquisition.

Earlier work includes save backups/conflict review, large-file streaming, device management, opt-in addon installation sharing and local passcode protection.
