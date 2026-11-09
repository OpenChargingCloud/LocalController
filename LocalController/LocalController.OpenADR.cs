/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of LocalController <https://github.com/OpenChargingCloud/LocalController>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.protocols.OpenADRv3;

using cloud.charging.open.LocalController.Configuration;
using cloud.charging.open.LocalController.OpenADR;

using OpenADRValueType = cloud.charging.open.protocols.OpenADRv3.ValueType;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The grid operator this local controller's charging pool is connected
    /// behind: this controller is a VEN of its OpenADR 3 VTN, reads the limits
    /// it sends - how much the pool may draw, how much it may feed in - and
    /// reports back on each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked, not told.</b> OpenADR 3 lets a VEN subscribe to be called back,
    /// but a local controller in a car park is seldom somewhere a grid operator
    /// can call: so it asks, every <see cref="OpenADRConfiguration.PollEvery"/>,
    /// for the events meant for it that are not over yet.
    /// </para>
    /// <para>
    /// <b>Shown, and reported on.</b> The limits are kept, logged as they come
    /// and go, and shown at the prompt and by the JSON API; the lowest one in
    /// force of each kind is what this controller is held to. On every event it
    /// reads it sends one report, saying what it took from it. Which reports it
    /// has sent is asked of the VTN after every start, so a restart sends none
    /// twice.
    /// </para>
    /// <para>
    /// <b>A grid operator that cannot be reached is logged, not fatal</b>, as a
    /// CSMS that cannot be: the limits read last stay until they are over, and
    /// the next poll tries again.
    /// </para>
    /// </remarks>
    public partial class LocalController
    {

        #region Data

        /// <summary>
        /// What the VTN is to this controller where the node judges its
        /// certificate.
        /// </summary>
        public const String  OpenADRService  = "openadr";

        private          OpenADRConfiguration      openADRSettings  = new OpenADRConfiguration().Effective();
        private readonly SemaphoreSlim             openADRGate      = new (1, 1);
        private readonly Lock                      openADRLock      = new ();

        private VENClient?                         venClient;
        private CancellationTokenSource?           venStop;
        private Task?                              venLoop;
        private Boolean                            venMayRun;

        private IReadOnlyList<GridLimit>           gridLimits       = [];
        private HashSet<String>?                   reportedEvents;
        private DateTimeOffset?                    lastPollAt;
        private DateTimeOffset?                    lastReadAt;
        private String?                            openADRProblem;
        private Boolean                            venSignedIn;

        #endregion

        #region Properties

        /// <summary>
        /// The client secret this local controller signs in at the VTN with.
        /// </summary>
        public VTNCredentials        OpenADRLogin       { get; private set; } = default!;

        /// <summary>
        /// What the VTN connection is configured as.
        /// </summary>
        public OpenADRConfiguration  OpenADRSettings
            => openADRSettings;

        /// <summary>
        /// Whether this local controller asks a VTN for limits at all.
        /// </summary>
        public Boolean               OpenADREnabled
            => openADRSettings.Enabled == true;

        /// <summary>
        /// Whether the last poll signed in and read the events.
        /// </summary>
        public Boolean               OpenADRSignedIn
            => venSignedIn;

        /// <summary>
        /// What went wrong the last time, or null when nothing has.
        /// </summary>
        public String?               OpenADRLastProblem
            => openADRProblem;

        /// <summary>
        /// When the VTN was last asked, and when it last answered with the
        /// events.
        /// </summary>
        public (DateTimeOffset? Asked, DateTimeOffset? Read)  OpenADRLastPoll
            => (lastPollAt, lastReadAt);

        /// <summary>
        /// Where the token is had, and when the one held expires - null for
        /// either before it is known.
        /// </summary>
        public (Uri? TokenURL, DateTimeOffset? Expires)  OpenADRToken
            => (venClient?.TokenURL, venClient?.TokenExpires);

        /// <summary>
        /// Every limit the grid operator sent that is not over, in the order
        /// they begin.
        /// </summary>
        public IReadOnlyList<GridLimit>  GridLimits
        {
            get
            {
                var now = TimeProvider.GetUtcNow();
                return [ .. gridLimits.Where(limit => !limit.IsOverAt(now)) ];
            }
        }

        /// <summary>
        /// How much this charging pool may draw from the grid now, or null where
        /// no limit says.
        /// </summary>
        public GridLimit?  ImportLimitNow
            => GridLimit.InForce(gridLimits, GridLimit.ImportCapacityLimit, TimeProvider.GetUtcNow());

        /// <summary>
        /// How much it may feed into the grid now, or null where no limit says.
        /// </summary>
        public GridLimit?  ExportLimitNow
            => GridLimit.InForce(gridLimits, GridLimit.ExportCapacityLimit, TimeProvider.GetUtcNow());

        #endregion


        #region (private) BuildOpenADRConnection(Configuration)

        /// <summary>
        /// Take in the settings and the secret. Nothing is asked here.
        /// </summary>
        private void BuildOpenADRConnection(OpenADRConfiguration? Configuration)
        {

            openADRSettings  = (Configuration ?? new OpenADRConfiguration()).Effective();

            // Beside the configuration file, as the CSMS credentials are.
            var directory    = Path.GetDirectoryName(ConfigFile.Path) ?? ".";

            OpenADRLogin     = new VTNCredentials(Path.Combine(directory, VTNCredentials.DefaultFileName));

            OpenADRLogin.OnNotice += (level, message) => Log.Log(level, message, "openadr", "auth");

            if (!OpenADRLogin.TryLoad(out var error))
                Log.Critical($"The OpenADR client secret could not be read: {error}", "openadr", "auth");

            Log.Info(
                openADRSettings.Enabled == true
                    ? $"This local controller asks the OpenADR VTN at {openADRSettings.VTNURL} for the limits of its grid operator, as '{openADRSettings.ClientID}'."
                    : "This local controller asks no OpenADR VTN for limits; it is switched off in the 'openADR' section of the configuration.",
                "openadr"
            );

        }

        #endregion

        #region (private) StartOpenADR() / StopOpenADR()

        /// <summary>
        /// Begin asking the VTN, when this controller is meant to.
        /// </summary>
        private void StartOpenADR()
        {

            venMayRun = true;

            if (openADRSettings.Enabled != true)
                return;

            if (openADRSettings.VTNURL is not String url || openADRSettings.ClientID is not String clientId)
                return;

            if (!OpenADRLogin.HasSecret)
            {
                openADRProblem = $"This local controller has no client secret to sign in at {url} with. " +
                                  "Set the one the grid operator gave it: 'openADR import <file>' at the console, or PUT /api/v1/configuration/openADR/credentials.";
                Log.Warning(openADRProblem, "openadr", "auth");
            }

            if (openADRSettings.InTheClear)
                Log.Warning($"The OpenADR VTN at {url} is spoken to unencrypted: the client secret and the bearer token cross the network as they are. " +
                             "An https:// URL keeps them to this local controller and the grid operator.",
                            "openadr", "auth");

            var vtn     = new Uri(url);
            var host    = vtn.Host;

            var handler = NameResolution.HTTPHandler(DNSClient);

            // The VTN's certificate is judged by the node, as the CSMS's is:
            // issued for its name, chaining to a root this machine trusts or to
            // a TLS root of this controller's store, and remembered.
            handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, errors)
                => JudgeServer(OpenADRService, host, null, certificate as X509Certificate2, chain, errors, Evidence: false).Accepted;

            var client  = new VENClient(vtn,
                                        clientId,
                                        OpenADRLogin.Secret,
                                        handler,
                                        openADRSettings.RequestTimeout ?? OpenADRConfiguration.DefaultRequestTimeout,
                                        TimeProvider);

            client.OnSignedIn += (expires, renewed) => {
                var minutes = Math.Round((expires - TimeProvider.GetUtcNow()).TotalMinutes);
                Log.Info(renewed
                             ? $"This local controller renewed its token at the OpenADR VTN; it holds for {minutes:0} minute(s)."
                             : $"This local controller signed in at the OpenADR VTN at {url} as '{clientId}'; the token holds for {minutes:0} minute(s).",
                         "openadr", "auth");
            };

            var stop    = new CancellationTokenSource();

            lock (openADRLock)
            {
                venClient       = client;
                venStop         = stop;
                reportedEvents  = null;
                venLoop         = Task.Run(() => PollLoop(stop.Token));
            }

        }

        /// <summary>
        /// Stop asking, and wait for a poll under way to end.
        /// </summary>
        private async Task StopOpenADR()
        {

            venMayRun = false;

            Task?                     loop;
            CancellationTokenSource?  stop;
            VENClient?                client;

            lock (openADRLock)
            {
                (loop, stop, client)  = (venLoop, venStop, venClient);
                (venLoop, venStop)    = (null, null);
            }

            if (stop is null)
                return;

            stop.Cancel();

            try
            {
                if (loop is not null)
                    await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            { }
            catch (Exception e)
            {
                Log.Warning($"Asking the OpenADR VTN did not end cleanly: {e.Message}", "openadr");
            }

            // Under the gate, so that a poll asked for at the prompt that is
            // still under way does not find its client gone.
            await openADRGate.WaitAsync().ConfigureAwait(false);

            try
            {
                client?.Dispose();
                venSignedIn = false;
            }
            finally
            {
                openADRGate.Release();
            }

            stop.Dispose();

        }

        #endregion

        #region (private) PollLoop(CancellationToken)

        private async Task PollLoop(CancellationToken CancellationToken)
        {

            while (!CancellationToken.IsCancellationRequested)
            {

                await PollOpenADR(CancellationToken).ConfigureAwait(false);

                try
                {
                    await Task.Delay(openADRSettings.PollEvery ?? OpenADRConfiguration.DefaultPollEvery,
                                     TimeProvider,
                                     CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

            }

        }

        #endregion

        #region PollOpenADR(CancellationToken)

        /// <summary>
        /// Ask the VTN now: sign in where needed, read the events meant for this
        /// controller, keep their limits, and report on the ones not reported
        /// on yet. Answers what went wrong, or null.
        /// </summary>
        public async Task<String?> PollOpenADR(CancellationToken CancellationToken = default)
        {

            if (openADRSettings.Enabled != true)
                return "This local controller asks no OpenADR VTN; it is switched off.";

            await openADRGate.WaitAsync(CancellationToken).ConfigureAwait(false);

            try
            {

                if (venClient is not VENClient client || !venMayRun)
                    return "This local controller is not running, and asks no VTN.";

                if (!OpenADRLogin.HasSecret)
                    return openADRProblem;

                lastPollAt = TimeProvider.GetUtcNow();

                try
                {

                    #region Which events were reported on already: asked once after every start

                    if (reportedEvents is null)
                    {
                        var reports    = await client.GetReports(CancellationToken).ConfigureAwait(false);
                        reportedEvents = [ .. reports.Select(report => report.Value<String>("eventID")).OfType<String>() ];
                    }

                    #endregion

                    var events      = await client.GetEvents(CancellationToken).ConfigureAwait(false);

                    venSignedIn     = true;
                    lastReadAt      = TimeProvider.GetUtcNow();

                    var (limits, parsed) = LimitsOf(events);

                    TakeLimits(limits);

                    #region One report on every event, once

                    foreach (var gridEvent in parsed)
                    {

                        var id = gridEvent.Id!.Value.ToString();

                        if (reportedEvents.Contains(id))
                            continue;

                        var ofIt = limits.Where(limit => limit.EventId.ToString() == id).ToArray();

                        try
                        {

                            await client.PostReport(ReportOn(gridEvent, ofIt, client.ClientID), CancellationToken).ConfigureAwait(false);

                            Log.Info($"This local controller reported to the grid operator on the event '{id}'.", "openadr", "report");

                        }
                        // A report the VTN says no to - an event cancelled in
                        // between, a report it does not take - is said once and
                        // not sent again: asking again would get the same no at
                        // every poll. A VTN that is not there, or one that does
                        // not take the token, is the poll's problem, below.
                        catch (VTNException e) when (e.Status is System.Net.HttpStatusCode status &&
                                                     (Int32) status is >= 400 and < 500 &&
                                                     status is not System.Net.HttpStatusCode.Unauthorized
                                                           and not System.Net.HttpStatusCode.RequestTimeout
                                                           and not System.Net.HttpStatusCode.TooManyRequests)
                        {
                            Log.Warning($"The report on the event '{id}' was refused, and is not sent again: {e.Message}", "openadr", "report");
                        }

                        reportedEvents.Add(id);

                    }

                    #endregion

                    if (openADRProblem is not null)
                    {
                        openADRProblem = null;
                        Log.Notice($"The OpenADR VTN at {client.VTNURL} answers again.", "openadr");
                    }

                    return null;

                }
                catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {

                    var problem = e is VTNException
                                      ? e.Message
                                      : $"Asking the OpenADR VTN at {client.VTNURL} failed: {e.Message}";

                    if (e is VTNException { Status: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest })
                    {
                        venSignedIn = false;
                        client.SignOut();
                    }

                    // Said once, and not at every poll of a VTN that is down.
                    if (problem != openADRProblem)
                    {
                        openADRProblem = problem;
                        Log.Warning(problem + " The limits read last stay until they are over; it is asked again.", "openadr");
                    }

                    return problem;

                }

            }
            finally
            {
                openADRGate.Release();
            }

        }

        #endregion

        #region (private) LimitsOf(Events)

        /// <summary>
        /// The limits of the events as the VTN listed them, and the events that
        /// could be read at all.
        /// </summary>
        private (List<GridLimit> Limits, List<Event> Events) LimitsOf(IEnumerable<JObject> Events)
        {

            var limits  = new List<GridLimit>();
            var parsed  = new List<Event>();

            foreach (var json in Events)
            {

                if (!Event.TryParse(json, out var gridEvent, out var error))
                {
                    LogOnce($"An event of the OpenADR VTN could not be read, and was passed over: {error}");
                    continue;
                }

                if (gridEvent.Id is null)
                {
                    LogOnce("An event of the OpenADR VTN without an identification was passed over.");
                    continue;
                }

                parsed.Add(gridEvent);
                limits.AddRange(GridLimit.Of(gridEvent, out var problems));

                foreach (var problem in problems)
                    LogOnce(problem);

            }

            return (limits, parsed);

            void LogOnce(String Problem)
            {
                if (!passedOver.Contains(Problem))
                {
                    passedOver.Add(Problem);
                    Log.Warning(Problem, "openadr");
                }
            }

        }

        private readonly HashSet<String> passedOver = [];

        #endregion

        #region (private) TakeLimits(Limits)

        /// <summary>
        /// Keep the limits read, and say which came and which went.
        /// </summary>
        private void TakeLimits(IReadOnlyList<GridLimit> Limits)
        {

            var now       = TimeProvider.GetUtcNow();
            var before    = gridLimits;

            var importWas = GridLimit.InForce(before, GridLimit.ImportCapacityLimit, now);
            var exportWas = GridLimit.InForce(before, GridLimit.ExportCapacityLimit, now);

            gridLimits    = [ .. Limits.OrderBy(limit => limit.Start).ThenBy(limit => limit.EventId.ToString()).ThenBy(limit => limit.Interval) ];

            foreach (var limit in gridLimits.Where(limit => !before.Contains(limit)))
                Log.Notice($"The grid operator sent a limit: {limit}{(limit.EventName is not null ? $" ('{limit.EventName}')" : "")}, event '{limit.EventId}'.",
                           "openadr", "limit");

            // Withdrawn, rather than over: those simply end.
            foreach (var limit in before.Where(limit => !gridLimits.Contains(limit) && !limit.IsOverAt(now)))
                Log.Notice($"The grid operator withdrew a limit: {limit}, event '{limit.EventId}'.",
                           "openadr", "limit");

            SayWhatHolds(GridLimit.ImportCapacityLimit, importWas, ImportLimitNow, "draw from");
            SayWhatHolds(GridLimit.ExportCapacityLimit, exportWas, ExportLimitNow, "feed into");

            void SayWhatHolds(String Kind, GridLimit? Was, GridLimit? Is, String Verb)
            {

                if (Was?.KW == Is?.KW)
                    return;

                Log.Notice(Is is not null
                               ? $"This charging pool may {Verb} the grid at most {Is.KW:0.###} kW now ({Kind}{(Is.End is DateTimeOffset end ? $", until {end.UtcDateTime:HH:mm} UTC" : "")})."
                               : $"No {Kind} of the grid operator holds any more.",
                           "openadr", "limit");

            }

        }

        #endregion

        #region (private) ReportOn(Event, Limits, ClientName)

        /// <summary>
        /// The report on an event: what this local controller took from it - each
        /// limit, as the interval it is of, for this controller as the resource.
        /// </summary>
        private Report ReportOn(Event                   Event,
                                IReadOnlyList<GridLimit> Limits,
                                String                  ClientName)
        {

            var intervals = Limits.GroupBy(limit => limit.Interval).
                                   Select (group => new Interval(Interval_Id.Parse(group.Key),
                                                                 [ .. group.Select(limit => new ValuesMap(OpenADRValueType.Parse(limit.Kind), [ limit.KW ])) ],
                                                                 new IntervalPeriod(group.First().Start,
                                                                                    group.First().End - group.First().Start))).
                                   ToArray();

            var kinds     = Limits.Select(limit => limit.Kind).Distinct().ToArray();

            return new Report(
                       Program_Id.Parse(Event.ProgramId.ToString()),
                       Event.Id!.Value,
                       ClientName,
                       [ new ResourceReport(Node.Id.ToString(), intervals) ],
                       ReportName:          Limits.Count > 0
                                                ? $"Received: {String.Join(", ", Limits.Select(limit => $"{limit.Kind} {limit.KW:0.###} kW"))}"
                                                : "Received, but no limit could be read from it",
                       PayloadDescriptors:  [ .. kinds.Select(kind => new ReportPayloadDescriptor(PayloadType.Parse(kind), Units: UnitType.Parse("KW"))) ]
                   );

        }

        #endregion


        #region OpenADRJSON()

        /// <summary>
        /// The VTN connection as the web interface reads it: what it is
        /// configured as, what it is doing, the secret's file and whether there
        /// is one - and the limits.
        /// </summary>
        public JObject OpenADRJSON()
        {

            var now   = TimeProvider.GetUtcNow();
            var json  = openADRSettings.ToJSON();

            json.Add("state", new JObject(
                new JProperty("running",        venLoop is not null),
                new JProperty("signedIn",       venSignedIn),
                new JProperty("tokenURL",       venClient?.TokenURL?.ToString()),
                new JProperty("tokenExpires",   venClient?.TokenExpires?.ToString("o")),
                new JProperty("lastPoll",       lastPollAt?.ToString("o")),
                new JProperty("lastRead",       lastReadAt?.ToString("o")),
                new JProperty("lastProblem",    openADRProblem),
                new JProperty("inTheClear",     openADRSettings.InTheClear)
            ));

            json.Add("credentials", OpenADRLogin.ToJSON());

            json.Add("limits", new JObject(
                new JProperty("importKW",  ImportLimitNow?.KW),
                new JProperty("exportKW",  ExportLimitNow?.KW),
                new JProperty("all",       new JArray(GridLimits.Select(limit => limit.ToJSON(now))))
            ));

            return json;

        }

        #endregion

        #region TryUpdateOpenADRConfiguration(JSON, out Error [, out NotSaved])

        /// <summary>
        /// Change the VTN connection, write it to the configuration file, and
        /// ask the VTN as it now says.
        /// </summary>
        public Task<(Boolean Changed, String? Error, Boolean NotSaved)> TryUpdateOpenADRConfiguration(JObject JSON)

            => TryUpdateOpenADRConfiguration(JSON, null);

        /// <summary>
        /// Change the VTN connection - and the client secret with it, where one
        /// is given: written to its own file, never to the configuration.
        /// </summary>
        /// <param name="JSON">The fields of the 'openADR' section to change.</param>
        /// <param name="ClientSecret">The client secret to sign in with from now on, or null to keep the one there is.</param>
        public async Task<(Boolean Changed, String? Error, Boolean NotSaved)> TryUpdateOpenADRConfiguration(JObject  JSON,
                                                                                                            String?  ClientSecret)
        {

            if (!OpenADRConfiguration.TryParse(JSON, out var changes, out var error, AChange: true))
                return (false, error, false);

            var wanted = openADRSettings.Merge(changes).Effective();

            // Switched on, together with what it is switched on with.
            if (wanted.Enabled == true && (wanted.VTNURL is null || wanted.ClientID is null))
                return (false, $"'{OpenADRConfiguration.SectionName}' is switched on, but has no {(wanted.VTNURL is null ? "'vtnURL'" : "'clientID'")}.", false);

            if (ClientSecret is not null &&
                !OpenADRLogin.TrySetSecret(ClientSecret, out error, out var secretNotSaved))
            {
                return (false, error, secretNotSaved);
            }

            if (!ConfigFile.TryMergeSection(OpenADRConfiguration.SectionName, changes.ToJSON(), out error))
                return (false, error, true);

            var running  = venMayRun;
            var sameVTN  = wanted.VTNURL == openADRSettings.VTNURL && wanted.ClientID == openADRSettings.ClientID;

            await StopOpenADR().ConfigureAwait(false);

            // The limits of another VTN, or of another VEN, are not this one's.
            openADRSettings  = wanted;
            gridLimits       = sameVTN && wanted.Enabled == true ? gridLimits : [];
            openADRProblem   = null;

            Log.Info($"The OpenADR VTN connection is {wanted} now.", "openadr", "config");

            if (running)
                StartOpenADR();

            return (true, null, false);

        }

        #endregion

        #region RestartOpenADR()

        /// <summary>
        /// Ask the VTN anew: a client of its own, signing in again - for a secret
        /// set anew.
        /// </summary>
        public async Task RestartOpenADR()
        {

            if (!venMayRun)
                return;

            await StopOpenADR().ConfigureAwait(false);

            openADRProblem = null;

            StartOpenADR();

        }

        #endregion

    }

}
