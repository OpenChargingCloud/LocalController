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

Everything a running local controller is before it is a local controller -
its log, its configuration file, name resolution and the time, who may sign in,
and the HTTP server all of that sits behind - is not here. That is
[WWCP_Node](https://github.com/OpenChargingCloud/WWCP_Node), the part a
vehicle, a charging station and a local controller share, and
`LocalController` is one `WWCPNode` with an OCPP node, a port for the charging
stations below it and a line up to the CSMS above: its sections - `ocpp`,
`ocppServer`, `csms` - go into the same configuration file, and its JSON API
below the node's `/api`.


## What it can be told

| Page | What it changes | Permission |
|------|-----------------|------------|
| Configuration | nothing - it answers "what am I running" | `readConfiguration` |
| DNS client | the name servers and how they are asked; a test lookup, of all of them or of one | `changeNetworkSettings`, `runDiagnostics` |
| NTS client | the time servers and the rules for believing them; a synchronisation, and a test of one server step by step | `changeNetworkSettings`, `runDiagnostics` |
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
clock check from being scheduled at all - and the few tests that need it on, to
read what a start writes about the clock, run on a clock whose timers never
fire. A synchronisation a test asks for goes to an address on a port nobody
listens on, and the DNS client is only ever asked what it is configured as. A
test suite that needs a name server to answer is a test suite that fails on a
train.


## Name resolution and the time servers

Both are read from `configuration.json`, in the same two sections the vehicle,
the charging station and the energy meter use, so one file can be written once
and copied between them:

```json
{
  "dns": { "enabled": true, "servers": [ "192.168.1.1" ] },
  "nts": { "enabled": true,
           "servers": [ "ptbtime1.ptb.de", "ptbtime2.ptb.de",
                        "ptbtime3.ptb.de", "ptbtime4.ptb.de" ],
           "minServers": 2,
           "checkEverySeconds": 900,
           "legalTimeAuthority": "PTB" }
}
```

That `dns` block is one name server, asked over UDP on port 53. An entry of its
`servers` is an address or a host name, or an object saying more than that -
the form the DNS page writes the list back in:

```json
{ "address": "192.168.1.1", "port": 53, "transport": "UDP", "queryTimeoutSeconds": 2 }
```

`udp://192.168.1.1:53` is how the log names a name server, not a form the file
takes. A file saying it is refused at the start, with the entry named.

That `nts` block is what a local controller asks when the file says nothing at
all: the PTB's four, of which two have to answer. Naming them changes nothing;
it is written out here because a file that names its time servers is a file
somebody can check.

Every key of the section, and what it is when absent:

| Key | Default | |
|---|---|---|
| `enabled` | `true` | whether to ask at all |
| `servers` | the four above | a list, see below |
| `minServers` | `2`, or all of them when fewer | how many must answer for the group to have a time |
| `maxDeviationSeconds` | `60` | how far apart they may be before it is written down |
| `hostname` | - | one server instead of a list |
| `ntsKEPort`, `ntpPort` | `4460`, `123` | for that one server |
| `timeoutSeconds` | `10` | per request of that one server |
| `checkEverySeconds` | `900` | how often the clock is checked |
| `legalTimeAuthority` | - | who the operator says stands behind it |
| `legalTimeToleranceSeconds` | `1` | how far off the clock may be |
| `legalTimeMaxAgeSeconds` | `3600` | how old the last check may be |

An entry of `servers` is a host name, or an object saying more than the name:

```json
{ "hostname": "time.local", "priority": 0, "ntsKEPort": 4460, "enabled": true }
```

Servers sharing a priority are **one band** and are asked together; a lower
priority is asked first. The four above share priority 0, because they are
peers - putting them in separate bands would say something about them that is
not true.

A section naming a single `hostname` and no list becomes a group of one, which
is what every file written before there were groups says, and it keeps working.
A group of one is held to a quorum of one, and a section asking two of it is
refused.

A section that is absent is not a section set to nothing: it means the file has
no opinion, and what the constructor was handed stands. The same holds key by
key - a section mentioning nothing but `enabled` leaves the servers alone
rather than quietly reducing four to one, and one mentioning nothing but
`minServers` or `maxDeviationSeconds` holds the servers the controller already
has to it. A quorum those servers could never reach is refused: at the start,
before anything is asked, and over the API, before anything is written into
the file - as is a save that would leave the file one the next start refuses.

A host name written back into this file carries the root label -
`ptbtime1.ptb.de.` - because that is the absolute form it was parsed into, and
not a stray character. What the controller prints for somebody to read drops it
again.


## The clock

`LocalController` takes a `TimeProvider` as its last constructor parameter and
hands it to everything of its own that asks what time it is: the timestamp of
every log entry, `CreatedAt` and the uptime the status resource reports. The
system clock by default; an NTS-disciplined or a fake one where a test says
so. The sign-in sessions are not among them - they belong to the HTTPExt API
and run on its clock.

It is the first thing the node below assigns, before the event log is built,
because the log stamps its entries with it - a clock set afterwards would leave
the log reading the system one, which is a log that cannot be held against
anything.

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
its time servers what time it is, measures the difference and reports it - and
leaves its own clock exactly where it was. Everything below this controller
reads the time from here, so a jump backwards would put two meter readings out
of order in a record written somewhere else entirely, with nothing in it to say
why.

`GET /api/v1/configuration/time` is that measurement, and the one word it never
guesses is "legal": that needs a claim the operator wrote into
`nts.legalTimeAuthority`, a check against those very servers, a recent one, and a
small difference. It names the group it is checked against, its servers and how
many of them have to answer. Any of those missing and the answer says `unverified` and
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
tagged `trace` plus whatever the controller's own table,
`LocalController.TraceTags`, finds in the text: OCPP going past in both
directions, and which side a line is about - the CSMS above or a charging
station below. That works in a debug build only: `Debug.WriteLine` carries `[Conditional("DEBUG")]`, so
a release build of those libraries compiles the calls away. `--no-trace`
switches the bridge off.

A program that reads commands on the same console hands the log a way to write
around the line being typed, so that an entry arriving mid-word neither lands
inside the command nor waits for it:

```csharp
localController.ShareConsoleWith(cli.WriteBlock);   // line off, entry whole, line back
```

The few things the log says on stderr about itself - a listener that failed, a
log file that cannot be written - go around the line the same way.

Three places keep it, because they answer different questions. The console
shows it to whoever started the controller, at the level they chose; the Logs
page keeps the last two thousand entries; and a `LogPath` handed to the
constructor writes every entry, down to the debug ones, into one file per UTC
day below it - `localcontroller-2026-09-24.log`, appended to and flushed after
each entry. LocalControllerCLI does that below `logs/` unless told
`--no-log-file`, since the other two are gone with the process. Nothing is
ever deleted. A file that cannot be written is said once on stderr, and the
file says how many entries it missed once it can be written again.

The entries are numbered and the number only ever grows. A browser loads a
snapshot from `/api/v1/logs`, which says how far it reaches, and then applies
everything newer from `/api/v1/events` - so a reconnect that replays a few
cached events costs bytes and nothing else.

The stream asks, before every entry it sends and at every heartbeat, whether
whoever opened it would still be let in. Once the session it was opened with
has ended - signed out, expired, or taken back with the account's others - the
stream ends too, without the entry, and the browser's next try is answered with
a 401. One opened with an API key ends the same way once the key is revoked or
has run out. One opened with Basic auth has neither, and is held to its account
instead.


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


## Your participation

This software is Open Source under the **Affero GPL 3.0 license**.
We appreciate your participation in this ongoing project, and your help to
improve it and the e-mobility ICT in general. If you find bugs, want to
request a feature or send us a pull request, feel free to use the normal
GitHub features to do so. For this please read the Contributor License
Agreement carefully and send us a signed copy or use a similar free and
open license.
