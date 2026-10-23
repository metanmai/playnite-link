## Save Manager replacement and sync fix

- Reuses Jurangren's Save Manager 2.0.3 ZIP/CRC backup core, with multi-folder/file saves, portable paths, exclusions, named backups, notes, retention, import/export, migration and verified retirement.
- Adds per-game device/session history and confirmed save-revision receipts. Offline, stale, pending and unknown devices are distinguished from an up-to-date confirmation.
- Fixes large-library receipt uploads: send only current required receipts and confirm only successfully uploaded records. Unconfigured games and accumulated historical outbox files no longer cause the observed receipt-upload timeout.
- Includes corresponding source under AGPL-3.0; original MIT notices are retained. This combined build changes the distribution license from earlier MIT-only releases.

## Validation

301 regression cases passed, including 44 replacement cases, actual process-kill recovery and retirement safeguards. Actual Playnite Desktop and Fullscreen replacement tests each passed 47 assertions; normal launch/conflict/runtime tests also passed in both modes.

Real Bloodborne save bytes passed native ZIP/CRC validation, byte-exact local restore, stale-file removal, concurrent-edit rejection, isolated offline conflict/cancel/resolution tests, fresh Google Drive hashing and an initially uncached separate-client cloud restore. This is file/transfer validation, not certification of in-game character loading.

## Known limits

The PC Bloodborne runtime encountered a null-write fault during the game test. Character loading and the physical ROG Ally transfer are not certified. The in-process diagnostic also encountered a locked runtime log; neither failure is counted as a successful clean game exit. Unrelated outside playtime edits remain reviewable rather than overwritten.

Live provider acceptance covers Google Drive. Other provider protocols have configuration/regression coverage, but their real accounts, Windows 10 and physical handheld controls remain unverified. The UI is English; backup deletion is individual. Deleted backup library entries retain immutable cloud history.

## Install on PC and Ally

Install the .pext below and restart Playnite. Use 0.6.1 on both devices, including when an older private handoff bundle contains 0.6.0. Keep each device's own cloud authorization and select the existing cloud game identity rather than creating a duplicate. Back up destination saves before any authoritative handoff restore. A private handoff ZIP and its SHA-256 are unchanged by this release.

No user saves, provider credentials, private handoff bundles or private runtime logs are included in this GitHub release. See docs/ACCEPTANCE-0.6.1.md and docs/ACCEPTANCE-0.6.0.md in the source archive for the case matrix and limitations.