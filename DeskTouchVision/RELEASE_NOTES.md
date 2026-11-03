# DeskTouch Vision v0.3.10

## Android network grouping
- Interconnected Windows computers appear as **one linked Windows network card**, showing member names. The previous UI showed a group heading and then misleading separate Connect buttons for each member.
- A connected or previously trusted Windows PC is chosen as the gateway. The phone does not need a new connection just to show or select the group.
- Trusted-device auto-reconnect connects to **at most one gateway per discovered linked group**, instead of silently opening separate phone connections to each linked PC.
- Existing per-device TLS fingerprints remain separate. A group label is **not an authenticated proxy or keyboard relay**. Truly controlling a different Windows PC from the phone via the gateway is not part of this release.

## Keyboard input checks
- Windows retries valid virtual-key `SendInput` injection if scan-code injection fails, without logging keystrokes.
- When a receiving Windows PC rejects a keyboard injection, DeskTouch reports the delivery failure to the sending PC, with guidance to check application focus and elevation.
- Independent native Windows keyboard/mouse authority and the original PC-to-PC encrypted session remain intact; no duplicate session is required simply to view the linked network.

## Android permissions
- To enable **Windows → Android** control, explicitly enable DeskTouch's **Windows control** toggle and Android's `DeskTouch Windows control` Accessibility service. Android 13+ may require allowing restricted settings for sideloaded apps.
- Notifications are optional for status alerts. Background/connected-device service needs normal Android foreground-service permissions and may be subject to OEM battery controls. Root, device admin, contacts, SMS, and full storage permissions are not required.

## Important boundary
- **The Windows-to-Android one-way mode remains unchanged.** The phone does not send global keyboard/mouse control to Windows PCs.
- Two PCs connected through DeskTouch do **not** automatically form an input-routing proxy. Mouse crossing the two PCs on a Windows-to-Windows link does not prove that Android keyboard traffic can reach either PC through the other. Cross-PC keyboard relay from one Android gateway requires a separate authorized multi-hop routing design and is not claimed complete.
- Private signing key/certificate remains the pinned v0.3.8 permanent Android identity; future APK updates remain in-place.
- CI tests cover linked-group selection, single-gateway routing decisions, input policy and packaging; physical Windows/Android acceptance is still required.
