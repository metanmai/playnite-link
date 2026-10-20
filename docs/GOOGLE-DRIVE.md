# Connect Google Drive

Playnite Link currently uses a client that **you own** for a new Google Drive connection. Reusing an existing authorized Rclone remote is also supported. The extension does not ship the developer's personal OAuth client or tokens.

1. Follow Rclone's official [Making your own client ID](https://rclone.org/drive/#making-your-own-client-id) instructions. Create a Google Cloud project, enable the Drive API, configure the OAuth consent screen and create a **Desktop app** client.
2. Download the client JSON to your computer. Keep it local; do not put it in a shared vault, issue or source repository.
3. In Playnite Link, choose a new **Google Drive** connection and select that client JSON.
4. Complete Google's account authorization in your browser. If your app is in testing, your account must be a test user. Google's testing-mode authorization expiry can require reconnecting; Rclone documents production/verification considerations.
5. Connect to the default `PlayniteLink` folder or a dedicated vault folder of your choice. On your other device, use the same OAuth app/storage account and the same vault folder.

Google controls OAuth consent, verification and API quotas. This extension cannot bypass these requirements or guarantee quota availability. It does not require a hosted application backend or a Playnite Link subscription. Your cloud provider's storage limits/pricing still apply.

The current Rclone Drive connection requests broad Drive access. The extension directs its operations to the configured vault, but that permission itself is broader than one folder. You can inspect and revoke it in your Google account. Credentials and tokens remain in the local Rclone configuration.

A future shared public Google OAuth connection requires its own consent/verification work. The current release does not advertise one-click Google onboarding without a personal client.
