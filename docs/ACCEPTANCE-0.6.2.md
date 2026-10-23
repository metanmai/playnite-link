# 0.6.2: independent sessions and HLTB progress

Link records dated sessions and displays device history without GameActivity. Optional display failures no longer veto native playtime projection. Stop callbacks use the durable active-session journal rather than requiring a volatile tracking entry. Unreadable save locations remain pending while session metadata uploads and totals project independently.

HLTB progress now uses Link's own browser sign-in and HTTP client, without loading the HLTB add-on. It matches unique exact titles in the signed-in user's existing profile, validates account/game/submission identity on each fresh edit, changes only progress, preserves greater totals and verifies writes before saving upload receipts. Missing or ambiguous profile entries remain pending. This release does not reproduce the HLTB add-on's theme controls or public time-to-beat search UI.

The migration page and manual review/import/retire workflow are removed. Available existing backups with an unambiguous local game ID are copied and verified in the background. Originals remain preserved. A loaded Save Manager assembly requires one normal Playnite restart before Link can safely take over. Ambiguous configurations fail safely and retain originals. Normal backup import/export remains available.

Validation: all regression suites passed, including process-kill recovery and retirement safeguards. The native HLTB protocol fixture passed 27 checks; no live authenticated HLTB update is claimed for this new client. The diagnostic collector passed credential-canary exclusion, session visibility, disabled-sync reporting, event classification and file-allowlist checks. Actual Playnite host validation exercises native sessions with neither optional add-on installed, loss of volatile tracking, and session upload/display while a save file is exclusively locked. Synthetic host tests are not physical Ally certification.

The actual Ally failure still requires its diagnostic report. Previous Bloodborne file/transfer validation remains valid; previous in-game crash/character-load limits are not reclassified as successful tests.
