# Built-in achievements and Playnite Link sidebar

Playnite Link now has a sidebar dashboard inside Playnite, with live sync status, sync/pause controls, games/saves/session history, backups, achievements, connected devices and diagnostics.

The original Playnite Achievements 4.1.1 engine is included inside Link. Its provider authentication, refresh, views, settings, manual tracking, custom achievements, friends, scores, notifications, capture and theme/StartPage support are available without installing another achievement extension. Provider credentials stay local; sign into each service independently on each device.

Linked games share provider catalogs, unlocks/dates, compatible progress, points/rarity/icons and portable per-game customizations, including notes, goals and deliberate manual relocks/deletions. Refreshes and edits queue background sync. Account/provider/game boundaries remain separate. Concurrent customization edits preserve both versions for review. Capture media, friend caches and machine-specific appearance/notification/capture preferences remain local.

Existing supported standalone achievement code is safely preserved outside Playnite's active extensions after shutdown; its data is retained. Unknown/newer versions are preserved for review. No migration wizard or separate achievement installation is required. Install 0.7.0 on participating devices and use the same cloud game identity and personal vault. This update retains native sessions, HLTB progress and the Save Manager backup core.

Validation: original engine suite 4,981 passed, one skipped; actual Playnite Desktop and Fullscreen 82 checks each; two independent profiles plus a restarted returning profile 30 checks; synthetic metadata/customization/blob transfer through the user's authorized Google Drive. Local regression and helper results accompany the acceptance report. Tests do not certify a physical ROG Ally run, all services' live authentication/gameplay unlocks, or shared capture media. See ACCEPTANCE-0.7.0.md for the precise coverage.
