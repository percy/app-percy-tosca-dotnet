# AppPercyTosca

App Percy visual testing for Tricentis Tosca mobile tests. Produces a distributable assembly
(`AppPercyTosca_v8.dll`) that adds a `PercyScreenshot` special execution task to Tosca, for taking
App Percy screenshots of the mobile app under test.

Built for **.NET 8 / Tosca Commander 24**. For web (HTML) tests, use
[percy-tosca-dotnet](https://github.com/percy/percy-tosca-dotnet) instead — this repository is the
mobile counterpart.

## Which mode to use

There are two ways to get screenshots into Percy, and on Tosca they are **not** equivalent. Pick
Percy on Automate unless you have a reason not to.

| | Percy on Automate (**recommended**) | App Percy |
|---|---|---|
| How | The Percy CLI reconnects to your BrowserStack session and captures server-side | This SDK captures locally through Tosca's mobile engine and uploads the image |
| Needs | BrowserStack App Automate + the Appium session id in a buffer (see below) | Any mobile session Tosca can screenshot |
| Full page screenshots | Yes | No |
| Ignore/consider regions by XPath | Yes — resolved by the CLI | **No** — pixel regions only |
| Device metadata | Resolved by the CLI from the live session | From test configuration parameters, or declared on the module |

The gap is not a shortcoming of Percy but of what a Tosca extension can reach. Mobile Engine 3.0 runs
**out of process** — Tosca Commander talks to a separate mobile server over IPC — so there is no
Appium driver object for an extension to borrow, and Tosca's `Execute Driver Script` (the only route
for a raw Appium command) is restricted to Tricentis' own device cloud. Percy on Automate sidesteps
all of that by having the CLI drive the session itself, which is why it can do more.

## Requirements

- Tosca Commander 24, with Mobile Engine 3.0 installed and its mobile server running
- `@percy/cli` **1.27.0 or newer** (`/percy/comparison` and `/percy/automateScreenshot` landed there)
- Node 14+ for the CLI

## Setup

Install and start the Percy CLI:

```sh-session
$ npm install --save-dev @percy/cli
$ set PERCY_TOKEN=<TOKEN>
$ percy app:exec:start
```

Then register the extension:

1. Copy `AppPercyTosca_v8.dll` from the [releases](../../releases) into
   `C:\Program Files (x86)\TRICENTIS\Tosca Testsuite\Percy`
2. Add that path in Tosca Commander → Project settings → TBox → Extension loading → Extensions
3. Restart Tosca Commander

Create a module with:

- **Engine** → `Percy`
- **SpecialExecutionTask** → `PercyScreenshot`
- each parameter you want to use as a row with **Parameter** → `True`

### Extra step for Percy on Automate

The CLI needs the Appium session id, and the only way to obtain it in Tosca is the built-in
**Get Appium Session Id** standard module (Standard modules → Engines → Mobile). Before your
PercyScreenshot step, add that module and have it write to a buffer named `PercyAppiumSessionId` — or
name your own buffer and pass it as the `SessionIdBuffer` parameter.

Without it the step fails with a message saying exactly this, rather than letting the CLI fail later
with something less informative.

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

### Device details

Tosca exposes device information through test configuration parameters (`DeviceName`, `OSVersion`,
`AppiumServer`, …) and this SDK reads them automatically. Set these only when a parameter is missing
or wrong — for example when the device is not in the SDK's built-in dimension table, which covers
older iPhones and iPads only (see `dotnet-8/AppPercyTosca.Core/resources/devices.json`).

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
correctly-tagged ones — the step warns when this happens. Percy on Automate is unaffected: the CLI
reads the real dimensions off the live session.

### Capture

| Parameter | Description |
|---|---|
| `FullScreen` | `true` if the app is in full-screen mode |
| `FullPage` | `true` to capture the whole scrollable page (**Percy on Automate only**) |
| `ScreenLengths` | Number of screens to capture for a full page |
| `ScrollableXpath`, `ScrollableId` | Which element to scroll for a full page |
| `TopScrollviewOffset`, `BottomScrollviewOffset` | Pixels to trim while scrolling |
| `IosOptimizedFullpage` | `true` for the optimised iOS full-page algorithm |

### Regions

**Custom (pixel) regions work in both modes.** XPath regions require Percy on Automate, where the CLI
resolves them against the session itself.

| Parameter | Description |
|---|---|
| `CustomIgnoreRegions`, `CustomConsiderRegions` | `top,bottom,left,right` in pixels, one region per entry |
| `IgnoreRegionXpaths`, `ConsiderRegionXpaths` | XPath locators (**Percy on Automate only**) |

Accessibility-id regions are **not supported on Tosca** in either mode, and the step logs a warning
saying so if you set them. Resolving them is a client-side feature of the other App Percy SDKs — it
needs a driver to query, which Tosca does not expose — and Percy on Automate has no accessibility-id
option for the CLI to resolve. Use an XPath, or a custom pixel region.

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
| `Options` | Raw JSON object merged into the Percy on Automate options, for reaching a CLI option this SDK has no named parameter for |
| `Diagnose` | `true` to log everything the SDK could and could not read from Tosca |

## Troubleshooting

Set `Diagnose` to `true` on the module and run the step. It logs the hub URL, the session id, the
resolved platform and every capability found — enough to identify almost every problem, since nearly
all of them are a missing test configuration parameter or an unset session-id buffer.

For more detail, set `PERCY_LOGLEVEL=debug` before starting Tosca Commander.

Log output goes to the Percy CLI (so it appears alongside the rest of the build) and is mirrored to
`%TEMP%\percy.txt`, which is where to look when the CLI itself is what failed.

A failed snapshot does **not** fail the Tosca step: a visual check that could not run is not a
functional regression, and failing the step would stop the rest of the sheet. To change that, set
`percyOptions.ignoreErrors` to `false` in your mobile configuration.

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
| `dotnet-8/AppPercyTosca.Core` | All of the logic — CLI protocol, device metadata, option parsing, capture flows. No Tricentis dependency |
| `dotnet-8/AppPercyTosca.Core.Tests` | Unit tests. CI enforces **100% line coverage** of the Core |
| `dotnet-8/AppPercyTosca.ShimCheck` | Compile-checks the Tosca shim against stubbed Tricentis types, so the shim's source is not entirely unverified on CI |
| `dotnet-8/AppPercyTosca` | The Tosca shim — the only code that touches Tricentis assemblies. Builds only on a machine with Tosca installed |

```sh-session
# Everything CI runs; works on any OS with the .NET 8 SDK
$ dotnet test dotnet-8/AppPercyTosca.Core.sln

# The extension itself; needs Tosca Testsuite installed
$ dotnet build dotnet-8/AppPercyTosca.sln
```

The shim is deliberately thin. If you find yourself adding a decision to it, consider whether it
belongs in the Core behind `IToscaEnvironment` instead — that is the seam that makes the rest of this
testable.

See [Release.md](dotnet-8/Release.md) for how a release is cut.
