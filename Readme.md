# AppPercyTosca

App Percy visual testing for Tricentis Tosca mobile tests. Produces a distributable assembly
(`AppPercyTosca_v8.dll`) that adds an `AppPercyScreenshot` special execution task to Tosca, for taking
App Percy screenshots of the mobile app under test.

Built for **.NET 8 / Tosca Commander 24**. For web (HTML) tests, use
[percy-tosca-dotnet](https://github.com/percy/percy-tosca-dotnet) instead — this repository is the
mobile counterpart.

## How capture works

**App Percy on BrowserStack App Automate.** That is the supported configuration — there is no separate
path for local or other-cloud devices.

Everything goes through the session's own HTTP endpoints, using two facts: the server address from your
`AppiumServer` test configuration parameter, and the Appium session id. No Tricentis API is involved,
which matters because Tosca's internals change between releases while the WebDriver protocol does not.

| Capture | How |
|---|---|
| **Single page** | The hub's `percyScreenshot` executor command captures and uploads; tiles come back as content hashes |
| **Full page** | The same command with `screenshotType: fullpage` — the hub scrolls, captures N tiles and returns their overlap heights for Percy to stitch |

Both are issued as `browserstack_executor:` scripts over `POST /session/{id}/execute/sync`. Tosca will
not pass a raw Appium command through, but the hub accepts one directly — so full page works here, which
earlier versions of this SDK wrongly concluded was impossible.

With `PERCY_DISABLE_REMOTE_UPLOADS=true` a single tile is captured over
`GET /session/{id}/screenshot` and uploaded instead. Full page is unavailable that way, exactly as in
percy-appium-dotnet.

Route 1 is the one to get working. It needs the Appium session id — see below.


### Requirements

- The session must be on **BrowserStack App Automate** — `AppiumServer` pointing at a BrowserStack hub
- The **Appium session id** must be passed to the module (see below)

XPath and accessibility-id regions are resolved against elements, which this SDK does not query — use
`CustomIgnoreRegions` with pixel coordinates.

## Requirements

- Tosca Commander 24, with Mobile Engine 3.0 installed and its mobile server running
- `@percy/cli` **1.27.0 or newer** (`/percy/comparison` landed there)
- Node 14+ for the CLI

## Setup

Install and start the Percy CLI:

```sh-session
$ npm install --save-dev @percy/cli
```

```sh-session
$ set PERCY_TOKEN=<your App project token>
$ percy app:exec:start
```

Use an **App** project's token and `app:exec:start`. A token starting with `auto_` is an Automate
project token and selects a mode this SDK does not support — the step reports that plainly rather than
failing obscurely.

Then register the extension:

1. Copy `AppPercyTosca_v8.dll` from the [releases](../../releases) into
   `C:\Program Files (x86)\TRICENTIS\Tosca Testsuite\Percy`
2. Add that path in Tosca Commander → Project settings → TBox → Extension loading → Extensions
3. Restart Tosca Commander

### The module

Create a module with **Engine** → `Percy` and **SpecialExecutionTask** → `AppPercyScreenshot`, then add
each parameter you want as a row with **Parameter** → `True`.

Getting these exactly right matters — a mistyped task name or engine surfaces as
`The SpecialExecutionTask 'x' was not found for engine 'y'`, which reads like a broken install rather
than a typo. The minimum viable module is three rows:

| Row | Value | Notes |
|---|---|---|
| `SnapshotName` | e.g. `Home` | Required; must be unique per snapshot |
| `Diagnose` | `true` | Worth leaving on until the first snapshot lands |
| `SessionIdBuffer` | `PercyAppiumSessionId` | Only if you named your buffer something else |

> **Not yet shipped:** the web SDK ([percy-tosca-dotnet](https://github.com/percy/percy-tosca-dotnet))
> ships a `PercySnapshot.tsu` subset so you can import a correct module instead of building one. The
> equivalent for this SDK is not in the repo yet — see `Release.md`. Until it is, build the module by
> hand from the table above.

### Required: the Appium session id

Capture needs the session id, and Tosca is the only thing that knows it. Add the built-in **Get Appium Session Id** standard module (Standard
modules → Engines → Mobile) before your AppPercyScreenshot step, writing to a buffer. Then hand that
buffer to Percy as a parameter value:

| Row | Value |
|---|---|
| `SessionId` | `{B[PercyAppiumSessionId]}` |

Tosca resolves the `{B[...]}` reference before the step runs, so the SDK receives the id as a plain
string. This is the most reliable route — it uses documented Tosca behaviour rather than reading
Tosca's buffer store, and it takes precedence over everything below.

(`SessionIdBuffer` names a buffer for the SDK to read itself, if you would rather not add a `SessionId`
row. It works, but it reaches into Tosca internals whose shape is not published.)

Without it, capture cannot reach the device and the step says so.

There used to be a third route — asking BrowserStack which session was running — and it is gone on
purpose. It inferred rather than knew, and on a shared account it could capture the wrong device and
produce a snapshot plausible enough to be accepted as a baseline.

## Parameters

`SnapshotName` is the only required parameter. Everything else is optional; a step with just a name
takes a single-screen snapshot of the current screen.

### Naming

| Parameter | Description |
|---|---|
| `SnapshotName` (**required**) | The snapshot name; must be unique to each snapshot |
| `TestCase` | Test case name, for grouping in Percy |
| `Labels` | Comma-separated labels |
| `Sync` | `true` to wait for the comparison before continuing |
| `ThTestCaseExecutionId` | Test Management execution id, for linking back to a test run |

### Device details

Tosca exposes device information through test configuration parameters (`DeviceName`, `OSVersion`,
`AppiumServer`, …) and this SDK reads them automatically. Set these only when a parameter is missing
or wrong — for example when the device is not in the SDK's built-in dimension table, which covers
older iPhones and iPads only (see `AppPercyTosca.Core/resources/devices.json`).

| Parameter | Description |
|---|---|
| `DeviceName` | Device label used for the Percy tag and dimension lookup |
| `OsName` | `Android` or `iOS`. Required if Tosca reports no platform |
| `OsVersion` | OS version |
| `ScreenWidth`, `ScreenHeight` | Full screen size in **pixels**. Worth setting on App Percy — see below |
| `StatusBarHeight`, `NavBarHeight` | Bar heights in pixels. `0` means "no bar" and is respected |
| `Orientation` | `portrait`, `landscape`, or `auto` to ask the device |

On the **App Percy** path, `ScreenWidth`/`ScreenHeight` are worth setting explicitly unless a
`DeviceScreenSize`, `ScreenResolution` or `Resolution` test configuration parameter carries the size.
Percy groups and diffs comparisons by the device tag, so a snapshot tagged `0x0` will not group with
correctly-tagged ones — the step warns when this happens.

### Capture

| Parameter | Description |
|---|---|
| `FullScreen` | `true` if the app is in full-screen mode |
| `FullPage` | `true` to capture the whole scrollable page |
| `ScreenLengths` | Number of screens to capture for a full page |
| `ScrollableXpath`, `ScrollableId` | Which element to scroll for a full page |
| `TopScrollviewOffset`, `BottomScrollviewOffset` | Pixels to trim while scrolling |
| `IosOptimizedFullpage` | `true` for the optimised iOS full-page algorithm |

### Regions

Regions are given in **pixel coordinates**.

| Parameter | Description |
|---|---|
| `CustomIgnoreRegions`, `CustomConsiderRegions` | `top,bottom,left,right` in pixels, one region per entry |

XPath and accessibility-id regions are **not available on Tosca**. Resolving them needs a driver to
query for elements, which a Tosca mobile session does not expose to an extension. Use pixel regions.

Lists are separated by **newlines** if the value contains any, otherwise by **semicolons**. Commas
are deliberately not separators, because an XPath predicate such as `//*[contains(@id,'total')]`
contains one and splitting on it would silently break the locator. Inside a custom region, commas
separate the four numbers:

```
0,100,0,1080; 2200,2340,0,1080
```

### Escape hatches

| Parameter | Description |
|---|---|
| `SessionId` | The Appium session id. Use `{B[PercyAppiumSessionId]}` to pass a buffer written by the *Get Appium Session Id* module |
| `SessionIdBuffer` | Buffer for the SDK to read itself instead (default `PercyAppiumSessionId`) |
| `Diagnose` | `true` to log everything the SDK could and could not read from Tosca |

## Troubleshooting

Set `Diagnose` to `true` on the module and run the step. It logs the hub URL, the session id, the
resolved platform and every capability found — enough to identify almost every problem, since nearly
all of them are a missing test configuration parameter or an unset session-id buffer.

For more detail, set `PERCY_LOGLEVEL=debug` before starting Tosca Commander.

Log output goes to the Percy CLI (so it appears alongside the rest of the build) and is mirrored to
**`%TEMP%\percy.txt`**, which is where to look when the CLI itself is what failed. Every line is
timestamped. Set `PERCY_LOG_FILE` to pin it somewhere else — worth doing, because `%TEMP%` resolves
per-account and Tosca may not run as you.

The first line written is an `assembly loaded:` entry, recorded when the CLR loads the extension —
before Tosca has decided whether to register anything. That makes an empty or missing log file
diagnostic in itself:

| `percy.txt` | Meaning |
|---|---|
| missing, or no `assembly loaded:` line | Tosca never loaded the DLL. Check the extension folder, the registered path, and that Commander was fully restarted — not the code |
| has `assembly loaded:` but nothing else | The DLL loaded but the task was not registered, or no step ran. Compare the module's `Engine` and `SpecialExecutionTask` values against `Percy` and `AppPercyScreenshot` |
| has later lines | The extension is running; read on for the actual problem |

A failed snapshot does **not** fail the Tosca step: a visual check that could not run is not a
functional regression, and failing the step would stop the rest of the sheet. To change that, add a
test configuration parameter `percy.ignoreErrors` with the value `false`. A TCP named `percy.enabled`
set to `false` turns Percy off entirely without editing any test sheet.

(The other App Percy SDKs use a nested `percyOptions` capability for this. That shape cannot come from
a TCP, which is why the flat `percy.*` spellings are the ones to use on Tosca.)

### Environment variables

| Variable | Effect |
|---|---|
| `PERCY_TOKEN` | Your Percy project token (read by the CLI) |
| `PERCY_LOGLEVEL=debug` | Verbose SDK logging |
| `PERCY_CLI_API` | CLI address (default `http://localhost:5338`) |
| `PERCY_TMP_DIR` | Where screenshot tiles are written (default the system temp directory) |

## Development

The repository is split so that almost all of it is testable without Tosca:

| Project | What it is |
|---|---|
| `AppPercyTosca.Core` | All of the logic — CLI protocol, device metadata, option parsing, capture flows. No Tricentis dependency |
| `AppPercyTosca.Core.Tests` | Unit tests. CI enforces **100% line coverage** of the Core |
| `AppPercyTosca.ShimCheck` | Compile-checks the Tosca shim against stubbed Tricentis types, so the shim's source is not entirely unverified on CI |
| `AppPercyTosca` | The Tosca shim — the only code that touches Tricentis assemblies. Builds only on a machine with Tosca installed |

```sh-session
# Everything CI runs; works on any OS with the .NET 8 SDK
$ dotnet test AppPercyTosca.Core.sln

# The extension itself; needs Tosca Testsuite installed
$ dotnet build AppPercyTosca.sln
```

The shim is deliberately thin. If you find yourself adding a decision to it, consider whether it
belongs in the Core behind `IToscaEnvironment` instead — that is the seam that makes the rest of this
testable.

See [Release.md](Release.md) for how a release is cut.
