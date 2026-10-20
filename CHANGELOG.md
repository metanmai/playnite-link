# Changelog

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
