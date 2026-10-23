## Changes

- Durable recovery after abrupt termination during save replacement or add-on installation; original save/package backups are preserved.
- Verify cached and queued save blobs before reuse or upload, bound JSON parsing, and prevent remote settings from granting local save-path sharing.
- Defer historical save restores while a game is starting or running. Improve save conflict review, cloud game linking, and local regression tooling.

## Validation and accepted scope

247 distinct regression checks and all 41 live Google Drive save/conflict/transfer scenarios passed. Actual Desktop and Fullscreen Playnite tests cover launch recovery, conflict decisions and restore safety. Fresh Google browser enrollment, upload/download hashes, token refresh/rejection, a 40 MiB save round trip, and authenticated HLTB no-op updates passed.

Conditionally ready for Windows 11 / Playnite 10 with Google Drive. Dropbox/OneDrive live acceptance, Windows 10, physical handheld controls and production-scale soak remain unverified. See docs/AUDIT-CLOSURE-2026-10-22.md in the matching source archive.

## Install

Download the .pext below, open it with Playnite, and restart. Save folders and optional integrations remain explicit local choices. New Google Drive connections require a personal Desktop OAuth client JSON.

This release replaces the older published releases. Historical Git tags are retained.