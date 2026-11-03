# DeskTouch Vision — Android updates and persistent signing

DeskTouch Vision for Android is part of the **same repository** and connects to
the existing Windows DeskTouch sharing system.

## Update behaviour (v0.3.6+)

- **Get updates** checks the official GitHub `releases/latest` API.
- **Auto-update** checks approximately every 6 hours while the Android
  `connectedDevice` service runs, downloads the newer APK into the private
  app cache, and verifies its SHA-256 against the asset in the same release.
  It posts a notification. Manufacturer battery restrictions or lack of
  network access can delay checks.
- **Update existing application** verifies the downloaded APK's package name,
  signature and SHA-256, then opens **Android's own package installer**.
  Android requires confirmation; background silent installation is not
  possible for a normal third-party sideloaded app.
- Updates preserve existing app data **only if the APK is signed by the same
  publisher signing certificate**. The updater rejects changed signing keys
  rather than bypassing Android package security.

### One-time signing migration required

Previous GitHub preview APKs (up to v0.3.5) were **ephemerally debug-signed**.
GitHub Actions runners do not share that signing key. Therefore v0.3.6 may be
unable to update v0.3.5 **in place**. No source-only change can recover a private
signing key that was not retained. If Android says the signatures differ, a
one-time manual uninstall/reinstall may be needed, which loses locally encrypted
trusted-device tokens; pair trusted devices again.

For all **future** releases to update the existing application, maintain a
stable private signing key:

1. On an offline, trusted development PC, create a private Android signing
   keystore using Android Studio or `keytool` and record the alias and
   passwords securely. Never check the keystore or passwords into GitHub.
2. In GitHub repository **Settings → Secrets and variables → Actions**, create
   these four **Repository secrets**:
   - `DESKTOUCH_ANDROID_KEYSTORE_BASE64`: base64 encoding of the keystore
     (single line, no shell transcription into the repository)
   - `DESKTOUCH_ANDROID_STORE_PASSWORD`
   - `DESKTOUCH_ANDROID_KEY_ALIAS`
   - `DESKTOUCH_ANDROID_KEY_PASSWORD`
3. Publish a new Android APK through the official GitHub Actions workflow.
   With all four secrets configured, CI uses the persistent key to sign the
   release build. Without them, CI intentionally falls back to a labeled
   **debug-preview** APK; it is not guaranteed to upgrade the previous APK.
4. Back up the keystore outside GitHub. Loss of the private signing key means
   the next APK cannot be installed over the old one without a signing-key
   rotation supported by Android's verified signing lineage.

Do not commit a reusable debug signing key as a shortcut. Do not disable
Android package signature checks or antivirus. Package names, credentials and
release integrity must remain authenticated.

## Multi-device networking

- The Windows receiver accepts up to **8 simultaneously authenticated**
  Windows/Android connections over the existing TLS protocol (TCP 37377).
- Android maintains several distinct Windows sessions, discovered by
  cryptographic TLS fingerprint rather than saved IP address.
- Many devices can remain connected concurrently. **Only one remote source
  can own a Windows PC's keyboard/mouse input lease at a time**, with the
  local physical keyboard/mouse retaining priority; this prevents contradictory
  key/button injections. The Android user selects the active Windows target.
- A PC also remains discoverable while initiating an outgoing session. Windows
  supports one selected outgoing controller; inbound sessions stay online.
- Android-to-Android global cursor injection is unavailable to an ordinary
  Android app; discovery is not equivalent to that permission.

### Validation limits

GitHub Actions unit tests, Windows loopback multipoint TLS test and APK builds
do not reproduce OEM Android background restrictions, every physical input
device, or every LAN router's behavior. Physical phone and two-PC validation
is needed before claiming complete end-to-end success.
