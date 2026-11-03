# DeskTouch Vision v0.3.9

## Windows Displays
- Show actual Windows computer and Android manufacturer/model names at the top of each screen tile.
- Preserve native Windows `DISPLAYn` numbering per PC; Android screens start their own numbering at 1 instead of sharing a global counter.
- Allow selecting a connected Android phone or Windows PC from the target list, reusing that existing authenticated TLS session. Other peers stay connected.
- Refresh the actual name of a second Windows computer after TLS pairing, rather than displaying an old trusted-device alias.

## Windows → Android (one-way preview)
- After explicit, user-approved Android Accessibility access and an in-app opt-in, the Windows PC can request authority and control the phone over the already paired TLS connection.
- A virtual Android overlay pointer supports pointer motion, taps, swipes and scrolls. Basic text editing is supported in accessible, non-password Android fields; protected system areas and complex shortcuts remain unsupported.
- Android user touch immediately ends the remote lease. Exactly one Windows controller can hold Android authority at a time. Android-to-Windows input is disabled for this mode.
- Latest pointer movement is coalesced to reduce visible delay. The original two-phase authority claim/first-applied-pointer acknowledgement is retained.

## Compatibility and security
- Maintains Windows-to-Windows duplex and multiple trusted TLS peers. Distinct Windows PCs retain individual cryptographic identities; a linked-PC visual group is **not** a transparent routed mesh through another PC.
- Reuses v0.3.8's permanent Android APK signing identity, so a properly installed v0.3.8 can update to v0.3.9 in place.
- No root, unsupported Android global keyboard driver, antivirus exclusion, hidden Accessibility enablement, screen streaming or unauthorized device takeover.
- GitHub CI compiles and tests both platforms. On-device validation with two physical Windows PCs and an Android Accessibility service remains required.
