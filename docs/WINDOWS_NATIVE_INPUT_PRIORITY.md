# DeskTouch Vision — Windows-native input priority policy

**Windows-first feature branch:** `feature/native-lan-kvm`. Android and webcam-based disconnected peripherals remain on hold. **Not yet released.**

## Core rule

Windows always owns its real hardware keyboard, touchpad, and externally attached mouse/keyboard. DeskTouch **never disables those drivers**. Desktop device sharing is an **optional, secondary input path**.

- Receiver computer detects locally originated physical keyboard and mouse input (including devices integrated into a laptop).
- Real local activity **immediately revokes** an active remote-control grant, releases any remotely held modifier keys and mouse buttons, and informs the source to restore its own cursor.
- Actual activity, not mere device presence, determines priority. A receiver without any connected keyboard/mouse can be controlled through the same workflow.
- After local activity stops, a brief **2,000 ms inactivity gate** permits a new *explicit* edge-handoff request. Remote control is never silently reacquired just because the receiving computer went idle.
- Input stays local if receiver denies a claim, is offline, loses connection, or sees a Windows display topology change.
- Synthetic input from DeskTouch must not count as local physical activity: Windows Raw Input identifies device-originated events; native low-level hooks ignore injected keyboard and mouse events.
- No keylogging: neither raw keystrokes nor device identifiers are stored in diagnostics; input is forwarded only during user-requested sharing.

## Windows multiple monitors

Preserve the existing Windows extended desktop on **every** computer. Walking over native monitor seams remains ordinary Windows pointer movement. Switching to another computer is allowed only at the exposed external edge and maps to an actual monitor on the other computer. Monitor changes trigger a safe disconnect/re-pair.

## Important ambiguity and UI behavior

When PC A's keyboard is the **shared source keyboard** and the cursor controls PC B, ordinary typing on PC A is by design sent to PC B. There is no reliable way to infer from these keystrokes alone whether the person intends to type on PC A instead. To take back control of PC A, the user moves across the return edge, uses **Ctrl+Alt+Shift+F12**, or disconnects. However, physically typing on PC B's OWN built-in/connected keyboard unambiguously signals native PC B ownership and preempts DeskTouch.

## Two-PC handoff acceptance criteria

1. PC A at right edge requests control of PC B after PC B has been idle >=2 seconds.
2. If PC B user is still typing or moving its own mouse, request is denied and PC A remains local.
3. When PC B user presses any key while DeskTouch is active, PC B wins and PC A receives a revoke notice; held remote modifier/mouse keys are released.
4. PC A cannot silently retake PC B while PC B is locally active; a new right-edge movement is required after local input is idle.
5. No changes to local keyboard/mouse hardware behavior, Windows display arrangement, or network routing.
6. End-to-end two-real-PC testing is mandatory before installer release. The automated tests only cover policy invariants and compilation.
