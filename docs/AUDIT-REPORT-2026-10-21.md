# Production audit — 21 October 2026

## A. Executive summary

**Historical 21 October recommendation: NOT READY for an unrestricted production release.** This audit repaired confirmed integrity, privacy, validation and lifecycle defects. **Current recommendation is CONDITIONALLY READY for Windows 11 / Playnite 10 with Google Drive:** the [22 October closure report](AUDIT-CLOSURE-2026-10-22.md) closes A10/A12 with fresh browser enrollment, live WAN and actual process-kill recovery evidence. The results and limitations below describe the original audit unless explicitly updated.

Scope: Playnite Link 0.5.11, Windows 10/11, Playnite 10, .NET Framework 4.8. Actual host tests used Playnite 10.63.0.0. Tests used disposable local profiles and synthetic games/saves. No real user library, installed extension, production account or save folder was modified. No release was published; the existing release package was not overwritten.

All project files were untracked at the start. No commit/branch operation was performed. This reviews the working files, not a diff against a verified release commit. Historical evidence was used for orientation only.

### Architecture and expected journeys

| Layer | Implementation and responsibilities |
|---|---|
| Host/UI | `Plugin.cs`, WPF views, walkthrough, account navigation and game picker. SDK callbacks record sessions and gate launches. |
| Local persistence | `Ledger.cs`: exclusive writer lease, durable JSON session journal and projection intent, flushed atomic writes with backups. `VaultProfiles.cs`: separate cloud-vault profiles. |
| Saves/preferences | `Vault.cs`: immutable content-addressed revision DAGs; `Blobs.cs`: streamed SHA-256 files, staged directory restore and retained backups. Favorite/Hidden/Notes are a separate revision kind. |
| Scheduling | `Personal.cs`: serialized worker queue, launch reservations, cancellation generations and idle backoff. Normal launches use local saves immediately; known conflicts require review. |
| Connection/transport | Rclone subprocesses through `Transport.cs`, `CloudAccount.cs`, `Connections.cs`, `ConnectionProbe.cs`, `RcloneRuntime.cs`; quoted argv, retries, waits and local OAuth. |
| Devices | `Devices.cs` profile/settings/status histories. Pause/removal is cooperative; actual credential revocation belongs to the storage provider. |
| Integrations | GameActivity session adoption/projection; optional HLTB progress; optional extension/theme packages staged by `SetupProfile.cs` and applied/rolled back by `SetupApply.ps1`. |
| Protection | DPAPI-protected PBKDF2 verifier, WPF window shielding and SDK launch guard. Extension privacy lock, not OS isolation. |
| Delivery | Framework compiler via `build.ps1`; installed Playnite assemblies; Toolbox packaging via `package.ps1`. Static help/OAuth pages are documentation. |

Primary journey: connect a personal storage account → select the same vault on each device → explicitly link a game → optionally select its dedicated saves directory → synchronize → play → publish the durable session/save change → resolve divergent versions explicitly. Account recovery, historical restore, interrupted-session review and optional integrations extend that journey.

There is no active account server, SQL schema/migrations, AI, subscription, payment or advertising subsystem. `server` and `scripts` are empty. RLS, billing and prompt-injection checks are not applicable.

## B. Readiness scorecard

Scores describe inspected scope, not certification. Insufficiently established categories are unverified.

| Category | Assessment | Evidence / limitation |
|---|---|---|
| Functional completeness | 8/10 | Coherent linking, saves, sessions, preferences, devices, lock and add-on flows. Manual directory discovery; no retention management. |
| Functional correctness | 8/10 | Regression suites, actual launches and two-device convergence. Provider-specific acceptance still missing. |
| Code quality | 6/10 | Good domain validation/tests; compressed multi-operation lines and large partial classes complicate review. |
| Architecture | 8/10 | Local-first journals and immutable histories fit personal-cloud requirements. Full-history materialization limits longevity. |
| Security | 7/10 | Integrity/path/DPAPI/lock tests, new JSON bounds and local path consent. Host dependency remains old; no exhaustive binary vulnerability inventory. |
| Reliability | 7/10 | Retries, backups, cancellation and installer rollback exercised. Abrupt termination at rename/commit boundaries unverified. |
| Performance | Unverified at production scale | Local warm metadata measured hundreds of milliseconds; 64 MiB and 4,100-file tests pass. No WAN/large-library SLO evidence. |
| UX/visual quality | 8/10 in tested WPF modes | Actual screenshots inspected; visible conflict footer, readable controls and retained drafts. Hardware/DPI/accessibility matrix incomplete. |
| Test coverage/confidence | 8/10 for local behavior | Broad executable and actual-host tests; no measured line or branch coverage. |
| Deployment readiness | Unverified for release | Build/package work. Clean install/upgrade, provider enrollment, pinned CI provenance remain open. |

