# Release-gate follow-up — 22 October 2026

This continues the 21 October audit. Results below are fresh runs; historical results are not substituted for current evidence. The evidence root is recorded in `evidence/closure-current-root.txt`. Private credentials and provider diagnostics remain outside the repository.

**A10 and A12 are closed for the audited Google Drive workflow: fresh browser enrollment, WAN acceptance and abrupt-termination recovery all passed. Release recommendation: CONDITIONALLY READY for Windows 11 / Playnite 10 with Google Drive.** There is no remaining confirmed P0/P1 defect from this audit in that tested scope. Release claims must retain the documented limits: Dropbox/OneDrive live acceptance, Windows 10, physical handheld controls and production-scale soak are unverified. Hosted CI remains a P2 follow-up. These limits do not reopen the completed Google enrollment/WAN or process-recovery gates.

## Recovery gate A12

The untested boundary exposed two actual defects: save replacement could leave the original folder renamed away after process termination, and the add-on helper's rollback list existed only in memory.

- Save replacement now flushes a transaction marker before moving the original directory. Capture and the game-launch gate recover a missing original folder from its backup. A fully installed replacement is kept with the original backup; no partial staged folder is installed by recovery. Malformed or incomplete recovery metadata fails closed. Stages left by a crash are retained for inspection.
- Add-on installation now flushes a journal before moving packages and flushes its completion receipt. Recovery rolls back uncommitted installations, retaining replacements separately, or recognizes a committed receipt. Interrupted recovery can be retried. An exclusive helper lease prevents concurrent recovery or a competing helper's premature host restart. Capture/restaging and premature receipt processing are blocked while recovery is pending. The Add-ons page offers **Recover interrupted installation and restart**, and startup reports the pending recovery.
- `CrashRecoveryTests`: **11/11 passed**. Actual child processes were killed at staged, journal, backup, installed and committed boundaries for existing and absent save roots; two separate recovery processes ran after every kill. Original/replacement bytes and retained backups were checked. Malformed journal rejection was checked.
- `Run-SetupCrashTests.ps1`: **4/4 passed**. Actual PowerShell helpers were killed after journal creation, original rename, replacement installation and durable receipt. Each fixture included existing, newly added and removed packages; recovery ran twice and verified bytes/presence.
- Actual Playnite Desktop and Fullscreen host tests passed with an interrupted save replacement injected before launch. Both launched the recovered bytes, rejected running-game history restores, and passed conflict/history/backups checks.
- Final full regression runner passed **246 checks** (242 executable checks plus four helper termination scenarios). The subsequently expanded metadata suite passed **8/8**, adding quota/authentication failure and immutable retry coverage: **247 distinct checks in the final inventory**. After the helper lease/restart guard, the affected installer suite again passed **18/18**, and the four kill scenarios passed with an added concurrent-helper/no-premature-restart assertion. No duplicate reruns are added to the inventory count.

These tests establish abrupt process-termination behavior on this Windows filesystem, not physical power-loss or storage-controller guarantees.

## Provider gate A10 — closed for Google Drive

- Existing Google Drive authorization is usable. Tests copy the authorized configuration and create unique synthetic vault prefixes; normal user vault contents are not changed.
- Real Google token endpoint checks passed: expired access token refresh/persistence, and rejection of an invalid refresh credential. Original credential configuration remained unchanged. This does not revoke the user's real grant.
- Real Google streaming acceptance passed: 41,943,053-byte save restored with SHA-256 verification, duplicated paths sharing one blob, conflicting branches resolved with backup preservation, two-device roster/settings/pause/resume/remove convergence, and exact 140-second ledger convergence. Runtime: 214.45 seconds for the complete scenario.
- Live authenticated HLTB acceptance passed: two no-op absolute submissions and five receipt checks, with progress/lists/scores/completions unchanged.
- Fresh browser enrollment passed through the production `CloudAccount.Configure` path after the account owner completed Google sign-in. A new private configuration received the authorization; no existing token was supplied to that enrollment. The first attempt timed out and is retained as an unsuccessful attempt. The successful connection uploaded and downloaded a unique synthetic file with matching SHA-256 hashes, then independently read and validated **42 existing test-vault metadata records**. First metadata read: **14.47 seconds**; warm read: **3.70 seconds**. Sanitized evidence: `evidence/audit-20261022/fresh-enrollment.json` and `fresh-auth-metadata.json`.
- First actual two-peer WAN acceptance attempt timed out publishing metadata. Queued local data was retained. Direct retry uploaded the synthetic records successfully. The fresh complete retry passed all three enrollment/restart phases and **30/30 main + 11/11 transfer scenarios**. These cover offline divergence, local/cloud/cancel decisions, deletion/empty-save confirmation, background conflict deferral, backup byte verification, live upload cancellation, absence of premature remote publication, resumed upload and subsequent two-client convergence. Exact native GameActivity totals remained two sessions/16 seconds without duplicate identities. Failures are retained separately and are not counted as passing.
- Quota exhaustion is not induced against the user's account. Actual provider-wide quota, physical handheld, Windows 10 and destructive power-loss testing are not claimed.
- Simulated quota and rejected-credential subprocesses exercised production transport failure handling; queued bytes survived, and real Rclone local-backend retry published one immutable object without duplicates.

## Packaging and onboarding

The isolated 0.5.11 candidate package was extracted and checked for exact tested DLL/helper hashes and absence of private test credentials. Its DLL passed actual Playnite onboarding, stop/resume, reconnect, vault switch, restart and history-isolation tests. A first onboarding run failed because its test expected the obsolete text “Personal vault synced”; the corrected assertion waits for the actual completed state. The failed run is retained and not counted as passing. This is isolated package loading, not a production deployment or Windows-wide installer test.

## Recovery instructions

For interrupted saves, restart Playnite normally. Recovery runs before capture or launch. Do not delete sibling `.pl-backup-*`, `.pl-stage-*` or `.pl-restore` files while recovery is unresolved. If the journal is malformed or both original and backup are missing, launch is blocked and the error identifies the recovery problem; preserve the files for manual review.

For interrupted add-ons, stop games and open **Playnite Link → Add-ons → Recover interrupted installation and restart**. The helper waits for Playnite to exit before moving packages. Successful rollback retains displaced replacements under the setup backup directory; settings and sign-ins remain local. Review and apply the desired update again after recovery.

If an interrupted third-party installation prevents Playnite itself from opening, close Playnite and run the packaged `SetupApply.ps1` with `-Plan` pointing to that profile's `setup/pending.json` and `-RecoverOnly`, then start Playnite. This is the same recovery path exercised by the helper-kill tests; do not re-stage or overwrite the pending plan first.

## Final checklist

- [x] Save and installer abrupt-termination recovery and regressions.
- [x] Actual Desktop/Fullscreen launch recovery.
- [x] Live Google access-token refresh and invalid-refresh rejection.
- [x] Complete actual two-peer WAN save/conflict/interruption acceptance.
- [x] Complete fresh Google browser authorization with user interaction and verify upload/download plus existing-vault reads.
- [x] Final package verification and evidence reconciliation; hashes and sanitized results are in `evidence/audit-20261022/`.

All gates raised in the selected release-blocker statement are now closed within the audited Google Drive scope. No production installation, release publication, real save-folder modification or real grant revocation was performed. The final isolated package hash and current acceptance summary are recorded in `evidence/audit-20261022/package-verification.json` and `closure-summary.json`.
