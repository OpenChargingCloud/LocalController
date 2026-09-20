# LocalController

One EV charging local controller, with a web interface in front of it: a C#
HTTP backend built on [Hermod](https://github.com/Vanaheimr/Hermod), and a
frontend of HTML, SCSS and TypeScript bundled by webpack and embedded into the
assembly - so the local controller is one binary to deploy and needs nothing
installed beside it.

Nothing is rendered on the server. The browser loads one bundle and talks to
the controller over a JSON API and one Server-Sent Events stream.

```
  browser  ──  GET /                       the SPA stub and the bundle
           ──  POST /ext/login             the session cookie
           ──  GET  /api/v1/auth/me        what that account may do here
           ──  GET  /api/v1/configuration  what the controller is made of
           ──  GET  /api/v1/logs           what happened up to now
           ──  GET  /api/v1/events         and everything from now on (SSE)
```

A local controller sits between a CSMS above it and the charging stations
below, and is usually the only thing in a car park with a keyboard within reach
of it. That is what the web interface is for: it is the one place where
somebody can see what the box is doing without having a back end to ask.


## What it can be told

| Page | What it changes | Permission |
|------|-----------------|------------|
| Configuration | nothing - it answers "what am I running" | `readConfiguration` |
| DNS client | the name servers and how they are asked; a test lookup | `changeNetworkSettings`, `runDiagnostics` |
| NTS client | the time server and how it is asked; a synchronisation | `changeNetworkSettings`, `runDiagnostics` |
| Logs | nothing - it reads | `readConfiguration` |

Everything on the DNS and NTS pages takes effect the moment it is saved, for
everything inside the controller that resolves a name or reads a clock, and is
written to `configuration.json` in the same breath - the file first, because a
change that was applied but not written down disappears at the next start
without anybody noticing.

What this controller says it is in OCPP - its node id, vendor, model, serial
number - is read from the `ocpp` section of that file at the start and is
deliberately *not* changeable while running: an identification is what a CSMS
knows this controller by, and changing it under a live connection would not
rename the controller, it would make it a second one nobody is talking to.


## Running it

From the repository that has this one as a submodule
([LocalControllerCLI](https://github.com/OpenChargingCloud/LocalControllerCLI)):

```
dotnet run --project LocalControllerCLI
```

At the first start there are no accounts, so the controller makes one up for
the user `root`, keeps it under `accounts/` and prints the password once:

```
  ┌─ First start: there were no accounts, so one was made up for you ─────────
  │  user      root
  │  password  QBDD77Lc7HseB-xORuuw8RpX
  │  It is shown here once and kept only as a hash. Write it down.
  └───────────────────────────────────────────────────────────────────────────
```

Then open http://127.0.0.1:2350/ and sign in.

Port 2350 and not 2348: an OpenChargingCloud charging station uses 2348 and
2349, and a controller and a station are often tried out on the same bench.


## Building

`dotnet build` builds the frontend too: `LocalController.csproj` runs
`npm ci` (only when `Frontend/node_modules` is missing) and `npm run build`
(only when something under `Frontend/src` changed), then embeds every file of
`Frontend/dist` as a manifest resource named
`cloud.charging.open.LocalController.HTTPRoot.<path>` - which is what Hermod's
`EmbeddedContentSource` reads and `MapSinglePageApplication` serves.

```
dotnet build                            the whole thing
dotnet build -p:SkipFrontendBuild=true  backend only, reusing the existing dist/ -
                                        and where there is none, no web interface
                                        at all, which is a warning and not an error
npm run watch     (in Frontend/)        rebuild the bundle as it is edited
npm run typecheck (in Frontend/)        tsc --noEmit
```

While editing the frontend, start the controller with `--frontend
libs/LocalController/LocalController/Frontend/dist` so that it serves the
directory `npm run watch` writes into: a reload in the browser then shows the
change, without rebuilding the C# side.


## The tests

```
dotnet test libs/LocalController/LocalControllerTests
```

They start real controllers and talk to them over HTTP the way the browser
does: the bundle is served, the sign-in works, a change to the name servers
reaches both the shared DNS client and the file, the log filters, the event
stream delivers, and a controller that is told to stop stops.

Each test gets a controller of its own, on a port the operating system has
just confirmed is free and with its own directory for the two files a
controller writes - so they neither fight with each other nor with a
controller somebody has running on 2350 while they work.

**They never touch the network.** The configuration written before each
controller is built switches the time client off, which is what stops the
clock check from being scheduled at all, and the DNS client is only ever asked
what it is configured as. A test suite that needs a name server to answer is a
test suite that fails on a train.


## The clock

`LocalController` takes a `TimeProvider` as its last constructor parameter and
hands it to everything of its own that asks what time it is: the timestamp of
every log entry, `CreatedAt` and the uptime the status resource reports. The
system clock by default; an NTS-disciplined or a fake one where a test says
so. The sign-in sessions are not among them - they belong to the HTTPExt API
and run on its clock.

It is assigned first in the constructor, before the event log is built, because
the log stamps its entries with it - a clock set afterwards would leave the log
reading the system one, which is a log that cannot be held against anything.

```csharp
sealed class FixedClock(DateTimeOffset Start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = Start;
    public override DateTimeOffset GetUtcNow() => Now;
}

var clock           = new FixedClock(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
var localController = new LocalController(TimeProvider: clock);

var at    = localController.CreatedAt;               // 2000-01-01T00:00:00Z
clock.Now = clock.Now.AddHours(13);
var up    = localController.TimeProvider.GetUtcNow() - at;   // 13 hours of uptime
```

**The clock is never set from NTS.** Every fifteen minutes the controller asks
its time server what time it is, measures the difference and reports it - and
leaves its own clock exactly where it was. Everything below this controller
reads the time from here, so a jump backwards would put two meter readings out
of order in a record written somewhere else entirely, with nothing in it to say
why.

`GET /api/v1/configuration/time` is that measurement, and the one word it never
guesses is "legal": that needs a claim the operator wrote into
`nts.legalTimeAuthority`, a check against that very server, a recent one, and a
small difference. Any of those missing and the answer says `unverified` and
names which one in `why`.


## The log

Every entry carries a timestamp, a level (`debug`, `info`, `notice`,
`warning`, `error`, `critical`) and any number of tags (`http`, `ocpp`, `dns`,
`nts`, `web`, `auth`, ...). The Logs page filters on both - the level counts as
a tag, so `critical` and `ocpp` can be picked together.

Anything in the local controller can write to it:

```csharp
localController.Log.Warning("The CSMS did not answer the BootNotification.", "ocpp");
```

What the libraries below write through Illias' `DebugX` lands there too,
tagged `trace` plus whatever `TraceBridge` recognises in the text. That works
in a debug build only: `Debug.WriteLine` carries `[Conditional("DEBUG")]`, so
a release build of those libraries compiles the calls away. `--no-trace`
switches the bridge off.

The entries are numbered and the number only ever grows. A browser loads a
snapshot from `/api/v1/logs`, which says how far it reaches, and then applies
everything newer from `/api/v1/events` - so a reconnect that replays a few
cached events costs bytes and nothing else.


## Who may open it

The accounts are Hermod's `HTTPExtAPI`, mounted at `/ext` and kept under
`accounts/`: the users, their passwords as PBKDF2-SHA256 PHC strings, the
sessions and the API keys. Signing in happens at `POST /ext/login` - that is
the only place that can check a password - and the cookie it sets is what this
controller's own API reads. Basic auth and API keys work just as well, because
Hermod offers all three.

What somebody may do comes from the user groups they are in. Each group is one
role, under the same name:

| Role | May |
|------|-----|
| `viewer` | read the configuration and the log |
| `cpo` | that, and change the name and time servers, and test them |
| `systemadmin` | everything this controller can be told |

The three groups are made at every start, so a group deleted by hand does not
leave a role nobody can ever hold again. A group that is not one of these
grants nothing - a role this controller has never heard of is a role it cannot
enforce.

Membership is asked on every request rather than remembered at the sign-in, so
taking somebody out of a group takes effect on their next request. The
permissions travel to the browser so a page can grey out what somebody may not
do - a courtesy, not a lock: every request is checked again on arrival.
