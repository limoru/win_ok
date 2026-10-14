# Development

## Prerequisites

- Windows 10/11
- .NET 9 SDK
- The repository pins SDK `9.0.315` in `global.json`.

The locally installed .NET 10 compiler currently fails on this Windows build with a CET compatibility error. The project therefore targets .NET 9 for the first runnable milestone.
## Current Decision

Decision recorded on `2026-10-11`: keep the project on .NET 9 for the current development milestone.

- Functionally, .NET 9 works for this project: the solution builds without warnings, the WPF UI starts, Windows target discovery works, and session/diagnostic interactions pass smoke tests.
- The current choice avoids the Windows 10 CET problem that prevents the installed .NET 10 compiler from starting.
- .NET 9 is an STS release whose support ended on `2026-05-12`. It no longer receives normal security updates, so this is an interim development choice rather than a preferred final release target.
- After Windows is updated to at least build `19045.5011`, revisit .NET 10 and remove the SDK pin from `global.json`.
- The migration expected at that point is mainly the SDK version and `TargetFramework` values (`net9.0` to `net10.0`, and `net9.0-windows` to `net10.0-windows`). The project does not intentionally depend on .NET 9-only APIs.

## CET Startup Issue

The current machine is Windows 10 22H2 build `19045.2006`. .NET 9 and later mark apphost executables as CET-compatible. CoreCLR then enables the CPU shadow stack, but this old Windows build lacks the corresponding special user-mode APC support. CoreCLR detects that combination during startup and calls fail-fast with exit code `0xC0000602`.

Microsoft fixed this in the Windows 10 servicing train. Build `19045.5011` is the lowest version reported to contain the fix. This machine currently has `KB5066791` available in Windows Update, which upgrades it to `19045.6456`.

Recommended fix:

1. Install `KB5066791` or a later cumulative update.
2. Restart Windows.
3. Confirm that the build number is at least `19045.5011`.
4. Remove the SDK pin in `global.json` or change it to the desired .NET 10 SDK version.

The local Windows Update policy currently has `NoAutoUpdate = 1`, so updates do not install automatically.

For applications that must run on unpatched Windows 10 machines, add this to the application project:

```xml
<CETCompat>false</CETCompat>
```

That workaround disables CET for the application executable. It does not fix the .NET 10 SDK compiler itself, because the SDK's own compiler apphost is also marked as CET-compatible.

## Build

```powershell
dotnet build AutomationPlatform.slnx
```

## Run the GUI

```powershell
.\run-gui.cmd
```

The script builds the solution and launches the WPF application through the `dotnet` host. Apphost generation is disabled for local debugging because the generated `.exe` hits a CET fail-fast on the current Windows build.

## Implemented

- Layered solution structure.
- Core target/session/frame/input contracts.
- Windows visible-window discovery.
- Multi-session WPF shell.
- Session creation, start/pause state, logs, and diagnostics export.
- Initial Windows client-area capture: PrintWindow with client crop and BitBlt fallback, PNG export, developer coordinate readout, and a separate screenshot-sized 1:1 preview window.
- Developer vision tools: rectangle annotation, crop preview/export, PP-OCRv5 text detection plus per-line ONNX recognition for the selected region, and OpenCV template lookup on the captured frame.
- Shared per-session frame source with a background capture loop, frame sequencing, wait-for-next-frame, first-frame timeout, and capture-error reporting.
- Windows input dispatcher for background `PostMessage` and foreground `SendInput`, including pointer, swipe, keyboard, Unicode text, serialized operations, and button-release cleanup on cancellation.
- Windows Graphics Capture (WGC) capture backend using `Windows.Graphics.Capture`, a free-threaded `Direct3D11CaptureFramePool`, and D3D11 staging-texture readback.
- WGC captures the full window and crops to the target client area so frames share the same coordinate space as `PrintWindow` and `BitBlt`.
- Automatic WGC session rebuild after target resize or capture failure, with cursor capture disabled.
- Capture preference supports explicit WGC, compatibility `BitBlt`, and `Auto` (WGC -> `PrintWindow` -> `BitBlt`). Explicit WGC failures are reported instead of silently changing backends.
- Automatic capture fallback from a successful but black `PrintWindow` result to `BitBlt`; compatibility mode uses `BitBlt` directly.
- Session startup now opens the real Windows target, waits for the first frame, reports backend/state, and disposes the capture loop when stopped.
- `SessionTaskCoordinator` serializes each session, runs modules in order, supports pause/resume and cancellation, reports the idle state, and exposes the currently executing module.
- `TaskContext` carries the real frame source, input sink, activity reporter, task log sink, the active task configuration snapshot, and an optional OCR provider. It provides latest/next frame waits, relative click/swipe, keyboard/text input, sleep, and structured logging.
- `DiagnosticTask` verifies frame sequence, size, and pixel format. It reports progress through `SessionActivity`, writes tagged `status`/`warning` logs, and never sends input during diagnosis.
- `ChaosModeTask` now has the first migrated page loop: OCR is run once per new frame, button text is matched inside Python-compatible relative regions, and center-confirm, confirm, continue, and enter handlers are implemented. The chaos descriptor remains `Planned` until the migration covers enough of the run loop to be safe to start from the GUI.
- Task catalog and `TaskPlanResolver` are implemented. `ChaosMode` is registered as planned while its page processors are being migrated, and `Diagnostic` remains the runnable vertical-slice example used by the task page.
- The GUI starts the task loop after the first frame is ready, mirrors task activity and logs into the existing home/log views, and drains the loop before releasing the capture and input resources.

## Verification

The safe Windows automation smoke test runs a local WinForms target and does not send foreground mouse or keyboard input.

Build and pure logic tests:

```text
dotnet build AutomationPlatform.slnx
0 warnings, 0 errors

dotnet test Automation.Core.Tests\Automation.Core.Tests.csproj --no-restore
Passed: 7, Failed: 0
```

Current smoke result:

```text
FIRST_FRAME seq=1 400x300 method=WindowsGraphicsCapture
SECOND_FRAME seq=2
WGC_FRAME 400x300 method=WindowsGraphicsCapture
WGC_RESIZED 500x350 seq=2
WGC_RESTORED 400x300 seq=3
COMPATIBILITY_FRAME 400x300 method=BitBlt
SESSION_START state=Running frame=1 message=已连接 400x300，首帧 #1 · WindowsGraphicsCapture。
SESSION_ACTIVITY task=运行诊断 step=抓帧链路正常 detail=帧 #1 · 400x300 · BGRA32
SESSION_LOG tag=status message=诊断通过：帧 #1 · 400x300 · BGRA32
```

## Next Milestones

1. Implement configuration and hot reload from [04-config-system-and-hot-reload.md](./04-config-system-and-hot-reload.md).
2. Implement the task catalog, task plan, and task board from [09-task-board-design.md](./09-task-board-design.md).
3. Expose OCR and template matching through `TaskContext`.
4. Add multi-scale template search and optional text-angle classification.
5. Add multi-session load and resource-limit tests.