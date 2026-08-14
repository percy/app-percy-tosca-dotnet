# AppPercyTosca

App Percy visual testing for Tricentis Tosca mobile tests. Produces a distributable assembly
(`AppPercyTosca.dll`) that adds three special execution tasks to Tosca — `AppPercyStartCli`,
`AppPercyScreenshot` and `AppPercyStopCli` — for running a Percy build and taking App Percy screenshots
of the mobile app under test, without leaving Tosca.

Built for **.NET 8 / Tosca Commander 24**. For web (HTML) tests, use
[percy-tosca-dotnet](https://github.com/percy/percy-tosca-dotnet) instead — this repository is the
mobile counterpart.

## How capture works

**App Percy on BrowserStack App Automate.** That is the supported configuration — there is no separate
path for local or other-cloud devices.

Everything goes through the session's own HTTP endpoints, using two facts: the server address from your
`AppiumServer` test configuration parameter, and the Appium session id. No Tricentis API is involved,
which matters because Tosca's internals change between releases while the WebDriver protocol does not.

The hub takes the screenshots and uploads them itself, so no image data passes through Tosca. Both
single-page and full-page capture are supported.

Tosca will not pass a raw Appium command through, but the session accepts one directly — which is what
makes full page possible here.

## Requirements

- Tosca Commander 24, with Mobile Engine 3.0 installed and its mobile server running
- A session on **BrowserStack App Automate** — `AppiumServer` pointing at a BrowserStack hub
- The **Appium session id**, passed to the module (see below)
- `@percy/cli` **1.27.0 or newer**
- Node 14+ for the CLI

## Setup

Install the Percy CLI:

```sh-session
$ npm install --global @percy/cli
```

Then register the extension:

1. Copy `AppPercyTosca.dll` from the [releases](../../releases) into
   `C:\Program Files (x86)\TRICENTIS\Tosca Testsuite\Percy`
2. Add that path in Tosca Commander → Project settings → TBox → Extension loading → Extensions
3. Restart Tosca Commander

## The three tasks

The extension provides three special execution tasks, and a test case uses all three. All take
**Engine** → `Percy`; only the **SpecialExecutionTask** differs.

| SpecialExecutionTask | Where | What it does |
|---|---|---|
| `AppPercyStartCli` | once, first | Starts the Percy CLI and waits until it is serving |
| `AppPercyScreenshot` | any number of times | Takes one screenshot |
| `AppPercyStopCli` | once, last | Stops the CLI, which finalizes the build, and reports the build link |

Starting and stopping from the sheet means a run no longer depends on someone having typed
`percy app:exec:start` in a terminal first. That mattered more than it sounds: forgetting is not a loud
failure — snapshots are swallowed, every step passes, and the build comes out empty.

`AppPercyStartCli` **fails the step** if the CLI does not come up, for the same reason. `AppPercyStopCli`
passes even if it cannot stop cleanly, since by then the snapshots are already uploaded and Percy
finalizes the build on its own timeout; failing the last step of an otherwise good run would say
something less true than passing it.

### Starting the CLI

Create a module with **Engine** → `Percy` and **SpecialExecutionTask** → `AppPercyStartCli`. Rows:

| Row | Value | Notes |
|---|---|---|
| `PercyToken` | your **App** project token | Or leave it off and set `PERCY_TOKEN` in the environment |
| `Branch` | e.g. `release-24` | Optional, but see below — usually worth setting |
| `CliCommand` | e.g. `C:\Users\you\AppData\Roaming\npm\percy` | Optional. Only needed when `percy` is not on the PATH Tosca sees |

Use an **App** project's token. One starting with `auto_` is an Automate project token and selects a mode
this SDK does not support — the step reports that plainly rather than failing obscurely.

The token is passed to the CLI through its environment, never on its command line, and is redacted if it
ever reaches a log by another route. It is still a secret in a test asset, so prefer an encrypted TCP or
the environment on a shared workspace.

#### Naming the branch

Percy groups builds into a history by branch, and the CLI takes the branch from the git repository it was
started in. A Tosca machine usually has none — so without a value every run lands on whatever Percy falls
back to, and no two builds compare against each other. `Branch` sets it.

It belongs on **this** task and nowhere else: the build is created when the CLI starts, so a branch given
to a screenshot step arrives after the decision has been made. Setting `PERCY_BRANCH` in the environment
works too, and the row wins over it.

Readiness is judged two ways: the CLI printing `Percy has started!`, **and** its healthcheck answering.
The log line alone would report success to a sheet whose snapshots are all about to be dropped. First
start on a fresh machine can take a couple of minutes, because the CLI downloads a browser.

### Stopping the CLI

**Engine** → `Percy`, **SpecialExecutionTask** → `AppPercyStopCli`. It needs no rows — `CliCommand` and
`PercyToken` are accepted if the CLI is not on the PATH Tosca sees.

### The screenshot module

Create a module with **Engine** → `Percy` and **SpecialExecutionTask** → `AppPercyScreenshot`, then add
each parameter you want as a row with **Parameter** → `True`.

Getting these exactly right matters — a mistyped task name or engine surfaces as
`The SpecialExecutionTask 'x' was not found for engine 'y'`, which reads like a broken install rather
than a typo. The minimum viable module is two rows:

| Row | Value | Notes |
|---|---|---|
| `ScreenshotName` | e.g. `Home` | Required; must be unique per screenshot |
| `SessionId` | `{B[PercyAppiumSessionId]}` | Required; the buffer the *Get Appium Session Id* module wrote |

Or skip the typing: **`AppPercyScreenshot.tsu`** ships with each release. Import that subset and you get
a module with the engine, task name and rows already correct — which removes the whole class of problem
above, since a mistyped task name is indistinguishable from a broken install.

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

`ScreenshotName` and `SessionId` are required. Everything else is optional; a step with just those two
takes a single-screen snapshot of the current screen.

### Naming

| Parameter | Description |
|---|---|
| `ScreenshotName` (**required**) | The screenshot's name; must be unique to each screenshot |
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

### Logging

Tosca cannot set environment variables for the process it runs in, so these have module parameters that
do the same job. A parameter wins over the variable of the same meaning.

| Parameter | Variable it stands in for | Effect |
|---|---|---|
| `LogLevel` | `PERCY_LOGLEVEL` | `debug` for verbose SDK logging |
| `LogFile` | `PERCY_LOG_FILE` | Where the log file copy is written |

`PERCY_TOKEN` is handled by `AppPercyStartCli`'s `PercyToken` row instead — see
[Starting the CLI](#starting-the-cli). It reaches the CLI through the child process's environment, so it
belongs on that task rather than on a snapshot.

The same mechanism backs a handful of switches this SDK reads for diagnosis and internal testing, which
are **not supported configuration** and are deliberately not documented individually — if you have been
asked to set one, you will have been told which. Support may ask for `LogLevel`; nothing else here is
something a test sheet should be choosing.

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
| has `assembly loaded:` but nothing else | The DLL loaded but the task was not registered, or no step ran. Compare the module's `Engine` and `SpecialExecutionTask` values against `Percy` and one of `AppPercyStartCli` / `AppPercyScreenshot` / `AppPercyStopCli` |
| has later lines | The extension is running; read on for the actual problem |

A failed snapshot does **not** fail the Tosca step: a visual check that could not run is not a
functional regression, and failing the step would stop the rest of the sheet. To change that, add a
test configuration parameter `percy.ignoreErrors` with the value `false`. A TCP named `percy.enabled`
set to `false` turns Percy off entirely without editing any test sheet.

(The other App Percy SDKs use a nested `percyOptions` capability for this. That shape cannot come from
a TCP, which is why the flat `percy.*` spellings are the ones to use on Tosca.)

### Environment variables

On Tosca, prefer the module parameters — see [Logging](#logging) — since Tosca cannot set environment
variables for the process it runs in.

| Variable | Effect |
|---|---|
| `PERCY_TOKEN` | Your Percy project token. Read by the CLI; `AppPercyStartCli` forwards its `PercyToken` row here |
| `PERCY_BRANCH` | The branch a build belongs to. Read by the CLI; `AppPercyStartCli` forwards its `Branch` row here |
| `PERCY_LOGLEVEL=debug` | Verbose SDK logging; same as the `LogLevel` parameter |
| `PERCY_LOG_FILE` | Where the log file copy is written (default `%TEMP%\percy.txt`); same as `LogFile` |

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
