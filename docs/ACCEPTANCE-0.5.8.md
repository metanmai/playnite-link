# Playnite Link 0.5.8: resumable cloud history

The recurring 1:45 error was an unconditional 105-second metadata deadline. The connected Drive vault contains 1,110 immutable JSON history records. The old transfer was terminated while still downloading records.

Metadata pulls now use a progress watchdog (45-second initial listing grace, 30 seconds without a completed record, 15-minute work cap), fast listing and verified immutable-cache reuse. Cancel/retry preserves completed objects; malformed cache entries are quarantined and downloaded again. Only verified metadata from local profiles with an identical nonempty stable cloud-folder identity can seed a reconnected profile. Mappings, settings and session state are never copied. Temporary network failures show a waiting/retry state, with cached-record counts during download. Save blobs retain their streaming transport.

Real Google Drive read-only tests: an empty cache downloaded all 1,110 records in 305.77 seconds, then checked again in 10.05 seconds. Reusing this computer's verified same-vault cache seeded all 1,110 records; validation plus the first check took 22.66 seconds, and the next check took 11.11 seconds. These are measured results, not a ten-second guarantee for first-time setup or every connection. No cloud records were modified by these probes.

157 assertions passed: 7 metadata, 58 ledger/game-picker, 63 vault/connection, 18 GameActivity, 11 HowLongToBeat. Actual isolated Desktop A and Fullscreen B, with both integrations enabled, passed add-on-A, choose-on-B, failed lookup, publication retry, unpublished ancestor publication, six repeated syncs per phase and reopened-A verification. Both ended with exactly one game, one mapping, two sessions and 16 seconds; GameActivity matched. The earlier separate real-Drive game-picker A/B/reopen suite also passed; the final timeout build's peer run used local Rclone storage. This change did not independently exercise online HLTB uploads.

Version 0.5.8 was installed into the two manual test clients, preserving profiles and credentials. The normal Playnite extension uninstall was preserved.
