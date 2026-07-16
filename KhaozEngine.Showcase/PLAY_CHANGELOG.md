# Showcase - Player Changelog

---

## 2026-07-16

### Build 0.4.0 (Alpha 4)

- **Major**
  - The whole showcase got a facelift. The menu is now a tile grid with a one-line description per room, so you can tell what each demo shows before you enter it.
  - The old `2D sprites`, `GUI widgets`, and `Input` rooms merged into one `2D & GUI` room with tabs, so the whole 2D toolkit is one visit instead of three.
  - Every room now shows its name in the top corner and its controls along the bottom, and the 3D rooms pop a small on-screen note when you flip a render toggle, so you can finally tell what those keys did.
- **New**
  - A `Toast stack` demo joined the `Screens & dialogs` tab: fire standard, warning, danger, sticky, and self-updating toasts, and tap them to dismiss.
- **Bug**
  - The `Catcher` mini-game field now fills the window instead of stopping short of the right and bottom edges.

---

## 2026-07-10

### Build 0.3.0 (Alpha 3)

- **New**
  - Added the `Patch Notes` panel to the Gui room so you can review every change without leaving the menu.
- **Major**
  - The `Widgets` screen now uses a real scrollable list instead of a fixed page, so the demo holds as many rows as you like and every one of them stays reachable no matter how small the window gets, which used to cut the bottom rows off completely on a laptop sized display and made half the point of the demo invisible unless you resized the window first.

### Build 0.2.1 (Alpha 2)

- **Bug**
  - Fixed the `Overlay demo` pause screen not resuming when `Resume` was clicked twice in quick succession.

---

## 2026-07-05

### Build 0.2.0 (Alpha 2)

- **Minor**
  - `Immediate` screen buttons now highlight on hover for clearer feedback.
- **Rebalance**
  - Tuned the `Tooltip` hover delay on the `Widgets` screen so it appears sooner.