## C. Remediation register

Symbol locations remain useful if line numbers shift. Tests and initial failures are retained in the evidence directory described below.

| ID | Severity / status | Location, evidence, root cause and impact | Resolution / verification |
|---|---|---|---|
| A01 | P1 — fixed | `Blobs.cs:Put`, `Transport.cs:PushFiles`: cache valid bytes, overwrite with equal-length different bytes, recapture. Original only checked length; queued objects also uploaded without content-address validation. Corruption could spread and prevent restoration. | Verify SHA-256 on cache reuse and before blob upload. Original regression failed, corrected checks pass; corruption-before-restore tests also pass. Additional disk reads are an intentional integrity cost. |
| A02 | P1 — fixed | `SetupProfile.cs:Capture`: move an installed manifest aside and rescan. Discovery treated the still-existing package as removed and offered shared deletion to peers. | Publish removal only when the actual install path disappears. Test covers missing manifest, restored manifest and genuine removal. Original failed; fixed passes, along with all 18 setup tests. |
| A03 | P1 — mitigated locally | Host Newtonsoft.Json 10.0.3 was used without depth limits at JSON entry points; affected by CVE-2024-21907. Deep cloud/provider/local input could exhaust resources. | `JsonSafety.cs` caps depth at 64 and sets deserialization TypeNameHandling.None; all source JSON read entry points routed through it. Deep typed/token inputs and extra documents rejected. Host global settings and assemblies unchanged. Dependency itself remains host-owned. |
| A04 | P1 — fixed | `DeviceViews.cs:RestoreHistoryAsync` lacked normal sync's game-busy check and replacement reservation. Manual restore could replace a running/starting game's files. | Core checks before/after queueing and immediately before replacement; reserves through checkpoint persistence and respects cancellation generations. Both actual host modes reject two running-game attempts and restore successfully while stopped. |
| A05 | P2 — fixed | `Plugin.cs:GetGameMenuItems`: initialization errors set repository to null, but menu callback dereferenced it. Damaged local state could break the game menu. | Return no extension actions while disabled; regression directly invokes the callback in disabled state. |
| A06 | P2 — fixed | `tests/PersonalHostProbe.cs` expected obsolete titles/buttons; first fresh desktop run timed out during conflict cancellation. | Update selectors to current UI without removing behavioral assertions; desktop/fullscreen pass. Add history-restore assertions. |
| A07 | P2 — fixed | README/product/UI documents contradicted immediate-launch, explicit-linking and active-theme behavior. Users could incorrectly expect a prelaunch cloud check. | Align current documentation with source and actual host behavior. Versioned historical acceptance reports remain historical. |
| A08 | P2 — partially fixed | Fixed build output collided with a running baseline test. No unified fail-fast regression command or pinned CI gate existed. | Optional build/package output directories; new `tests/Run-Regression.ps1` with fresh local fixtures and exit-code gates. Hosted CI/pinned SDK acquisition deferred. |
| A09 | P2 — fixed | `Personal.cs`/`DeviceViews.cs`: remote settings could enable another device's save-path sharing, contrary to local opt-in. | Remote requests cannot newly grant consent; remote UI cannot enable it. Regression covers local grant, remote rejection, retained consent and withdrawal. |
| A10 | P1 — closed for Google Drive | Original audit did not run fresh provider acceptance. | 22 October: fresh browser enrollment and synthetic upload/download hashes, 42-record existing-vault metadata reads, Google refresh/invalid-refresh rejection, large-save/conflict/device checks, 41/41 host WAN scenarios and HLTB no-op submissions pass. See closure report for provider scope and evidence. |
| A11 | P2 — deferred | `Vault.Read/Heads`, `DeviceDirectory.Read/Views`: full histories, caches and backups grow without pruning. Production-scale cost is unknown. | Measure long-history/multi-GiB behavior; separately design retention with ancestry safety. No invented performance threshold or measured failure. |
| A12 | P1 — fixed in follow-up | Actual termination could leave saves renamed away or add-on installations partially applied; previous rollback state was not durable. | Durable save and installer journals, idempotent recovery, pending-install guards and recovery UI. Actual process-kill matrices and Desktop/Fullscreen launch-recovery tests pass. See 22 October follow-up. |

No confirmed P0 defect was established. A10 is a validation blocker, not a demonstrated application failure.

