# Global hotkeys: diagnosis and manual verification

## Findings (2026-10-15)

Fresh installations use Ctrl+Alt+F8 (Rewind), F10 (SaveClip), F9 (recording),
and F1 (screenshot). Existing settings are intentionally preserved. The local
configuration inspected during this investigation instead had **F9 for Rewind,
no recording shortcut**, F10 for SaveClip, and F1 for screenshots. This explains
the F8/recording binding mismatch; it does not prove the cause of every earlier
reported missed shortcut, particularly in Forza.

The corrected native tests registered all four defaults and received real
WM_HOTKEY messages. No general RegisterHotKey/HwndSource dispatch failure was
reproduced. The HWND/hook is created on the WPF STA dispatcher at SourceInitialized;
Hide/minimize retain it, and tray restore reuses it. The implementation uses
Win32 virtual keys (Ctrl=2, Alt=1, Shift=4), not translated characters. Function
keys are independent of text layout; letter/OEM bindings follow Windows virtual
key semantics. Ctrl+Alt can also represent AltGr on some layouts; test such
bindings on the intended layout. Windows/other-app conflicts remain possible.

Previously, SaveClip returned without feedback when no recorder was running, and
startup registration failures only left the last error in Settings/a transient
warning field. Both could look like an ignored key. The update keeps per-action
registration errors visible, logs native failures even without diagnostics,
provides Retry registration and explicit Restore defaults, and reports rejected
SaveClip commands. A failed edit retains the old live binding and settings.
Restore defaults can transfer legacy F9 from Rewind to Recording without a
temporary unregister or an internal conflict. Save failures roll back new
reservations. No settings migration resets a user's bindings automatically.

WM_HOTKEY queues work onto the dispatcher outside the native window procedure.
Shared application commands retain their existing queue/capture guards;
MOD_NOREPEAT suppresses held-key repeats, with an additional in-flight guard and
350 ms debounce for toggles. Saves continue through the bounded Save Queue and
are not serialized for the duration of export. Exit removes registrations and
the hook before asynchronous shutdown; native unregister failures are logged.
No global keyboard hook, injection, game integration, or anti-cheat workaround
was added to Rewind.

## Recover the expected defaults

Open Settings → Горячие клавиши → **Восстановить стандартные сочетания**.
This is explicit and transactional: if Windows rejects a default, the old
configuration remains. Alternatively keep/customize the existing bindings.
For an unavailable shortcut, close/reconfigure its other owner and choose
**Повторить регистрацию**. Reopening Rewind from its tray also retries missing
registrations. Windows error 1409 means a shortcut is occupied; Windows does not
identify its owner through RegisterHotKey.

## Opt-in stage diagnostics

Exit Rewind from its tray first, then run from the repository in PowerShell:

```powershell
$env:REWIND_HOTKEY_DIAGNOSTICS_FILE = "$PWD\artifacts\hotkeys.jsonl"
Start-Process "$PWD\artifacts\rewind-manual-test\Rewind.exe"
```

Unset the environment variable before the next launch to turn diagnostics off.
Only hotkey-related state is recorded, not arbitrary keystrokes. Logs may contain
media paths/feedback. Do not publish them without reviewing them. File writes run
on a bounded background queue; the log rotates at 2 MiB. The ordinary errors.log
still records registration/settings/unregister failures without opt-in logging.

| Stage | Interpretation |
| --- | --- |
| session/configuration | Session, admin-role status, dispatcher apartment, actual enabled bindings and HWND |
| register | Success/failure and immediate Win32 error |
| message/message-ignored | Raw real WM_HOTKEY, foreground HWND and visibility/state; unmatched/stale/editing messages |
| command-start/command-ignored | Routing/admission and repeat protection |
| command-returned/command-failed | Shared command state/error/feedback; returning alone does not mean success |
| save-rejected/save-deferred/save-accepted | Capture/admission versus queue state |
| save-published/save-failed/screenshot-published | Actual media outcome |
| notification/overlay-shown | Feedback requested/applied; not proof of visibility over exclusive fullscreen |
| unregister | Native cleanup result |

The manifest remains asInvoker/uiAccess=false. Integrity levels can affect
synthetic test input: Microsoft documents that SendInput is restricted by UIPI.
That limitation must not be confused with physical keyboard/WM_HOTKEY behavior.
No elevation is requested as a hotkey fix. See [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
and [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).

## Verification and limits

Debug and Release builds completed without warnings/errors. Corrected interactive
test: `artifacts/hotkeys-visible-20261015-230121/results.json` (26 checks).
All four Ctrl+Alt defaults dispatched while focused, with File Explorer focused,
minimized, and closed to tray. The last two remained minimized/hidden. Actual
screenshots, Rewind saves, conventional recording start/stop and notifications
were observed. Save while Rewind was off produced rejection feedback.

Release test: `artifacts/hotkeys-visible-20261015-230607/results.json` includes
repeated native SaveClip requests while conventional recording stayed active,
validated MKVs (including a queued request), and custom Ctrl+Shift+F2 producing a
screenshot. A separate restarted process reloaded/re-registered that custom
binding. Component checks cover duplicate/external conflicts, preference/status
separation, disable/re-enable, legacy F9 transfer, failed restore, failed settings
write, retry recovery and native cleanup.

Final component checks and UI renders are in
`artifacts/hotkeys-component-layout-final/` (26 checks; `settings.png` and
`conflict.png`). Conflict feedback reserves space below the main controls.

Regression reports: `artifacts/hotkeys-regression-final/results.json` (5,480),
`artifacts/hotkeys-save-queue-final-2/results.json` (422), and
`artifacts/hotkeys-compression-final/results.json` (36). The first Save Queue run
had a missing independent decoder path; rerunning with
`REWIND_TEST_DECODER=.tools/decoder/ffmpeg.exe` passed. Earlier `hotkeys-baseline*`,
`hotkeys-probe*`, `hotkeys-early-probe`, `hotkeys-live*`, and the 22:55 visible run
had an unintended second App window and are **not valid conflict/root-cause
evidence**. The harness now loads only production resources, never App startup.

Not tested: Forza foreground/exclusive fullscreen, an elevated game foreground,
and the bare Explorer desktop shell itself (automated unfocused tests used a
File Explorer window). No Forza compatibility claim is made. Manually test all
four bindings in those environments and confirm file creation as well as overlay
visibility. If they fail, compare registration → message → command → publication
→ feedback logs before attributing the failure to game restrictions.

## Repeat the harness

```powershell
$env:DOTNET_ROOT = "$PWD\.tools\dotnet"
$env:DOTNET_CLI_HOME = "$PWD\.tools\dotnet-home"
$env:NUGET_PACKAGES = "$PWD\.tools\nuget"
& .\.tools\dotnet\dotnet.exe run --project dev/HotkeyChecks -c Debug -- checks artifacts/hotkeys-new-checks
```

Use `live` instead of `checks` for the four foreground/visibility phases, or
`burst` for repeated saves/custom screenshot. Follow the generated `phase.txt`.
Run on the visible, unlocked Windows desktop with ordinary Rewind closed.
`restart` reuses an existing test directory's settings. Test settings, queue and
media stay under the specified artifacts directory. A no-argument published
harness generates a fresh output directory and runs `live`; an adjacent
`hotkey-test-mode.txt` may select `checks` or `burst`. Harness binaries/media are
development outputs and never belong in the source publication or manual app.
