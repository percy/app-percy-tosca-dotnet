# AppPercyTosca

App Percy visual testing for Tricentis Tosca mobile tests. Produces a distributable assembly
(`AppPercyTosca_v8.dll`) that adds an `AppPercyScreenshot` special execution task to Tosca, for taking
App Percy screenshots of the mobile app under test.

Built for **.NET 8 / Tosca Commander 24**. For web (HTML) tests, use
[percy-tosca-dotnet](https://github.com/percy/percy-tosca-dotnet) instead — this repository is the
mobile counterpart.

## How capture works

This SDK is built for **App Percy**: it captures the device screen and uploads it to Percy.

Capture is attempted two ways, in this order:

1. **Directly from the device session** — a standard WebDriver `GET /session/{id}/screenshot` against
   the server in your `AppiumServer` test configuration parameter, using the Appium session id from a
   Tosca buffer. This needs no Tricentis API at all, which is why it is preferred: it does not depend
   on internals that change between Tosca releases.
2. **Via Tosca's own mobile screenshot task** — used when the session cannot be reached directly.
   Requires `Directory` and `Filename` on the module, and the right task name for your Tosca version
   (discovered automatically; see `ScreenshotTaskName`).

Route 1 is the one to get working, and it needs the **Get Appium Session Id** step described below.


### What App Percy cannot do on Tosca

Two limits, both from what a Tosca extension can reach rather than from Percy. Mobile Engine 3.0 runs
**out of process**, so there is no Appium driver object for an extension to borrow, and Tosca's
`Execute Driver Script` is restricted to Tricentis' own device cloud:

- **No full-page capture.** Only the visible screen.
- **No XPath or accessibility-id regions.** Use `CustomIgnoreRegions` with pixel coordinates.

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

Create a module with:

- **Engine** → `Percy`
- **SpecialExecutionTask** → `AppPercyScreenshot`
- each parameter you want to use as a row with **Parameter** → `True`

### Required: the Appium session id

Capture needs the session id, and the only way to obtain it in Tosca is the built-in
**Get Appium Session Id** standard module (Standard modules → Engines → Mobile). Before your
AppPercyScreenshot step, add that module and have it write to a buffer named `PercyAppiumSessionId` — or
name your own buffer and pass it as the `SessionIdBuffer` parameter.

Without it, capture falls back to Tosca's own screenshot task, which is the less reliable route.

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

App Percy captures by asking Tosca's own mobile screenshot task to do it. Which task that is differs
between Tosca versions — the published `Mobile30PrintScreen` / `ME3.0` pair is from a Tosca 16-era page
and is **not** registered on 24 — so the SDK tries that pair first and then any task this install
registers whose name looks like a screenshot, preferring mobile ones. Run with `PERCY_LOGLEVEL=debug`
to see which it used, or every candidate it found if none worked, then pin it with
`ScreenshotTaskName` / `ScreenshotEngineId`.

`Directory` and `Filename` look like an odd thing to put in a test sheet, and they are — but they are
unavoidable. App Percy captures by delegating to Tosca's own mobile screenshot task, and that task
reads its destination from the test action's `Directory` and `Filename` parameters. There is no way to
supply them on your behalf: doing so would mean implementing `ISpecialExecutionTaskTestAction`, an
interface with ~35 members over undocumented Tricentis types. Two rows is the cheaper trade. The file
is read and deleted immediately.

On the **App Percy** path, `ScreenWidth`/`ScreenHeight` are worth setting explicitly unless a
`DeviceScreenSize`, `ScreenResolution` or `Resolution` test configuration parameter carries the size.
Percy groups and diffs comparisons by the device tag, so a snapshot tagged `0x0` will not group with
correctly-tagged ones — the step warns when this happens.

### Capture

| Parameter | Description |
|---|---|
| `FullScreen` | `true` if the app is in full-screen mode |

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
| `SessionIdBuffer` | Buffer holding the Appium session id (default `PercyAppiumSessionId`) |
| `Directory`, `Filename` | Destination for the fallback capture route. Only needed if the device session cannot be reached directly |
| `ScreenshotTaskName`, `ScreenshotEngineId` | Which Tosca task performs the App Percy capture. Only needed if discovery picks the wrong one — see below |
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
