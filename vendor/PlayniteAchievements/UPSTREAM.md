# Built-in achievement component

Original project: https://github.com/justin-delano/PlayniteAchievements

Pinned release: v4.1.1, MIT, copyright 2026 Justin Delano.

The unmodified release PEXT is retained as build input. SHA-256:
`8005864bbc9d5512cf26b8d24dac9e68f288f4606a9342d823c67d6d21c36692`.

Build extraction renames its `extension.yaml` to `engine-manifest.yaml` so Playnite does not install/register a second add-on. Link constructs the engine in its own process, forwards Playnite lifecycle events, supplies the engine's original views/providers/settings and registers StartPage support. An API adapter routes native achievement settings requests into Link's settings host. Platform credentials remain in the local engine data directory; they are never cloud-synchronized.

`Source-4.1.1.zip` contains the release's source, tests, tools and root documents. Documentation screenshots/branding galleries are omitted. Code and build resources are retained. Its SHA-256 is `bf029aa6aabcd5a0084c9ecda628c9952f14be0a65dbb4e864cd8e9572a284ae`.

The original complete source archive is available at https://github.com/justin-delano/PlayniteAchievements/archive/refs/tags/v4.1.1.zip (downloaded SHA-256 `99d66aee1ea725fa66d2c72211a1963dda2be541ca885ae5a01dffa603038ce1`).

Upstream test validation uses the release sources, SDK 6.14.0 and the release DLL for pack resources. Windows source-inspection tests require CRLF line endings. After normalizing line endings in the isolated test checkout, 4,981 passed, one skipped, zero failed. This result does not claim live account authentication/provider acceptance on every service.
