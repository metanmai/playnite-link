# Reuse and dependencies

This combined distribution is licensed under GNU AGPL-3.0 (LICENSE).
Save Manager by Jurangren, pinned at c879d81bd864b23ab903dd2a62ecf30063bda518,
is included as source in vendor/SaveManager and as SaveManager.Core.dll.
See vendor/SaveManager/UPSTREAM.md for provenance and integration details.
Original Link MIT terms are preserved in vendor/PlayniteLink-MIT-LICENSE.

Microsoft.Toolkit.Uwp.Notifications 7.1.3 (.NET Foundation and contributors), MIT,
is bundled for the reused Save Manager Windows notification service. Its notice
is included as Notifications-LICENSE.md. Build downloads the pinned NuGet package
and verifies SHA-256 B328139AD101CCB810E9D8741E9ABF0B3F71F9D70397BDFEF6668EBD9BCD6C5E.

Playnite Link adapts the user's earlier PlaytimeRclone project (MIT): ledger, Rclone transport, settings, enrollment, recovery and test harness. Original license retained in `vendor/PlaytimeRclone-LICENSE`. The internal namespace remains `PlaytimeRclone` to keep the reused engine and tests compatible.

- [Playnite SDK](https://api.playnite.link/docs/tutorials/extensions/plugins.html), MIT. Referenced from the installed Playnite; not redistributed in the extension.
- [Newtonsoft.Json](https://www.newtonsoft.com/json), MIT. Uses the copy supplied by Playnite; not redistributed in the extension.
- [Rclone](https://rclone.org/licence/), MIT. An external executable, not bundled in the extension. An existing runtime is reused or official v1.75.2 is downloaded and checked against its published SHA-256. Its README accompanies the downloaded executable. Rclone handles provider OAuth, TLS, retries and optional crypt storage. The extension uses its documented argv interface and [configuration continuation protocol](https://rclone.org/commands/rclone_config_create/).
- [Syncthing](https://syncthing.net/), MPL-2.0. Optional external peer transport for a local immutable vault directory, not linked or bundled. No Syncthing code was copied.
- [GameActivity](https://github.com/Lacro59/playnite-gameactivity-plugin) and [HowLongToBeat](https://github.com/Lacro59/playnite-howlongtobeat-plugin), MIT. Optional installed add-ons accessed through their runtime model/database APIs. Their source was consulted to verify compatibility; no add-on binary or credential is bundled in the extension.

AuthService at `/home/metanmai/Code/auth-service` was inspected. Its mobile/Supabase authentication runtime is not a dependency: the user explicitly requested personal-drive ownership with no centralized account authority. Storage-provider sign-in replaces an application account server.

No Rclone OAuth credentials, SDK assemblies, test companion, user saves or fixture accounts are included in the release package.
