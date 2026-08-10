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

## Requirements

- Tosca Commander 24, with Mobile Engine 3.0 installed and its mobile server running
- A session on **BrowserStack App Automate** — `AppiumServer` pointing at a BrowserStack hub
- The **Appium session id**, passed to the module (see below)
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
than a typo. The minimum viable module is two rows:

| Row | Value | Notes |
|---|---|---|
| `SnapshotName` | e.g. `Home` | Required; must be unique per snapshot |
| `SessionId` | `{B[PercyAppiumSessionId]}` | Required; the buffer the *Get Appium Session Id* module wrote |

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
string — documented Tosca behaviour, rather than this SDK reaching into Tosca's buffer store.

If the `SessionId` row is left off, the SDK falls back to reading the `PercyAppiumSessionId` buffer
itself. That works, but it reflects into Tosca internals whose shape is not published, so name the
buffer `PercyAppiumSessionId` and prefer the row. Without either, capture cannot reach the device and
the step says so.

## Parameters

`SnapshotName` and `SessionId` are required. Everything else is optional; a step with just those two
takes a single-screen snapshot of the current screen.

### Naming

| Parameter | Description |
|---|---|
| `SnapshotName` (**required**) | The snapshot name; must be unique to each snapshot |
| `Labels` | Comma-separated labels |

### Device details

There are no parameters for these. The device name, OS, version, screen size, bar heights and
orientation are read from the App Automate session, which knows the device that was actually
allocated — a module parameter could only disagree with it, and a stale or mistyped one silently
splits a Percy baseline in a way that looks like a real visual change.

The one gap worth knowing: the built-in dimension table
(`AppPercyTosca.Core/resources/devices.json`) covers older iPhones and iPads only, so an unlisted iOS
device falls back to the session's viewport.

### Capture

| Parameter | Description |
|---|---|
| `FullScreen` | `true` if the app is in full-screen mode |
| `FullPage` | `true` to capture the whole scrollable page |
| `ScreenLengths` | Number of screens to capture for a full page |
| `TopScrollviewOffset` | Pixels to trim from the top of each full-page tile before stitching |
| `BottomScrollviewOffset` | Pixels to trim from the bottom of each full-page tile |
| `IosOptimizedFullpage` | The optimised iOS full-page algorithm |

### Regions

Regions are given in **pixel coordinates**.

| Parameter | Description |
|---|---|
| `CustomIgnoreRegions`, `CustomConsiderRegions` | `top,bottom,left,right` in pixels, one region per entry |

There are no XPath or accessibility-id region parameters. Resolving a locator needs a driver that can
be queried for elements, which a Tosca mobile session does not expose to an extension — so such a row
could never have resolved to anything, and offering one would only invite regions that silently do
nothing. Give pixel coordinates.

Regions are separated by **newlines** if the value contains any, otherwise by **semicolons**. Inside
one region, commas separate the four numbers:

```
0,100,0,1080; 2200,2340,0,1080
```

### Escape hatches

| Parameter | Description |
|---|---|
| `SessionId` | The Appium session id. Use `{B[PercyAppiumSessionId]}` to pass a buffer written by the *Get Appium Session Id* module |

### Settings that are otherwise environment variables

Tosca cannot set environment variables for the process it runs in, so each of these has a module
parameter that does the same job. A parameter wins over the variable of the same meaning.

| Parameter | Variable it stands in for | Effect |
|---|---|---|
| `LogLevel` | `PERCY_LOGLEVEL` | `debug` for verbose SDK logging |
| `LogFile` | `PERCY_LOG_FILE` | Where the log file copy is written |
| `CliApi` | `PERCY_CLI_API` | CLI address (default `http://localhost:5338`) |
| `TmpDir` | `PERCY_TMP_DIR` | Where screenshot tiles are written |
| `ForceFullPage` | `FORCE_FULL_PAGE` | `true` to force the full-page path |
| `DisableRemoteUploads` | `PERCY_DISABLE_REMOTE_UPLOADS` | `true` to keep tiles local |
| `EnablePercyDev` | `PERCY_ENABLE_DEV` | `true` to target the dev project |
| `AutomateDomain` | `AA_DOMAIN` | Domain fragment marking a host as App Automate |

`PERCY_TOKEN` is not in this table: the CLI reads it, not this SDK, so a parameter for it would be
silently ignored. It has to be set where the CLI can see it.

## Troubleshooting

Add a `LogLevel` row with the value `debug` to the Percy module step. Nearly every problem here is a
missing test configuration parameter or an unset session id, and each of those reports itself by name
in the log. (`PERCY_LOGLEVEL=debug` as a **system** environment variable does the same thing, if you
can set one and restart Commander.)

Log output goes to the Percy CLI (so it appears alongside the rest of the build) and is mirrored to
**`%TEMP%\percy.txt`**, which is where to look when the CLI itself is what failed — the CLI forward
drops lines silently when the CLI stops answering, and the file copy does not. Every line is
timestamped. Set the `LogFile` parameter (or `PERCY_LOG_FILE`) to pin it somewhere else — worth doing,
because `%TEMP%` resolves per-account and Tosca may not run as you.

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

Every variable below except `PERCY_TOKEN` also has a module parameter — see
[Settings that are otherwise environment variables](#settings-that-are-otherwise-environment-variables),
which is the route to use on Tosca.

| Variable | Effect |
|---|---|
| `PERCY_TOKEN` | Your Percy project token (read by the CLI; no parameter equivalent) |
| `PERCY_LOGLEVEL=debug` | Verbose SDK logging |
| `PERCY_LOG_FILE` | Where the log file copy is written (default `%TEMP%\percy.txt`) |
| `PERCY_CLI_API` | CLI address (default `http://localhost:5338`) |
| `PERCY_TMP_DIR` | Where screenshot tiles are written (default the system temp directory) |
| `FORCE_FULL_PAGE`, `PERCY_DISABLE_REMOTE_UPLOADS`, `PERCY_ENABLE_DEV`, `AA_DOMAIN` | See the parameter table above |

## Development

The repository is split so that almost all of it is testable without Tosca:

| Project | What it is |
|---|---|
| `AppPercyTosca.Core` | All of the logic — CLI protocol, device metadata, option parsing, capture flows. No Tricentis dependency |
| `AppPercyTosca.Core.Tests` | Unit tests. CI enforces **100% line coverage** of the Core |
| `AppPercyTosca.ShimCheck` | Compile-checks the Tosca shim against stubbed Tricentis types, so the shim's source is not entirely unverified on CI |
| `AppPercyTosca` | The Tosca shim — the only code that touches Tricentis assemblies. Builds only on a machine with Tosca installed |

```sh-session
# The two things CI gates on; both work on any OS with the .NET 8 SDK
$ dotnet test AppPercyTosca.Core.sln
$ dotnet format AppPercyTosca.Core.sln --verify-no-changes

# Apply whatever the format check would have complained about
$ dotnet format AppPercyTosca.Core.sln

# The extension itself; needs Tosca Testsuite installed
$ dotnet build AppPercyTosca.sln
```

Formatting rules live in `.editorconfig` rather than being inherited from the SDK, so an SDK upgrade
cannot start failing CI over a preference nobody chose. Whitespace and layout are enforced; naming
and expression preferences are `silent`, so they guide in an IDE without gating a build. CI also
rejects runs of blank lines, which `dotnet format` does not catch — see the `lint` job.

The shim is deliberately thin. If you find yourself adding a decision to it, consider whether it
belongs in the Core behind `IToscaEnvironment` instead — that is the seam that makes the rest of this
testable.

See [Release.md](Release.md) for how a release is cut.