Installed dependency evidence: Newtonsoft.Json file version `10.0.3.21018`, SHA-256 `973FD70CE48E5AC433A101B42871680C51E2FEBA2AEEC3D400DEA4115AF3A278`; Playnite SDK `6.18.0.0`, SHA-256 `73E07635701FC0E944D6B10A69727FB050E0105B18DA7522CF0F8CC9E366AED2`. Rclone 1.75.2's pinned archive was actually downloaded, checksum-verified and executed in RuntimeTests. This is not an exhaustive scan of optional installed binaries.

The [reviewed Json.NET advisory](https://github.com/advisories/GHSA-5crp-9r3c-p9vr) identifies versions before 13.0.1 and explicit MaxDepth mitigation. External research used Firecrawl. No host dependency was silently replaced.

## D. Feature completeness matrix

| Feature | Status | Fresh evidence / limits |
|---|---|---|
| Existing Rclone connection/vault pin/profile switch | Complete locally | Identity, alias, restart and profile-isolation tests. |
| Google/Dropbox/OneDrive browser enrollment | Google verified in follow-up; Dropbox/OneDrive unverified | Google fresh browser grant, upload/download and existing-vault metadata checks pass on 22 October. Protocol continuation tests cover structured questions. |
| Named cloud linking/publication retry | Complete locally | Actual two-peer picker runs, failed discovery, same-identity retry and first sync. |
| Playtime/counts/sessions/offline history | Complete locally | Ledger tests, actual game callbacks and native GameActivity convergence. |
| Saves/conflicts/cancel/deletion/retry | Complete in fixtures | Vault/streaming and actual two-peer tests; no commercial-game certification. |
| Save-history restore | Complete in tested hosts | Running-game rejection, stopped restore and new revision; power-loss recovery unverified. |
| Favorite/Hidden/Notes | Complete in tested scope | Strict portable schema and host favorite/notes projection. |
| Device settings/pause/removal | Complete cooperative controls | Device tests/UI; provider revocation remains external. |
| Path privacy | Complete after fix | Local consent regression. |
| App passcode | Complete in tested scope | DPAPI, WPF and actual lock probes in both modes. Explicit same-account bypass limitation. |
| Add-on/theme sharing | Complete in fixtures | Package staging, actual helper, rollback and active-theme protection; not all third-party packages certified. |
| Online HLTB | Partial validation | 11 protocol/adapter tests; no fresh authenticated online write. |
| Idle status/cancel/resume | Complete locally | Warm-cache/no-scan tests and interrupted upload scenarios. |
| Automated save-directory discovery | Missing, optional | Current design asks users for a dedicated folder. |
| Retention/compaction/storage reporting | Missing, post-launch design | History and backups retained indefinitely. |
| Handheld/gamepad-only setup | Unverified | Fullscreen rendering does not validate physical controls. |

Competitive comparison is intentionally narrow: [Ludusavi upstream](https://github.com/mtkennerly/ludusavi) documents broad save discovery and multiple library support. Manual directory selection is therefore an onboarding disadvantage here. Reviewed save-path suggestions are a high-value improvement, not a reason to replace the ledger or add unrelated functionality. No market ranking or comparative performance claim is made.

## E. Test results and evidence

Local evidence root: `C:\Users\win11\AppData\Local\PlayniteLink-Audit-f40a3683`. `final-*.txt` contains the full run after integrity/setup/JSON/menu fixes. Later history/privacy changes received targeted regressions and actual-host checks; earlier runs are not mislabeled as the last binary.

| Executable | Passed / failed in full run |
|---|---:|
| Tests | 61 / 0 |
| VaultTests | 64 / 0 |
| StreamingTests | 16 / 0 |
| SecurityTests | 9 / 0 |
| ApplicationLockTests | 15 / 0 |
| SetupTests | 18 / 0 |
| ActivityTests | 18 / 0 |
| HltbTests | 11 / 0 |
| MetadataTransferTests | 7 / 0 |
| RuntimeTests | 4 / 0 |
| AuditTests | 6 / 0; subsequently expanded to 7 for consent |

Full-run total: **229 checks passed**. These are executable cases, not a coverage percentage. Host/peer assertions are separate JSON records.

Final follow-up results: candidate AuditTests **7/7**, StreamingTests **16/16**, candidate desktop host passed (launches 674/51 ms, warm local metadata 389 ms). The local two-peer acceptance completed **30 main + 20 extra save scenarios, all passing**, plus three picker/native-GameActivity runs. The peer run used the JSON/integrity-fixed build before the final history/privacy additions; those additions were verified separately as described above. Full logs are copied into `docs/evidence/audit-20261021/`; peer tallies are `audit-20261021-local-tally.json` and `audit-20261021-local-extra-tally.json`.

Isolated candidate package SHA-256: `EF07B49A17BF27B8EFA5DACF0E5B1A1893AFF86F6C26E1F2FE6196012B11236E`. It is retained under the evidence root's `candidate-package` directory and was not installed or published. It was packaged before this final audit document was written; this report is a separate deliverable.

Executed commands used `pwsh -NoProfile -ExecutionPolicy Bypass -File`:

- `build.ps1 -BuildDirectory <evidence-root>/final-build`, followed by each executable above in its own fresh local directory. Transport tests received the installed Rclone path; SetupTests received `final-build/SetupApply.ps1` as its third argument.
- `tests/Run-HostProbe.ps1 -Mode Desktop -SkipBuild -BuildDirectory <evidence-root>/release-build`, and the Fullscreen equivalent, after the history fix.
- Separate `-LockProbe` runs in both modes with `final-build`.
- `tests/Run-SaveAcceptance.ps1 -EvidencePrefix audit-20261021-local` for actual local two-peer scenarios.
- `package.ps1 -SkipBuild -BuildDirectory <evidence-root>/candidate-build -ReleaseDirectory <evidence-root>/candidate-package`.

History-fix host results: desktop launches 514/64 ms, warm metadata 368 ms; fullscreen launches 1114/78 ms, warm metadata 227 ms. Both modes rejected two history attempts during real running games and restored while stopped. Both preserved drafts, exercised cloud/local choices and canceled a launch. Lock probes verified unlinked-game blocking, wrong/correct passcodes and relocking. These are small local-alias fixtures, not WAN benchmarks.

Current WPF images: `docs/evidence/desktop-*.png`, `fullscreen-*.png`, `lock-*-app-lock.png`; peer images use `audit-20261021-local`. Account, game, conflict and lock captures were visually inspected. Controls were readable, scrolling available and final conflict actions visible. In-process routed button events exercise actual WPF, not physical mouse/gamepad or assistive technology.

Failures retained: original new regressions failed 0/2; initial desktop host failed on old selectors; initial SetupTests ran 9/18 because the audit invocation supplied a PowerShell provider-qualified path to a native helper. Corrected Windows helper paths passed 18/18. A rebuild while the baseline DLL was loaded failed; isolated output builds resolved it. Direct script invocation was blocked by signing policy; the documented process-local bypass worked. No failed run is counted as passing.

The new regression runner is a reusable equivalent of the executed batch; its complete end-to-end execution is not claimed merely because those individual commands ran. No line/branch coverage was collected.

## F. Outstanding work

1. **A10 closed 22 October for Google Drive:** fresh browser authorization, synthetic upload/download and existing-vault reads passed, along with credential refresh/rejection, WAN transfer/interruption and signed-in HLTB. Quota failure/retry was simulated. Dropbox/OneDrive live acceptance is not claimed and remains outside the accepted release scope.
2. **Recovery A12 closed 22 October:** durable recovery fixes, actual process-kill/restart tests and host launch recovery passed. See the follow-up for evidence and user recovery instructions. Physical power-loss certification is not claimed.
3. Clean install/upgrade the candidate on a supported Windows profile; freeze source, assign a new release version and retain package hashes. Test packages retain 0.5.11 metadata and are not published releases.
4. Validate Windows 10, DPI, keyboard/screen-reader use and physical handheld controls; restrict release claims to accepted environments.
5. Configure CI with reviewed/pinned SDK acquisition, dependency inventory and fail-fast tests. `Run-Regression.ps1` supplies the local entry point; hosted CI remains absent.
6. Post-launch: long-history/multi-GiB/disk-full measurements, storage reporting and safe retention; incremental readability improvements in concurrency-heavy code.
7. Optional: reviewed save-directory suggestions and clearer OAuth setup assistance.

No destructive migration, user-data deletion, real payment, production deployment or recurring automation was performed.

## G. Re-audit and continuation checkpoint

Re-review covered both cache-reuse branches and pending uploads, genuine versus temporary package disappearance, JSON read entry-point coverage, running/stopped historical restore and remote consent. The second review found the history/consent issues after the initial remediation. Existing immutable schemas and canonical serialization were preserved, with compatibility tests passing.

Remaining limits: no exhaustive binary dependency scan, provider-wide acceptance, power-loss certification, production-scale soak or physical controller validation. The prioritized acceptance backlog is the next work, not a declaration of production readiness based on local success.
