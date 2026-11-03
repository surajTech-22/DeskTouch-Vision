# DeskTouch Vision v0.3.12 — single shared environment (development, not released)

## Real multi-device environment
- Replaces the one-to-one **controller-target connection model** with a Windows **environment hub**, to which multiple Windows PCs and Android phones can join and remain online together.
- A phone establishes **one authenticated TLS connection to the hub**, not one connection to each Windows PC. Each other device independently connects to the same hub; it is not necessary to pair every possible device-to-device combination.
- The hub publishes a live, authenticated membership roster of connected PCs/phones and screen geometry. The Windows and Android clients select the destination from this roster over their **existing** connection.
- Input for another environment member routes through the hub to that member's existing session. No new TCP connection is created when the user changes destination.
- The Android LAN discovery prefers a **hub** rather than an already joined PC, avoiding an accidental split environment. Windows **Add devices** no longer asks to disconnect an existing controller and replace it with another 1:1 connection.

## Keyboard, mouse and authority
- Routed mouse, scroll, clicks, Unicode text and key events use the same authenticated DeskTouch protocol and an explicit per-destination input claim.
- The hub relays the **actual destination's** grant and first-applied-pointer acknowledgement before the controller starts sending keyboard and clicks.
- Only one controller may own a destination at a time; other participants remain connected and may control different destinations simultaneously.
- The local physical mouse/keyboard always takes priority at the destination and revokes the remote lease.
- The hub tracks its **own** direct control leases alongside relayed leases to prevent two controllers from controlling the same phone or PC.
- If a target disconnects, its lease is removed, other clients remain online, and clients receive an updated member list.
- Existing coalesced mouse transport, Windows native sensitivity scaling and Android touchpad sensitivity are retained.

## Pairing / permission boundaries
- All participating devices must be explicitly approved by **their hub** once, with existing certificate pinning and persistent cryptographic trust. A mere LAN broadcast cannot authorize input, impersonate a member or grant a lease.
- The shared environment does not eliminate authenticated device-to-hub transport. It eliminates separate user-facing **device-pair** sessions and duplicate Android connections.
- Windows→Android remote gestures/typing still require Android Accessibility and in-app permission. Android's physical touch may revoke remote control. Android→Windows phone typing uses the on-screen keyboard input field or hardware keyboard and does not require Accessibility.
- The current Windows hub supports **up to eight simultaneously authenticated incoming peers**. This is a documented bound, not a claim of unlimited connections.
- The hub must be reachable on the same permitted private LAN. This revision is one hub with attached members, not a mesh of connected hubs across multiple networks. It does not stream screens or provide unrestricted hardware-level Android key injection.

## Verification and release policy
- Windows CI now includes a **three-peer live TLS integration test** for roster visibility, selecting a member through the hub, competing input claims, release, and destination physical-input revocation.
- Android CI covers hub-role discovery, packet allowlists and builds the debug APK.
- Automated tests cannot prove real-world latency, focus/elevated-app behavior, Accessibility text insertion or multi-monitor physical edge crossing. Test with two actual Windows computers and multiple Android phones before releasing.
- Keep the **existing permanent Android signing identity**; never publish debug-signed APKs as production updates.
- This branch is a development preview and must **not** deploy through release auto-updates until end-to-end acceptance passes.
