# Acceptance report — Playnite Link 0.4.0

Windows 11, installed Playnite 10.62, .NET Framework, existing Rclone. Synthetic games and add-on packages were used in separate Playnite profiles. No personal game saves or credentials were copied into fixtures. The installed passcode is preserved.

| Automated group | Expected | Passed | Failed |
|---|---:|---:|---:|
| Ledger and transport | 54 | 54 | 0 |
| Save conflicts, backups, preferences and OAuth protocol | 55 | 55 | 0 |
| Streaming, large saves and device management | 15 | 15 | 0 |
| Passcode persistence and throttling | 9 | 9 | 0 |
| WPF app lock, input and Desktop/Fullscreen theme resources | 15 | 15 | 0 |
| Peer add-ons, opt-in, exclusions, conflicts and rollback | 17 | 17 | 0 |

## Actual Playnite applications

| Run | Result |
|---|---|
| lock-desktop-host | Passed |
| lock-fullscreen-host | Passed |
| desktop-host | Passed |
| fullscreen-host | Passed |
| addon-peer-A | Passed |
| addon-peer-B | Passed |

Two independently configured Playnite instances acted as Desktop and Fullscreen peers. Tests drove real WPF buttons, entered disposable passcodes, launched a real synthetic game process, and applied a real extension DLL and complete theme package through the restart helper. The receiver restarted and loaded the installed extension. Double-clicking Apply was tested; it must produce only one installation/restart. The Fullscreen add-on review was also checked at an 800×640 dialog viewport with its Apply button inside the window.

## Resolution and state tally

| Case | Expected | Actual |
|---|---|---|
| desktop playtime | 100 + 2 sessions = 116 seconds | 116 seconds |
| desktop cancel save conflict | No extra game launch | True |
| desktop choose cloud | Cloud content restored | True |
| desktop choose local | Local content retained; remote branch preserved | True |
| desktop resolved save heads | 1 | 1 |
| desktop tab navigation and draft | Same page controls and unsaved draft retained | True / True |
| desktop game manager | Opens selected game controls without losing account page | True |
| desktop quiet idle save scans | 0 | 0 |
| desktop warm idle check | Responsive UI, no progress dialog | 489 ms on local Rclone alias |
| fullscreen playtime | 100 + 2 sessions = 116 seconds | 116 seconds |
| fullscreen cancel save conflict | No extra game launch | True |
| fullscreen choose cloud | Cloud content restored | True |
| fullscreen choose local | Local content retained; remote branch preserved | True |
| fullscreen resolved save heads | 1 | 1 |
| fullscreen tab navigation and draft | Same page controls and unsaved draft retained | True / True |
| fullscreen game manager | Opens selected game controls without losing account page | True |
| fullscreen quiet idle save scans | 0 | 0 |
| fullscreen warm idle check | Responsive UI, no progress dialog | 470 ms on local Rclone alias |
| Add-on concurrent edits | Two branches; explicit choice; one surviving head | Passed; discarded package retained in backup |
| Independent add-on installs | Union of both peers | Passed |
| Shared add-on removal | Code backed up, extension data retained | Passed |
| Active theme removal | Refuse until a different local theme is chosen | Passed |
| Device-only package | Neither publish nor apply remote changes | Passed, including restart persistence |
| Corrupt staged package | No installed files modified | Passed |
| Mid-install failure | Roll back earlier replacements; retry succeeds | Passed with a failure injected into a test-only copy of the helper |

## Existing Google Drive account

Dedicated synthetic acceptance folders were used on the existing Google Drive account. Each large file was 40 MiB + 13 bytes; two identical files formed an 80 MiB + 26-byte logical snapshot and shared a blob. Upload, restore, SHA-256 checks, save-conflict resolution, and preservation of the displaced local save in its backup completed.

**Full live-cloud acceptance passed using the owner's personal OAuth client.** The earlier shared Rclone OAuth project returned HTTP 403 RATE_LIMIT_EXCEEDED. A separate personal-client configuration removed that dependency. The combined save/conflict/device/playtime suite passed, including two devices, remote configuration, pause/resume/removal, and exactly 140 seconds of converged playtime after retries. Actual Desktop and Fullscreen Playnite peers published and received an extension and theme over Drive; the recipient installed, restarted, and loaded the extension. Local settings and active theme selection were retained. Forced OAuth token refresh also passed. Historical quota-failure evidence remains separately recorded.

## Visual review and practical limits

Captured actual WPF pages: compact lock, Overview, Devices, Protection, Add-ons, settings, game manager, playtime controls and all three save-conflict choices. Desktop and Fullscreen colour resources were reviewed; missing Fullscreen brushes, low-contrast fields and early lock-window theme loading were corrected. Tab switches preserve page instances and drafts, and Sync now does not close the account window. A visible Close action is available in Fullscreen.

This is software testing on one Windows computer with isolated profiles, not a physical ROG Ally/gamepad test. Real commercial-game save layouts have not been exercised here. Fresh browser OAuth enrollment and a forced refresh were exercised for the personal client; OAuth state handling, cancellation and masked secrets are also covered by protocol tests. Settings synchronization remains out of scope at the user's request; active theme, launch mode, Windows startup choices, extension settings and credentials remain local.

## Personal Google Drive connection — October 20, 2026

The owner authorized a personal Google OAuth Desktop client and a separate Rclone configuration. Google now shows **In production**. Authorization was renewed after publishing, and a forced token refresh passed. This removes the testing-mode one-week grant restriction; normal Google revocation and inactivity rules still apply.

Playnite Link now points to the personal configuration while retaining `gdrive:PlayniteLink`. The previous and new connections resolved to the exact same Drive folder ID, and 37 known device-history objects matched. The original Rclone config and app passcode were preserved; connection settings were backed up before a graceful restart.

The full cloud suite passed in 216.36 seconds: a 40 MiB + 13-byte file restored twice in an 80 MiB + 26-byte logical snapshot, SHA-256 checks, deduplication, concurrent save branches and backup, two devices, remote per-device preferences, pause/resume/removal, and 140 seconds of converged playtime. Two actual Playnite applications separately shared an extension and theme through Drive; the Fullscreen recipient installed, restarted, and loaded the extension. All settings and active theme selection remained local. Screenshots were visually inspected.

Google requires public app-information links for production OAuth. Only the approved homepage and privacy notice were deployed on the owner's existing free Netlify team at https://fluffy-florentine-e76038.netlify.app/. These pages do not host saves or OAuth tokens. No application backend or paid hosting upgrade was created. The Google project is linked to the owner's pre-existing billing account; no paid quota increase or hosting resources were enabled.

## Installed profile follow-up

Save Manager was still reading the original shared-client Rclone config and blocked startup with its own cloud error. The existing `gdrive` remote was therefore also migrated to the same verified personal client, with an exact timestamped original-config backup. Remote options and vault location were retained. On the next graceful restart, Save Manager's startup progress completed in approximately six seconds and Playnite reached Application started without the previous error dialog. The installed device-history directory passed a one-way Rclone content check against Drive. The passcode file hash remained identical to its pre-migration backup.

Native capture of the normal-profile window was unreliable (disabled accessibility root and unavailable/black capture). No visual success is claimed for that final normal-profile window. Visual verification and installation/restart assertions passed in the two isolated actual Playnite instances; their screenshots are retained. This does not represent physical ROG Ally hardware testing or a guarantee against future provider outages.
