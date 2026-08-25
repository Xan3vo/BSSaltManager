# BSS Alt Manager

Manages the local RDP sessions that BSS alts run in.

Roblox blocks VMs, and AutoHotkey takes over whatever desktop it runs on — so the
usual answer is a second Windows session per alt, reached over loopback RDP with
RDP Wrapper lifting the one-session-at-a-time limit. That setup works, but every
step of it is manual. This app does the setup and the launching.

**Scope: from an empty machine to alts farming on their own.** It creates the
sessions, tells you when the host is misconfigured, holds the Roblox logins,
signs an alt in inside its own session, joins the game, and installs and
auto-starts the macro — so a single Launch takes an alt all the way to playing,
with nothing to click in between. What is still manual is noticing when one
dies and bringing it back — see [Not built yet](#not-built-yet).

## What it does

- **Health panel.** Eleven checks on whether this machine can host concurrent
  sessions at all, each with a one-click fix and a plain sentence about what
  breaks if you leave it. Details below — this is the part that saves the most time.
- **Add alt.** Creates a local Windows account, generates its password, puts it in
  Remote Desktop Users, hides it from the sign-in screen, saves the credential so
  login is unattended, and writes a `.rdp` file with the session size pinned.
- **Adopt alt.** Point it at an account you already made by hand; it rotates the
  password to a generated one and wires up the rest.
- **Launch / Launch all.** Opens sessions, staggered, because starting several at
  once makes Windows fight itself and some connections time out.
- **Live status.** Every alt shows Running / Detached / Not started, read from the
  Terminal Services session table every 3 seconds.
- **Repair.** Re-applies the account, group membership, credential and `.rdp` file
  for an alt that was changed outside the app.
- **Accounts.** Captures a Roblox login by signing in through a real browser and
  keeps the token rather than the password. See [Accounts](#accounts).
- **Sign in.** Opens Roblox inside an alt's session, signed in as its account, and
  joins the game — automatically after a launch. See
  [Signing an alt in](#signing-an-alt-in).
- **Macro.** Installs [Kairos](https://github.com/KairosMacro/Kairos) per alt,
  writes its per-alt settings, and patches it to start itself once Roblox is up —
  no keypress in the session. See [The macro](#the-macro).
- **Hidden by default.** RDP windows launch off screen and never pop up; one
  on/off button per alt runs the session, and Show brings its window up only when
  you ask. See [Hiding the windows](#hiding-the-windows).

## Download

Grab **`BssAltManager-win-Setup.exe`** from the
[latest release](https://github.com/Xan3vo/BSSaltManager/releases/latest) and run it.
It installs per-user (no admin needed to install), adds a Start Menu and desktop
shortcut, and launches. Self-contained — no .NET install needed. Windows 10/11 x64.

**It updates itself.** On each start the app checks GitHub for a newer release in
the background; when one is out it downloads quietly and offers to restart into it.
No reinstalling.

The app is not code-signed, so the first run shows a SmartScreen "unknown
publisher" prompt — choose *More info → Run anyway*. It also requests elevation on
start, because it creates local accounts and writes to HKLM, neither of which works
unelevated.

Prefer no installer? A **`BssAltManager-win-Portable.zip`** is on the same release —
unzip and run `BssManager.exe`. The portable copy does not auto-update.

## Running from source

```bash
dotnet run --project "src/BssManager/BssManager.csproj"
```

Requires the .NET 10 SDK.

Config, generated `.rdp` files and the log live in `%APPDATA%\BssManager`.

## The health checks

The one that matters most is **rdpwrap.ini supports this Windows build**.

`rdpwrap.ini` is keyed on the exact version of `termsrv.dll`. Windows updates that
file; the ini does not follow. When they drift apart the wrapper silently stops
patching, and the failure is nasty precisely because it isn't loud — connecting an
alt takes over *your* session instead of opening a second one, and nothing anywhere
says why. Surfacing that by name, before you launch anything, is most of this app's
value today.

The fix button runs RDP Wrapper's own `autoupdate.bat` rather than reimplementing
offset discovery. That script can generate entries for a build nobody has published
offsets for yet, by downloading Microsoft's symbols for your `termsrv.dll` — so it
often works even when the ini is far behind.

The rest: wrapper installed, TermService pointing at it, service running, RDP
enabled, multiple-sessions-per-user allowed, minimised sessions still rendering,
and something actually listening on 3389.

### Skipping the Windows first-sign-in screens

A brand new local account signing in for the first time gets the privacy-settings
pages and "Let's finish setting up your device". They open inside the RDP window
and sit there until a human clicks through, which defeats the point of launching
an alt unattended. The **New alts skip Windows setup screens** check turns them off.

Two mechanisms, because the settings live in two scopes:

- **Machine policies** (`DisablePrivacyExperience`, `EnableFirstLogonAnimation`,
  the CloudContent and Edge first-run policies) apply to every account at once.
- **Active Setup** handles the per-user half. Windows runs its command once per
  user at first sign-in, *as that user*, so it can write `HKCU` directly.

Active Setup is used rather than editing `C:\Users\Default\NTUSER.DAT` because
hive loading needs SeBackupPrivilege and SeRestorePrivilege, which sit disabled
even in an elevated token, and which a child process like `reg.exe` cannot
inherit as enabled. On this machine hive loading still fails with
`ERROR_INVALID_PARAMETER` after enabling both in-process, cause unknown. The code
attempts it anyway and treats failure as non-fatal — Active Setup already covers
the same ground, and covers existing profiles too.

Two things this does **not** do. "Preparing Windows" during profile creation is
unavoidable and takes a few seconds the first time each alt signs in. And the
CloudContent policies are machine-wide, so your own account also loses Windows
Spotlight and suggested content. To undo that part, delete
`HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent`.

### Session tuning and Roblox

The same first-logon script strips the alt's desktop back to what a macro needs:
visual effects set to best performance, transparency and window animations off,
zero menu delay, Game Bar and background capture off, and no OneDrive setup
prompt. All per-user, all applied before the desktop appears.

Two machine policies matter more than any of that:

- **`bEnumerateHWBeforeSW = 1`** tells Remote Desktop sessions to use the
  hardware graphics adapter. Without it a session can fall back to the software
  renderer, and Roblox crawls no matter how fast the machine is. This is the
  single biggest win for running a game over RDP.
- **`AllowGameDVR = 0`** kills background game recording, which is on by default
  and costs frames for nothing.

The `.rdp` files also drop every "make the stream cheaper" feature — compression,
multimedia redirection, cursor shadows. Those trade CPU for bandwidth, and over
loopback bandwidth is free.

**Roblox installs itself.** The app stages the official installer once into
`C:\ProgramData\BssAltManager`, and each alt installs it on first sign-in. It runs
from `RunOnce`, not from Active Setup, because Active Setup blocks the logon
screen until it returns and installing Roblox takes minutes. Roblox lives in
`%LOCALAPPDATA%`, so every alt genuinely needs its own copy; the staged file is a
bootstrapper, so each alt still pulls the current client build itself.

The logon script guards on `alts.txt`, a list this app keeps in step with your
alts. Active Setup runs for **every** account including yours, so without that
guard it would strip the visual effects off your own desktop too.

### Keeping the host's startup apps out of alt sessions

Anything the machine starts for *all* users starts in every alt session too —
Cloudflare WARP, RGB utilities, vendor tray apps. They steal focus, cost memory
and occasionally throw dialogs over the macro.

The obvious fix does not work. Windows records "this startup item is disabled"
for all-users entries in **HKLM**, not HKCU — verified on this machine, where
`Cloudflare WARP.lnk` and the four all-users `Run` entries all keep their state
under `HKLM\...\Explorer\StartupApproved`. Disabling one there disables it for
the real user too, and deleting the entry outright is worse. There is no
supported per-user switch for a machine-wide startup item.

So alts close them instead. `suppress-startup.cmd` runs at every alt sign-in
from the alt's own `Run` key and sweeps for ~40 seconds, closing anything listed
in `blocked-apps.txt`. It sweeps repeatedly rather than once because these
programs start at their own pace and some relaunch themselves once. A `.vbs`
one-liner launches it hidden, so no console flashes in the session. It carries
the same `alts.txt` guard as everything else.

#### The list is discovered, not hardcoded

Nothing here is specific to one PC. On every machine the app scans three
sources and builds the list from what it actually finds:

- the all-users `Run` keys, 64-bit and 32-bit
- the all-users Startup folder, with shortcuts resolved to their real target
- **logon-triggered scheduled tasks**, read through the Task Scheduler COM API
  rather than by parsing `schtasks.exe`, whose output is localised and would
  break on a non-English Windows

That third source matters more than it sounds. On the development machine the
Run keys held four entries, while the scheduled tasks held twelve more —
browser updaters, OneDrive launchers, vendor agents. A scan that only looked at
Run keys would miss most of what actually starts.

The list is refreshed **every time the app opens**, so a program installed since
last time is picked up without anyone remembering to press anything.

#### Two lists

- `blocked-apps.txt` is generated. It is rewritten on every scan, so edits to it
  are lost.
- `blocked-apps-custom.txt` is yours. The app creates it once and never touches
  it again. Put your own additions there.

#### The safety list

Some executables are never blocked no matter what launches them: the shell and
session processes, generic hosts and tools (`rundll32`, `regsvr32`, `sc`, `net`,
`reg`, `schtasks`…), the scripting hosts the sweeper itself runs on (`cmd`,
`wscript`, `ping`), and anything Roblox.

This is not theoretical. On the development machine a logon task called
"Monitoring" runs `cmd.exe`, and another runs `sc.exe`. Without the safety list
the sweeper would have killed `cmd.exe` every two seconds inside every alt
session — including the sweeper itself.

Two honest limits. The blocked apps do briefly start before being closed, since
this closes them rather than preventing the launch. And it only covers processes
in the session: a background **service** is machine-wide and keeps running, so
for something like Cloudflare WARP this stops the client window appearing but
does not take the machine off the tunnel.

### Getting past the "Unknown remote connection" warning

Out of the box, every launch stops on a security warning naming an unknown
publisher. It is about the `.rdp` *file*, not the connection: an unsigned file
could have been tampered with, so the client asks before honouring it. There is
no policy to turn it off and no "don't ask again" for an unsigned file. The only
supported way past it is to sign the file with a certificate the machine trusts.

**Sign session files** does that. It generates a code-signing certificate on this
machine, trusts it, and signs every `.rdp` the app writes -- including on each
rewrite, since the signature covers the address and redirection settings and a
rewrite would invalidate it.

Three things have to line up, and all three were established by testing. Drop any
one and the dialog comes back:

| Piece | Without it |
|---|---|
| Certificate with its private key in `LocalMachine\My` | nothing to sign with |
| Public half in `LocalMachine\Root` | dialog still appears, now naming the publisher and asking you to vouch for it |
| Thumbprint in the `TrustedCertThumbprints` policy | dialog still appears |

Two traps worth writing down. `rdpsign.exe` takes the certificate's **SHA1**
thumbprint, even though the switch is spelled `/sha256` and its own help text says
otherwise. And putting the certificate in `TrustedPublisher` does *not* suppress
the prompt, despite the dialog's "remember my choices for connections from this
publisher" checkbox implying exactly that.

**On the trust footprint**, since this ships to other people: the certificate is
generated locally on each machine, its private key is never exported, and it is an
end-entity certificate rather than a CA -- so it can vouch for nothing except
itself. It cannot issue further certificates. To undo it completely:

```powershell
foreach ($s in 'My','Root') {
  Get-ChildItem "Cert:\LocalMachine\$s" |
    Where-Object { $_.Subject -eq 'CN=BSS Alt Manager RDP Signing' } | Remove-Item
}
Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services' `
  -Name TrustedCertThumbprints
```

## Accounts

The **Accounts** tab holds the Roblox logins, separately from the alts. They are
two different things: an alt is a Windows account with a session, a Roblox
account is who signs in inside it. Keeping them apart means a login can be moved
to a different session without rebuilding anything.

**Add account** opens Roblox's own login page in an embedded Chromium (the Edge
WebView2 runtime) and waits. When Roblox sets its `.ROBLOSECURITY` cookie, that
token is read out of the browser's cookie jar, checked against
`users.roblox.com/v1/users/authenticated` to find out whose account it is, sealed
with DPAPI and saved. Then the window closes.

Why a real browser rather than posting the credentials to Roblox's login API:

- **The password never reaches this app.** It is typed into Roblox's page.
- **Captchas and 2-step verification work**, because it genuinely is a browser
  rather than something imitating one. The API path breaks the moment Roblox
  serves an Arkose challenge, which for a fresh login is most of the time.
- **However the user got in counts.** One-time email codes and Quick Sign-in end
  with the same cookie, so watching the cookie jar catches all of them without
  the app knowing which was used.

Each sign-in gets a **throwaway browser profile** under
`%LOCALAPPDATA%\BssManager\login-profiles`. Adding a second account therefore
starts logged out rather than landing on the first one. The profile is deleted
once the browser lets go of it -- which is why it waits on the browser process
rather than deleting immediately: WebView2 outlives its window by a few seconds
and holds the folder open the whole time.

### What is actually stored

The token, not a password, sealed with DPAPI under the current Windows user --
same treatment as the alt account passwords, and it stops working if `config.json`
is copied to another machine or user profile.

Worth being clear-eyed: that token **is** access to the account for as long as it
lives. It is what any Roblox account manager stores, and there is no lesser
credential that will sign a session in. Roblox invalidates it on a password
change and on "log out of all sessions", and can expire it whenever it likes,
with no notice and no expiry date to read.

That is why a row says when the token was last confirmed rather than claiming it
is valid. **Check** asks Roblox; **Check all** does the lot before a run. A dead
one is fixed by signing in again -- the same account refreshes in place instead
of adding a duplicate.

### Which alt runs which account

The picker lives on the **Alts** tab, on the alt's own card, because a session
runs one account at a time -- that constraint belongs to the session. Assigning
an account that is already on another alt moves it rather than duplicating it.
The accounts list reports the assignment but does not set it.

## Signing an alt in

**Sign in** on an alt's card opens Roblox inside that session, signed in as the
assigned account, and joins the game. **Launch** does it by itself when the alt
has an account: it waits for the session to come up, gives the desktop a moment,
then signs in.

(The tab is labelled **RDP'S** in the app. The code calls the same thing an
*alt*, which is what it is once a Roblox account is attached to it.)

Two problems have to be solved, and neither is the obvious one.

### The saved token cannot launch anything

Roblox does not accept `.ROBLOSECURITY` as a way to start the client. The token
buys a **single-use authentication ticket** that expires in about a minute, and
the ticket is what goes into the `roblox-player:` launch URL.

That is a gift rather than an obstacle. The thing that crosses into the alt's
session is worthless a minute later and cannot be replayed, so the long-lived
credential never leaves this app.

Getting a ticket takes a CSRF token, and Roblox only issues one *by rejecting a
request first* -- so the 403 in the middle of that exchange is the happy path.

### Nothing this app starts can appear on another session's desktop

This app runs in your session. `CreateProcessAsUser` against another session
needs a token only SYSTEM can hand out, which means installing a service -- a lot
of machinery to put on someone else's PC.

Task Scheduler already does exactly this. A task registered against the alt with
`TASK_LOGON_INTERACTIVE_TOKEN` runs **inside whatever session that account is
signed in to**, started on demand by an administrator, with no stored password
and no service to install. One task per alt, registered when the alt is created
or repaired.

The task itself never changes: it runs a script that reads a URL from
`%ProgramData%\BssAltManager\launch\<user>.url` and opens it. Only the file
changes per launch, which keeps the ticket out of the task definition. The script
deletes the file *before* opening it, so a crash in between cannot leave a live
ticket readable, and the file is written with an ACL granting only that alt and
administrators -- ProgramData is world-readable by default.

### Private servers

**PS link** on a card sets which private server that session joins. Empty means
public servers, and that is a real answer rather than an unset one — hence Clear
being separate from Cancel. "Use this private server for every RDP" sets them all
at once, which is the usual case: the alts join the same server as the main
account.

The link is the long-standing form, the one you get from the server's own page:

```
https://www.roblox.com/games/1537690962/Bee-Swarm-Simulator?privateServerLinkCode=2849927461313...
```

The `privateServerLinkCode` is what joins; the place id in the path says which
game it belongs to, and is trusted over the configured one — joining a private
server of a different game would silently do nothing useful. A bare code pasted
on its own works too.

Short `roblox.com/share?code=...` links are **refused with an explanation**, not
half-accepted. They carry no usable code: resolving one takes a signed-in request
to a different API, and a link that looks right but launches nothing is worse
than one that says why.

With a link set, the launcher request changes from `RequestGame` to
`RequestPrivateGame` and carries the code. Validation happens as you type, in the
dialog — not thirty seconds into a launch, by which point the single-use ticket
has already been spent.

### Knowing whether it worked

The file disappearing proves the session ran the script -- better evidence than
the task's own result code, which only reports on `wscript`. After that the app
watches for a `RobloxPlayerBeta` process **in that session** before claiming the
alt is in the game. Delivered and running are different things, and only the
second one matters.

Failures say which stage lost it: a rejected token, a session that never picked
the launch up, or Roblox never appearing.

### What is unverified

Everything up to the launch URL is tested, and the delivery into a session is
tested end to end. What has not been run is Roblox itself accepting a real
ticket, which needs a live account. The URL format is the one account managers
use; it is also Roblox's to change, and when they do this is what breaks.

## Decisions worth knowing

**One loopback address per alt.** Alt 1 connects to `127.0.0.2`, alt 2 to
`127.0.0.3`, and so on. All the same machine. Windows Credential Manager stores
exactly one credential per target host, so alts sharing `127.0.0.1` would overwrite
each other's saved logins and every launch after the first would prompt.

**Resolution is pinned, smart sizing and dynamic resolution are off.** Pixel- and
image-search macros are tuned to one exact size. A session that resizes with its
window, or gets rescaled, produces a macro that "randomly" misclicks. Keep every
alt running the same macro at the same size.

**Session state comes from the Terminal Services API, not from tracking mstsc.**
Sessions outlive the client that created them and survive an app restart. Watching
processes would lie; the session table doesn't.

**Passwords are generated and never shown.** Nothing in the workflow needs a human
to type them, so there is no reason for them to be memorable or visible. They are
created with a crypto RNG, sealed with DPAPI under your user account, and written
into Credential Manager through the Win32 API rather than through `cmdkey`, so they
never appear in a command line. Copying `config.json` to another machine or user
profile deliberately makes them unreadable — use Repair there, which issues new ones.

**Deleting an alt does not delete its profile folder.** That folder holds the alt's
Roblox install and macro config. Removing the Windows account is a separate,
explicit choice in the dialog.

**Detach is offered but discouraged.** A detached session may stop composing its
desktop, which stalls any macro reading pixels. Minimising the window — with the
minimise registry fix applied — is the safe way to get a session out of the way.

## The macro

The in-game half is [Kairos](https://github.com/KairosMacro/Kairos), an
AutoHotkey v2 macro. This app installs and configures it per alt but does not
contain it.

**It is downloaded, not bundled.** Kairos is GPL-3.0. Shipping a copy inside
this app would put obligations on the whole distribution; fetching it from the
project's own release page when an alt needs it makes this an orchestrator of
software you obtained yourself. The version is pinned to a release tag rather
than tracking `main`, so an ini schema cannot change underneath us silently.

**Every alt gets its own copy**, in `%ProgramData%\BssAltManager\macro\<user>`,
ACL'd to that alt alone. This is not wasteful, it is required: Kairos resolves
settings against its working directory and declares `#SingleInstance Force`, so
two alts sharing a folder would share one config and then kill each other.

**Only the per-alt settings live here** — account type, alt number, hive slot,
walkspeed, field, pattern, sprinkler, rotation, and the private server. Kairos
has around a hundred settings across eight sections; mirroring all of them would
mean re-implementing its GUI and re-breaking on every release. Everything else
stays in the macro's own window, inside the session.

The private server sits with the macro settings rather than on the RDP card
because it is not a property of the session — it is part of what this alt is set
up to do, alongside its field and pattern. That is what makes Launch a single
action: the session opens, signs in, joins that server, and starts the macro,
with nothing to confirm in between.

Two files are written, in two formats, and they are not interchangeable.
`settings\<user>.ini` is read by Kairos's own parser, which expects the BOM its
writer emits. `settings\global.ini` — which names the preset to load — goes
through the Win32 profile API, which does *not*: a BOM there stops it finding
the section, and the macro silently loads the wrong preset.

**Starting it takes a small trick.** Kairos has no command line at all; it opens
a window and waits for its start hotkey. Two things get it going without anyone
pressing F1 in the session.

The primary path is a **patch to Kairos itself**. When an alt's copy is installed,
the app injects a short block into the downloaded `Main.ahk` — a timer that calls
the macro's own `start()` once `GetRobloxClientPos()` reports the client is up.
It is idempotent (guarded by Kairos's own `ran` flag and a marker comment so a
re-install won't double-patch) and it edits only the on-disk copy each alt
downloads for itself, so nothing GPL is redistributed. The macro starts itself,
inside its own session, the moment the game is ready.

As a fallback the launcher script also **clicks the Start control** from outside —
steadier than a keystroke, which would go wherever focus happened to be in a
session that just launched Roblox. It finds that control by its label, which reads
`Start (<key>)`. Matching the prefix rather than the whole string is deliberate:
the start key never has to be agreed on in two places, and a control that no longer
says Start — a run already going — is left alone.

Kairos hard-exits if screen DPI is not 96, which is why `desktopscalefactor:i:100`
in the .rdp file is load-bearing rather than cosmetic.

## Hiding the windows

Six alts means six mstsc windows and six taskbar buttons for things you never
look at, so **every RDP launches hidden** — it never pops up on screen. mstsc is
asked to open with a hidden window, and a tight poll takes it off screen the
instant it appears, so at worst there is a sub-frame flicker.

Each card carries **one on/off button**: Launch when the session is off, Log off
when it is on. **Show** is how you bring a hidden window up when you want to look
at it, and it doubles as reconnect — if a running session has lost its window
(detached or closed), Show relaunches it visible rather than leaving it stranded.

The distinction that matters is **hidden versus minimised**. Minimising an RDP
window makes the client tell the server to stop sending updates: the remote
desktop stops composing and a pixel-reading macro goes blind. That is what
`RemoteDesktop_SuppressWhenMinimized = 2` prevents, and why it is a health
check. Hiding never makes the window iconic, so that path is not taken at all --
measured while hidden, the client keeps decoding frames at the same rate as when
it is on screen.

A hidden window has no taskbar button and no Alt-Tab entry, and it outlives this
app. So the app reveals every RDP window as it closes, including ones it did not
hide -- otherwise a stray would only be recoverable by killing mstsc.

## A note on Effects and text

Do not put a `DropShadowEffect` on anything that contains text. WPF renders an
effect's whole subtree into an intermediate surface, and text in that surface
falls back from ClearType to grey antialiasing -- which reads as the whole app
being slightly out of focus. Panel shadows applied this way once made every
label in the window soft.

Where a glow is worth keeping, put it on a `Border` with no children and lay the
content over it in a `Grid`. The buttons and the logo tile do this.

## Not built yet

The session comes up, Roblox signs itself in, joins the game, and the macro
starts on its own. The one thing still missing is **watch and recover** — a
heartbeat that detects a crashed client or a stalled macro and restarts it. An
alt dying at 3am and quietly doing nothing until morning costs more than every
manual step this app already removed.

Sending *into* a session is solved. What is still missing is anything reporting
back *out* of one — that is the remaining architectural piece.

## Layout

```
src/BssManager/
  Models/      AltProfile, RobloxAccount, AppConfig, HealthCheck, SessionInfo,
               MacroSettings, PrivateServerLink
  Native/      P/Invoke: WTS sessions, netapi32 accounts, Credential Manager
  Services/    RdpWrapService (health), RdpSigningService, LocalUserService,
               CredentialService, RdpFileService, SessionService, AltManager,
               AltSetupService, SessionCommandService, RobloxAccountService,
               RobloxApi, RobloxLaunchService, KairosService, ConfigStore
  ViewModels/  MainViewModel, AltRowViewModel, AccountRowViewModel
  Views/       AddAltDialog, MessageDialog, RobloxLoginWindow,
               PrivateServerDialog, MacroDialog, converters
```

## License

Released under the [GNU General Public License v3.0](LICENSE).

The in-game macro, [Kairos](https://github.com/KairosMacro/Kairos), is a separate
GPL-3.0 project and is **not** bundled here — this app downloads it at runtime from
its own release page and patches only the on-disk copy each alt fetches for itself.
See [The macro](#the-macro).

## Disclaimer

This is an unofficial tool, not affiliated with or endorsed by Roblox or the Bee
Swarm Simulator developers. Automating gameplay may violate the Roblox Terms of
Service and can put accounts at risk. Use it on accounts you are willing to lose,
and at your own risk.
