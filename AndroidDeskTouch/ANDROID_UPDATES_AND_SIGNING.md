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

### First permanent signing release: v0.3.8

**The existing Android package ID remains `com.desktouch.vision`.**

The first permanent DeskTouch Android signing identity was created on
**2026-11-03**, and is supplied to the repository owner separately
via a **private, password-protected PKCS12 keystore**. It is never committed
to GitHub or included in a public APK/release asset.

Public certificate SHA-256 fingerprint:

```
56:B2:E0:07:8F:8D:7B:07:AB:21:C6:E6:3F:12:58:62:2F:DE:A0:81:BA:C8:36:98:D0:2F:11:BD:4E:66:C9:C6
```

The GitHub Actions release job **pins this public certificate**. It refuses to
publish an APK if the signing keystore is absent or has a different certificate.
Branch/pull-request CI can still build **debug artifacts for tests only**.

**One-time owner setup:**

1. Download the privately supplied `DeskTouch_Android_Permanent_Signing_Setup.zip`
   and the **separate private password file** in the ChatGPT conversation.
2. Extract the ZIP on a trusted Windows computer. Install GitHub CLI
   (`winget install --id GitHub.cli --exact --source winget`) if needed.
3. Launch `SETUP_SIGNING.cmd`. Log in to GitHub in your browser if prompted.
   It uploads the **same** keystore and password as **encrypted repository
   Actions secrets** (no secrets in Git history):
   - `DESKTOUCH_ANDROID_KEYSTORE_BASE64`
   - `DESKTOUCH_ANDROID_STORE_PASSWORD`
   - `DESKTOUCH_ANDROID_KEY_ALIAS`
   - `DESKTOUCH_ANDROID_KEY_PASSWORD`
4. After all four secrets are configured, merge the staged v0.3.8 release PR.
   The regular Windows release workflow automatically triggers an Android
   release build; only its permanently signed APK is attached to the release.
5. On Android, the installed v0.3.7 **debug-signed** APK does not have the
   same certificate. It **cannot be updated in place** with the permanently
   signed v0.3.8 APK. A **one-time manual uninstall and reinstall** is
   required if Android rejects installation. That clears Android app data,
   including encrypted trusted-device pairing records. Pair PCs again.
6. **All releases after v0.3.8** must use the same permanent certificate.
   Future updates can be installed over v0.3.8 through **Get updates**,
   **Update existing application**, or the update notification after the
   APK's hash and signing identity have been verified. Android still requires
   normal system installation approval; this is not silent installation.

**Keep two independent, secure backups of the keystore and its password.**
Store them separately, preferably with the password in a password manager.
Loss of the private signing identity prevents normal in-place upgrades.
Never share either private file or password in issues, pull requests, public
release assets, chat, or logs. Only the **public fingerprint** above belongs
in this repository.

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
