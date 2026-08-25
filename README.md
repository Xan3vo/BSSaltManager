<div align="center">

# ⬡ BSS Alt Manager

**Run a swarm of Bee Swarm Simulator alts on one PC — each one launched, signed in, and farming with a single click.**

[![Release](https://img.shields.io/github/v/release/Xan3vo/BSSaltManager?style=flat-square&color=568CFF)](https://github.com/Xan3vo/BSSaltManager/releases/latest)
[![License](https://img.shields.io/badge/license-GPL--3.0-568CFF?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-568CFF?style=flat-square)](#install)

</div>

---

Roblox blocks VMs, and AutoHotkey takes over whatever desktop it runs on — so each
alt needs its own Windows session, reached over loopback RDP. Setting that up by
hand is slow and error-prone. **This app does the whole thing:** it builds the
sessions, signs each alt in, joins the game, and auto-starts the macro — then keeps
every window out of your way.

## Install

Download **`BssAltManager-win-Setup.exe`** from the
**[latest release](https://github.com/Xan3vo/BSSaltManager/releases/latest)** and run it.

- ✅ Installs per-user, adds shortcuts, fully self-contained — **no .NET needed**.
- 🔄 **Updates itself.** Checks for new releases on launch and offers to restart into them.
- ⚠️ Not code-signed → first run shows SmartScreen. Click **More info → Run anyway**.
- 📦 Prefer no installer? Grab **`BssAltManager-win-Portable.zip`** instead (doesn't auto-update).

> Runs elevated — it creates local Windows accounts and writes machine policy.

## What it does

| | |
|---|---|
| 🩺 **Health panel** | 11 checks on whether your PC can host concurrent RDP sessions, each with a one-click fix and a plain sentence on what breaks without it. |
| ➕ **Add / adopt alts** | Turns each alt into a hidden local Windows account on its own loopback address, with a generated password and pinned session size. |
| 🔑 **Accounts** | Signs into Roblox through a real browser and keeps the **token, not the password**. Captchas and 2-step just work. |
| 🚀 **One-click launch** | Session up → alt signed in → game (or private server) joined → macro started. Nothing to click in between. |
| 🐝 **Macro** | Installs [Kairos](https://github.com/KairosMacro/Kairos) per alt and patches it to start itself once Roblox is up. |
| 🙈 **Hidden by default** | Windows launch off screen. One on/off button runs each alt; **Show** brings a window up only when you want it. |

## How it works

A few of the decisions that make it reliable:

- **One session per alt, one loopback address each** (`127.0.0.2`, `127.0.0.3`, …).
  Windows stores one credential per host, so alts can't share `127.0.0.1` without
  clobbering each other's saved logins.
- **RDP Wrapper health is the headline check.** `rdpwrap.ini` is keyed to your exact
  `termsrv.dll`; when a Windows update drifts them apart the wrapper silently stops
  patching and a launch hijacks *your* session instead. The app surfaces that by name
  before you launch anything.
- **The saved token never leaves the app.** It buys a single-use launch ticket that
  expires in ~a minute; only that worthless-in-60-seconds ticket crosses into a session.
- **Launches land in the right session** via a per-alt scheduled task with an
  interactive token — no service to install, no stored password.
- **Hidden, never minimised.** Minimising an RDP window makes the client stop decoding
  frames and a pixel-reading macro goes blind; hiding keeps it running at full rate.

Every session setting is pinned (resolution, DPI, hardware rendering) because
pixel- and image-search macros are tuned to one exact size.

## Build from source

```bash
dotnet run --project "src/BssManager/BssManager.csproj"
```

Requires the .NET 10 SDK.

## The macro

The in-game half is **[Kairos](https://github.com/KairosMacro/Kairos)** (AutoHotkey v2,
GPL-3.0). It is **downloaded at runtime, never bundled** — each alt fetches its own
copy, pinned to a release tag, and the app patches only that on-disk copy to auto-start.
Per-alt settings (field, pattern, hive, private server…) are written by the app;
everything else stays in the macro's own window.

## Roadmap

Sessions come up, sign in, join, and the macro starts on its own. Still to come:
**watch & recover** — a heartbeat that spots a crashed client or stalled macro and
restarts it, so an alt dying at 3am doesn't sit idle until morning.

## License

[GPL-3.0](LICENSE). Kairos is a separate GPL-3.0 project, downloaded at runtime — not bundled here.

> **Disclaimer.** Unofficial tool, not affiliated with Roblox or the Bee Swarm Simulator
> developers. Automating gameplay may violate the Roblox Terms of Service and can get
> accounts banned. Use accounts you're willing to lose, at your own risk.
