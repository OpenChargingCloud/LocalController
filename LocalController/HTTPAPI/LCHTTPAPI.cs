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
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Logging;

using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The JSON API the browser talks to, registered at "/api": the sign-in,
    /// the configuration of this local controller, its log, and one
    /// Server-Sent Events stream that carries everything that happens.
    /// </summary>
    /// <remarks>
    /// It lives in its own HTTPAPI so that unknown API paths never reach the
    /// single-page-application fallback of the web interface at "/": Hermod
    /// dispatches a request to the most specific HTTPAPI first.
    ///
    /// Everything below /api/v1 needs somebody signed in - the event stream
    /// included, which is why the stream is opened here by hand rather than
    /// through Hermod's MapEventSource. Signing in itself happens at the
    /// HTTPExt API's own "/ext/login"; this API only reads what that door set.
    /// </remarks>
    public partial class LCHTTPAPI : HTTPAPI
    {

        #region Data

        /// <summary>
        /// The default root path of this API.
        /// </summary>
        public static readonly HTTPPath  DefaultAPIPath      = HTTPPath.Parse("/api");

        /// <summary>
        /// The identification of the Server-Sent Events source.
        /// </summary>
        public const           String    EventSourceName     = "events";

        /// <summary>
        /// The sub-event every log entry travels as.
        /// </summary>
        public const           String    LogEventName        = "log";

        /// <summary>
        /// How long an event stream stays silent before a comment is sent down
        /// it instead.
        /// </summary>
        /// <remarks>
        /// Silence is how an event stream waits, and a proxy in front of the
        /// local controller cannot tell it from a controller that has gone:
        /// nginx gives up on an upstream that has sent nothing for 60 seconds.
        /// Fifteen seconds is what the HTML standard suggests for exactly this,
        /// and a browser skips a comment.
        /// </remarks>
        public static readonly TimeSpan  DefaultEventStreamHeartbeat = TimeSpan.FromSeconds(15);

        /// <summary>
        /// The most log entries one request may ask for.
        /// </summary>
        public const           Int32     MaxLogPageSize      = 2_000;

        /// <summary>
        /// How many log entries a request brings back when it does not say.
        /// </summary>
        public const           Int32     DefaultLogPageSize  = 500;

        private readonly DateTimeOffset  startedAt;

        /// <summary>
        /// Cancelled when this local controller is shutting down, so that the
        /// event streams end.
        /// </summary>
        /// <remarks>
        /// A browser on the Logs page holds a request open that is not waiting
        /// on its socket but on the next log entry, so closing the socket under
        /// it does not end it - and an HTTP server that waits for every request
        /// it started would then never finish stopping. This is what ends them
        /// instead; see <see cref="CloseEventStreams"/>.
        /// </remarks>
        private readonly CancellationTokenSource  shutdown = new ();

        #endregion

        #region Properties

        /// <summary>
        /// The local controller this API speaks for.
        /// </summary>
        public LocalController           Controller  { get; }

        /// <summary>
        /// Everything that happens inside this local controller.
        /// </summary>
        public EventLog                  Log         { get; }

        /// <summary>
        /// Who may open the web interface: the accounts, and the groups whose
        /// membership carries this controller's roles.
        /// </summary>
        public HTTPExtAPI                ExtAPI      { get; }

        /// <summary>
        /// The version reported by the status resource.
        /// </summary>
        public String                    Version     { get; }

        /// <summary>
        /// The Server-Sent Events source every browser hangs on (/api/v1/events).
        /// </summary>
        public HTTPEventSource<JObject>  Events      { get; }

        /// <summary>
        /// How long an event stream stays silent before a comment is sent down
        /// it; <see cref="DefaultEventStreamHeartbeat"/> unless set, and never
        /// when set to zero.
        /// </summary>
        public TimeSpan                  EventStreamHeartbeat { get; set; } = DefaultEventStreamHeartbeat;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create and register the JSON API within the given HTTP server.
        /// </summary>
        /// <param name="HTTPServer">The HTTP server.</param>
        /// <param name="Controller">The local controller this API speaks for.</param>
        /// <param name="ExtAPI">The accounts and the groups they are in.</param>
        /// <param name="Log">Everything that happens inside this local controller.</param>
        /// <param name="APIPath">The root path of the API, "/api" by default.</param>
        /// <param name="Version">The version reported by the status resource.</param>
        public LCHTTPAPI(HTTPServer       HTTPServer,
                         LocalController  Controller,
                         HTTPExtAPI       ExtAPI,
                         EventLog         Log,
                         HTTPPath?        APIPath   = null,
                         String?          Version   = null)

            : base(HTTPServer,
                   RootPath:     APIPath ?? DefaultAPIPath,
                   Description:  I18NString.Create("The JSON API of this local controller"))

        {

            this.Controller  = Controller;
            this.ExtAPI      = ExtAPI;
            this.Log         = Log;
            this.startedAt   = Controller.TimeProvider.GetUtcNow();

            this.Version     = Version
                                   ?? typeof(LCHTTPAPI).Assembly.GetName().Version?.ToString(3)
                                   ?? "0.0.0";

            // Hermod caches the last events and replays them to a new client.
            // The browser ignores everything older than the snapshot it loaded,
            // so a replay costs nothing but bytes; what it buys is that a
            // browser which reconnects after a hiccup gets what it missed.
            this.Events      = this.AddJSONEventSource(
                                 HTTPEventSource_Id.Parse(EventSourceName),
                                 MaxNumberOfCachedEvents:  500,
                                 RetryInterval:            TimeSpan.FromSeconds(2),
                                 EnableLogging:            false
                             );

            this.Log.OnLogged += entry => Publish(LogEventName, entry.ToJSON());

            RegisterURLTemplates();

        }

        #endregion


        #region (private) RegisterURLTemplates()

        private void RegisterURLTemplates()
        {

            // No sign-in route here. Signing in happens at the HTTPExt API's
            // own "/ext/login", which is the only place that can check a
            // password: the check reads a store this API has no access to, and
            // a second door onto the same credentials is a second door to get
            // wrong. What this API does is read the cookie that door sets.
            AddHandler(HTTPPath.Root + "v1/auth/logout",   Logout,            HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/auth/me",       Me,                HTTPMethod.GET);

            AddHandler(HTTPPath.Root + "v1/status",        GetStatus,         HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration", GetConfiguration,  HTTPMethod.GET);

            AddHandler(HTTPPath.Root + "v1/configuration/dns",        GetDNSConfiguration,   HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/dns",        PutDNSConfiguration,   HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/configuration/dns/query",  PostDNSQuery,          HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/configuration/nts",        GetNTSConfiguration,   HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/nts",        PutNTSConfiguration,   HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/configuration/nts/sync",   PostNTSSync,           HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/nts/test",   PostNTSTest,           HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/configuration/time",       GetClock,              HTTPMethod.GET);

            // The charging station server, its certificates, the chains it
            // accepts and the stations that may sign in; see
            // LCHTTPAPI.OCPPServer.cs.
            RegisterOCPPServerRoutes();
            RegisterCSMSRoutes();

            AddHandler(HTTPPath.Root + "v1/logs",          GetLogs,           HTTPMethod.GET);

            AddHandler(HTTPMethod.GET,
                       HTTPPath.Root + "v1/events",
                       HTTPContentType.Text.EVENTSTREAM,
                       StreamEvents);

            // Everything else below /api answers with a JSON 404 instead of
            // the single-page-application stub of the web interface.
            foreach (var method in new[] { HTTPMethod.GET, HTTPMethod.HEAD, HTTPMethod.POST, HTTPMethod.PUT, HTTPMethod.DELETE })
                AddHandler(HTTPPath.Root + "{path..}", UnknownPath, method);

        }

        #endregion


        #region (private) Logout          (Request)

        /// <summary>
        /// POST /api/v1/auth/logout: ends the session and expires the cookie.
        /// </summary>
        private Task<HTTPResponse> Logout(HTTPRequest Request)
        {

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return Task.FromResult(refused);

            // The session is ended where it lives, and not only forgotten by
            // this browser: a cookie that is merely expired is still a valid
            // token to whoever copied it.
            if (Request.Cookies is not null                                                      &&
                Request.Cookies.TryGet(ExtAPI.SessionCookieName, out var cookie)                 &&
                cookie is not null                                                               &&
                SecurityToken_Id.TryParse(cookie.FirstOrDefault().Key, out var securityTokenId))
            {

                ExtAPI.Sessions.Remove(securityTokenId);

                Log.Notice($"A session was ended from {Request.RemoteSocket}.", "web", "auth");

            }

            return Task.FromResult(
                       new HTTPResponse.Builder(Request) {
                           HTTPStatusCode  = HTTPStatusCode.NoContent,
                           CacheControl    = "no-store",
                           SetCookie       = ExpiredSessionCookie()
                       }.WithCommonSecurityHeaders().AsImmutable
                   );

        }

        #endregion

        #region (private) Me              (Request)

        /// <summary>
        /// GET /api/v1/auth/me: who is signed in, or 401.
        /// </summary>
        private Task<HTTPResponse> Me(HTTPRequest Request)

            => Task.FromResult(
                   TryGetUser(Request, out var user, out var unauthorized)
                       ? JSONResponse(Request, HTTPStatusCode.OK, MeJSON(user))
                       : unauthorized
               );

        #endregion


        #region (private) GetStatus       (Request)

        /// <summary>
        /// GET /api/v1/status: how this local controller is doing right now.
        /// </summary>
        private Task<HTTPResponse> GetStatus(HTTPRequest Request)
        {

            if (!TryGetUser(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            var now = Controller.TimeProvider.GetUtcNow();

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.OK,
                           new JObject(
                               new JProperty("service",    "LocalController"),
                               new JProperty("version",    Version),
                               new JProperty("ocppId",     Controller.Node.Id.ToString()),
                               new JProperty("hermod",     typeof(HTTPServer).Assembly.GetName().Version?.ToString(3)),
                               new JProperty("timestamp",  now.ToString("o")),
                               new JProperty("startedAt",  startedAt.ToString("o")),
                               new JProperty("uptime",     (now - startedAt).ToString(@"d\.hh\:mm\:ss")),
                               new JProperty("sessions",   ExtAPI.Sessions.Count()),
                               new JProperty("log",        new JObject(
                                                               new JProperty("entries",   Log.Count),
                                                               new JProperty("capacity",  Log.Capacity),
                                                               new JProperty("lastId",    Log.LastId),
                                                               new JProperty("tags",      new JArray(Log.KnownTags))
                                                           ))
                           )
                       )
                   );

        }

        #endregion

        #region (private) GetConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration: what this local controller is made of.
        /// </summary>
        private Task<HTTPResponse> GetConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.ConfigurationJSON())
                   );

        }

        #endregion

        #region (private) GetDNSConfiguration(Request) / PutDNSConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/dns: how this local controller resolves names.
        /// </summary>
        private Task<HTTPResponse> GetDNSConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.DNSConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/dns: change what may be changed about it.
        /// Answers with the whole configuration as it now stands, so that the
        /// page does not have to ask again to find out what it got.
        /// </summary>
        private Task<HTTPResponse> PutDNSConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Controller.TryUpdateDNSConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.DNSConfigurationJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/dns/query with {"name", "recordTypes"}
        /// and an optional {"server"}: make this local controller look a name
        /// up and say what came back.
        /// </summary>
        /// <remarks>
        /// A POST although it changes nothing here, because it makes this
        /// local controller send traffic to a host somebody named - which is not
        /// something to leave sitting in a URL that a browser may repeat,
        /// prefetch or put in a history.
        ///
        /// The server is the place of one of the configured name servers in
        /// the list, counted from 0, and asks that one and nothing else, and
        /// without the cache. Left out, the question is put the way this
        /// controller resolves anything: to the servers in turn, until one
        /// answers. A place with no server at it is said in the answer; one
        /// that is not a place at all is refused here, rather than read as
        /// "all of them".
        /// </remarks>
        private async Task<HTTPResponse> PostDNSQuery(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            var name = json.Value<String>("name")?.Trim();

            if (String.IsNullOrEmpty(name))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, "A 'name' to look up is required.");

            if (!LocalController.TryParseRecordTypes(json["recordTypes"], out var recordTypes, out var problem))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, problem);

            Int32? server = null;

            if (json["server"] is JToken serverToken && serverToken.Type != JTokenType.Null)
            {

                if (serverToken.Type != JTokenType.Integer ||
                    serverToken.Value<Int64>() is < 0 or > Int32.MaxValue)
                {
                    return ErrorJSON(Request, HTTPStatusCode.BadRequest,
                                     "'server' must be the place of a name server in the list, counted from 0.");
                }

                server = serverToken.Value<Int32>();

            }

            Log.Info($"'{user.Id}' asked this local controller to resolve '{name}'.", "dns", "test", "web");

            return JSONResponse(
                       Request,
                       HTTPStatusCode.OK,
                       await Controller.ResolveAsync(name, recordTypes, server, Request.CancellationToken)
                   );

        }

        #endregion

        #region (private) GetNTSConfiguration(Request) / PutNTSConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/nts: where this local controller gets the time from.
        /// </summary>
        private Task<HTTPResponse> GetNTSConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.NTSConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/nts: change what may be changed about it.
        /// </summary>
        private Task<HTTPResponse> PutNTSConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Controller.TryUpdateNTSConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.NTSConfigurationJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/nts/sync: one key exchange and one
        /// authenticated NTP request, with every step in the log.
        /// </summary>
        /// <remarks>
        /// Answers with the whole NTS configuration and not only with the
        /// result, because an exchange moves the cookie pool, the key material
        /// and the record of the last exchange - all of which the page is
        /// showing while it waits.
        /// </remarks>
        private async Task<HTTPResponse> PostNTSSync(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var user, out var refused))
                return refused;

            Log.Info($"'{user.Id}' asked this local controller to synchronise its time.", "nts", "test", "web");

            var result = await Controller.SyncTimeAsync(Request.CancellationToken);

            var json   = Controller.NTSConfigurationJSON();

            json["result"] = result;

            return JSONResponse(Request, HTTPStatusCode.OK, json);

        }

        #endregion

        #region (private) PostNTSTest(Request)

        /// <summary>
        /// POST /api/v1/configuration/nts/test with an optional {"host"}: ask
        /// one time server everything there is to ask, and say where it got
        /// to.
        /// </summary>
        /// <remarks>
        /// "Sync now" says whether the group has a time; this says where one
        /// server got to - the name, the TCP connection, the TLS handshake and
        /// the certificate it presented, the key exchange, the authenticated
        /// request - each step timed and in the answer, so that a server that
        /// is merely slow can be told from one that is refusing.
        ///
        /// The host is optional and names the server to ask; left out, it is
        /// the configured one. The key exchange may name NTP servers other
        /// than itself, which is the other reason this takes a host at all.
        ///
        /// At the diagnostics permission, with the other tests. Like "Sync
        /// now", it does not step the clock.
        /// </remarks>
        private async Task<HTTPResponse> PostNTSTest(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            if (json["host"] is JToken hostToken && hostToken.Type is not (JTokenType.String or JTokenType.Null))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, "'host' must be the name or the address of a time server.");

            var host = json.Value<String>("host")?.Trim();

            if (host?.Length == 0)
                host = null;

            Log.Info($"'{user.Id}' asked this local controller to test {(host is null ? "its time server" : $"the time server '{host}'")}.",
                     "nts", "test", "web");

            return JSONResponse(
                       Request,
                       HTTPStatusCode.OK,
                       await Controller.TestTimeServerAsync(host, Request.CancellationToken)
                   );

        }

        #endregion

        #region (private) GetClock        (Request)

        /// <summary>
        /// GET /api/v1/configuration/time: what time it is here, and what that
        /// is worth.
        /// </summary>
        /// <remarks>
        /// Separate from the NTS configuration although it is the same subject,
        /// because it answers a different question and is asked at a different
        /// rate: the configuration says where the time comes from and is read
        /// when somebody opens a page, this says whether the clock is currently
        /// worth anything and is read by whatever wants to keep saying so.
        ///
        /// Reading, not diagnosing - it reports the last check rather than
        /// making one - so it takes the permission that reading takes.
        /// </remarks>
        private Task<HTTPResponse> GetClock(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.ClockJSON())
                   );

        }

        #endregion


        #region (private) GetLogs         (Request)

        /// <summary>
        /// GET /api/v1/logs?limit=&amp;after=&amp;tag=: what happened, oldest
        /// of the returned entries first.
        /// </summary>
        /// <remarks>
        /// This is the snapshot a browser loads before it starts following the
        /// event stream; "lastId" says how far it reaches, and everything the
        /// stream delivers with a greater id is new.
        /// </remarks>
        private Task<HTTPResponse> GetLogs(HTTPRequest Request)
        {

            if (!TryGetUser(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            var limit    = Request.QueryString.GetInt32 ("limit") ?? DefaultLogPageSize;
            var after    = Request.QueryString.GetUInt64("after");
            var tag      = Request.QueryString.GetString("tag");

            if (limit < 1 || limit > MaxLogPageSize)
                return Task.FromResult(
                           ErrorJSON(Request, HTTPStatusCode.BadRequest, $"'limit' must be between 1 and {MaxLogPageSize}.")
                       );

            var entries  = Log.Recent(limit, after, tag).ToArray();

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.OK,
                           new JObject(
                               // The whole log's last id and not the last of
                               // this page: a page filtered by a tag would
                               // otherwise make the browser ask again for
                               // everything between the two.
                               new JProperty("lastId",   Log.LastId),
                               new JProperty("capacity", Log.Capacity),
                               new JProperty("tags",     new JArray(Log.KnownTags)),
                               new JProperty("entries",  new JArray(entries.Select(entry => entry.ToJSON())))
                           )
                       )
                   );

        }

        #endregion


        #region (private) StreamEvents    (Request)

        /// <summary>
        /// GET /api/v1/events: the Server-Sent Events stream every browser
        /// hangs on. Modelled on Hermod's MapEventSource, with the session
        /// checked first and without opening the stream to other origins.
        /// </summary>
        /// <remarks>
        /// Two things in here are for a proxy in front of the local controller,
        /// and both were learned from nginx as it comes - by the vehicle, whose
        /// Logs page stayed empty behind one.
        ///
        /// "X-Accel-Buffering: no", because nginx buffers what it passes on,
        /// and a buffered event stream reaches the browser as nothing at all -
        /// not even its header - until a buffer is full or the controller has
        /// been silent long enough for nginx to give up on it. The browser never
        /// sees the stream open, so the Logs page says "reconnecting ..." and
        /// does not ask for its snapshot either: behind nginx the vehicle's
        /// header came after 72 seconds, and its stream ended 98 ms later.
        ///
        /// And a comment whenever the stream has been silent for
        /// <see cref="EventStreamHeartbeat"/>, because the 60 seconds after
        /// which nginx gives up are an ordinary pause for a local controller
        /// nobody is using.
        /// </remarks>
        private Task<HTTPResponse> StreamEvents(HTTPRequest Request)
        {

            if (!TryGetUser(Request, out var reader, out var unauthorized))
                return Task.FromResult(unauthorized);

            var clientId    = Request.RemoteSocket.ToString();

            // Asked before every event and at every heartbeat - see StillLetIn().
            var stillLetIn  = StillLetIn(Request, reader);

            return Task.FromResult(
                       new HTTPResponse.Builder(Request) {

                           HTTPStatusCode  = HTTPStatusCode.OK,
                           Server          = HTTPServer.HTTPServerName,
                           ContentType     = HTTPContentType.Text.EVENTSTREAM,
                           CacheControl    = "no-cache",
                           Connection      = ConnectionType.KeepAlive,

                           HTTPSSEWorker   = async (response, stream) => {

                               // Either the browser going away or this
                               // controller shutting down ends the stream. The
                               // second one is not something the request's own
                               // token knows about - see CloseEventStreams().
                               using var ending = CancellationTokenSource.CreateLinkedTokenSource(
                                                      Request.CancellationToken,
                                                      shutdown.Token
                                                  );

                               try
                               {

                                   await stream.WriteAsync("retry: ");
                                   await stream.WriteAsync(((UInt32) Events.RetryInterval.TotalMilliseconds).ToString());
                                   await stream.WriteAsync("\n\n");

                                   // The preamble has to leave the buffer now,
                                   // not with the first event: on a quiet
                                   // local controller the browser would otherwise wait
                                   // for its first byte until its own read
                                   // timeout expired.
                                   await stream.FlushAsync(ending.Token);

                                   var heartbeat  = EventStreamHeartbeat > TimeSpan.Zero
                                                        ? EventStreamHeartbeat
                                                        : Timeout.InfiniteTimeSpan;

                                   await using var events = Events.GetAllEventsGreater(
                                                                clientId,
                                                                Request.GetHeaderField(HTTPRequestHeaderField.LastEventId),
                                                                ending.Token
                                                            ).GetAsyncEnumerator(ending.Token);

                                   // The next event is waited for across heartbeats
                                   // rather than asked for again: an enumerator
                                   // takes one question at a time.
                                   var next = events.MoveNextAsync().AsTask();

                                   // Set when the stream ends because its session did.
                                   var signedOut = false;

                                   try
                                   {

                                       while (true)
                                       {

                                           try
                                           {
                                               if (!await next.WaitAsync(heartbeat, ending.Token))
                                                   break;
                                           }
                                           catch (TimeoutException)
                                           {

                                               // A quiet stream is asked as well, or one
                                               // whose session ended would go on for as
                                               // long as nothing was logged.
                                               if (!stillLetIn())
                                               {
                                                   signedOut = true;
                                                   break;
                                               }

                                               await stream.WriteHeartbeat(CancellationToken: ending.Token);
                                               continue;

                                           }

                                           // Asked before the event is written, not after:
                                           // what was logged after the sign-out is not sent
                                           // to the session that signed out.
                                           if (!stillLetIn())
                                           {
                                               signedOut = true;
                                               break;
                                           }

                                           var httpEvent = events.Current;

                                           await stream.WriteAsync(httpEvent.SerializedHeader);
                                           await stream.WriteAsync(httpEvent.SerializedData);
                                           await stream.WriteAsync("\n\n");
                                           await stream.FlushAsync(ending.Token);

                                           next = events.MoveNextAsync().AsTask();

                                       }

                                   }
                                   finally
                                   {
                                       // However the loop ended, the enumerator may
                                       // still be waiting for the next event - a
                                       // heartbeat that could not be written leaves
                                       // it so - and it cannot be disposed before
                                       // it has stopped. Cancelling stops it.
                                       ending.Cancel();

                                       try
                                       {
                                           await next;
                                       }
                                       catch
                                       { }
                                   }

                                   // Its session over, the reader is told the one way
                                   // a stream can tell anybody anything: it ends, and
                                   // the browser's retry is answered with a 401.
                                   if (signedOut)
                                       await Events.Unsubscribe(clientId);

                               }
                               catch (OperationCanceledException)
                               {
                                   await Events.Unsubscribe(clientId);
                               }
                               catch (ObjectDisposedException)
                               {
                                   await Events.Unsubscribe(clientId);
                               }
                               catch (Exception e)
                               {
                                   await Events.Unsubscribe(clientId);

                                   // Not through the event log: an event stream
                                   // that ends because the browser went away is
                                   // the normal end of one, and logging it here
                                   // would publish an event to the very streams
                                   // that are closing.
                                   System.Diagnostics.Debug.WriteLine($"The event stream of {clientId} ended: {e.Message}");
                               }

                           }

                       }.Set("X-Accel-Buffering", "no").
                         WithCommonSecurityHeaders().
                         AsImmutable
                   );

        }

        #endregion

        #region (private) StillLetIn(Request, Reader)

        /// <summary>
        /// Whether whoever opened an event stream would still be let in -
        /// asked before every event the stream is sent, and at every heartbeat.
        /// </summary>
        /// <remarks>
        /// A stream is one request that is answered for hours, and it used to
        /// be asked about its session once, when it opened. Measured: signed
        /// out, the Logs page went on saying "live" and showing every line the
        /// local controller wrote, for as long as it was watched.
        ///
        /// A stream opened with a session is asked whether that session is
        /// still there and its account still one that may sign in - what a new
        /// request with the same cookie is asked. The session is looked at and
        /// not taken through Sessions.TryGet, which counts as a use: with an
        /// idle timeout, a Logs page left open would keep its session alive for
        /// ever, one line of the log at a time. Among the few sessions a local
        /// controller has, looking costs nothing.
        ///
        /// One opened with a password or an API key has no session that could
        /// end. Its account is asked about instead, and the password is not
        /// checked again: that would be 600 000 rounds of PBKDF2 and a turn of
        /// the sign-in's rate limit, for every line of the log.
        /// </remarks>
        /// <param name="Request">The request that opened the stream.</param>
        /// <param name="Reader">Who it was let in as.</param>
        private Func<Boolean> StillLetIn(HTTPRequest Request, IUser Reader)
        {

            if (Request.Cookies is not null                                                      &&
                Request.Cookies.TryGet(ExtAPI.SessionCookieName, out var cookie)                 &&
                cookie is not null                                                               &&
                SecurityToken_Id.TryParse(cookie.FirstOrDefault().Key, out var securityTokenId) &&
                LiveSession(securityTokenId) is not null)
            {
                return () => LiveSession(securityTokenId) is Session session  &&
                             ExtAPI.TryGetUser(session.UserId, out var user)   &&
                             HTTPExtAPI.CanAuthenticate(user);
            }

            var readerId = Reader.Id;

            return () => ExtAPI.TryGetUser(readerId, out var user) &&
                         HTTPExtAPI.CanAuthenticate(user);


            Session? LiveSession(SecurityToken_Id Token)
            {

                var now = ExtAPI.Sessions.TimeProvider.GetUtcNow();

                return ExtAPI.Sessions.FirstOrDefault(session => session.Token == Token &&
                                                                 !session.IsExpired(now));

            }

        }

        #endregion

        #region CloseEventStreams()

        /// <summary>
        /// End every open event stream, so that the HTTP server can stop.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="LocalController.Stop"/> before the server is
        /// stopped, and not by the server itself: an event stream is a request
        /// that has been answered and is still being written to, and Hermod
        /// waits for every request it started before it reports itself stopped.
        /// Closing the socket underneath one does not wake it, because it is
        /// waiting for the next log entry and not for the network - so without
        /// this, a controller with one browser on its Logs page never finishes
        /// shutting down.
        ///
        /// The browsers see the connection end and reconnect by themselves;
        /// that is what the retry interval of the stream is for.
        /// </remarks>
        public void CloseEventStreams()
        {

            if (!shutdown.IsCancellationRequested)
                shutdown.Cancel();

        }

        #endregion

        #region (private) UnknownPath     (Request)

        private Task<HTTPResponse> UnknownPath(HTTPRequest Request)

            => Task.FromResult(
                   JSONResponse(
                       Request,
                       HTTPStatusCode.NotFound,
                       new JObject(
                           new JProperty("error",  "Unknown API path"),
                           new JProperty("path",   Request.Path.ToString())
                       )
                   )
               );

        #endregion


        #region (private) Publish(SubEvent, JSON)

        /// <summary>
        /// Hands an event to every browser. Fire-and-forget on purpose: this is
        /// called from inside whatever wrote the log entry, and none of those
        /// should wait for a slow browser.
        /// </summary>
        private void Publish(String   SubEvent,
                             JObject  JSON)
        {

            Events.SubmitEvent(SubEvent, JSON).
                   ContinueWith(task => System.Diagnostics.Debug.WriteLine($"Publishing a '{SubEvent}' event failed: {task.Exception?.GetBaseException().Message}"),
                                TaskContinuationOptions.OnlyOnFaulted);

        }

        #endregion

        #region (private) TryGetUser(Request, out User, out Unauthorized)

        /// <summary>
        /// Who is behind the request, or the 401 response - which also expires
        /// a stale cookie, so that the browser stops sending it.
        /// </summary>
        private Boolean TryGetUser(HTTPRequest                             Request,
                                   [NotNullWhen(true)]  out IUser?         User,
                                   [NotNullWhen(false)] out HTTPResponse?  Unauthorized)
        {

            // Cookie, HTTP Basic auth or an API key - whichever of the three
            // the caller used. Which one it was does not change what they may
            // do: the groups do that, and they hang off the account rather than
            // off the door it came through.
            if (ExtAPI.TryGetHTTPUser(Request, out User) && User is not null)
            {
                Unauthorized = null;
                return true;
            }

            var builder = new HTTPResponse.Builder(Request) {
                              HTTPStatusCode  = HTTPStatusCode.Unauthorized,
                              ContentType     = HTTPContentType.Application.JSON_UTF8,
                              Content         = Encoding.UTF8.GetBytes(new JObject(new JProperty("error", "Sign in required.")).ToString(Formatting.None)),
                              CacheControl    = "no-store"
                          };

            if (Request.Cookies is not null &&
                Request.Cookies.TryGet(ExtAPI.SessionCookieName, out _))
            {
                builder.SetCookie = ExpiredSessionCookie();
            }

            Unauthorized = builder.WithCommonSecurityHeaders().AsImmutable;
            return false;

        }

        #endregion

        #region (private) TryAuthorize(Request, Required, StateChanging, out User, out Refused)

        /// <summary>
        /// Who is behind the request, when they are allowed to do this - or
        /// the response that says why not.
        /// </summary>
        /// <remarks>
        /// Three refusals, in the order they have to happen: a request from
        /// another site is turned away before it is read at all, a request
        /// without a session is a 401 that also expires a stale cookie, and a
        /// request from somebody signed in who may not do this is a 403 naming
        /// the permission they are short of and the roles that carry it. The
        /// difference between the last two matters to a browser: 401 means sign
        /// in again, 403 means signing in again will not help.
        /// </remarks>
        /// <param name="Request">The request.</param>
        /// <param name="Required">What this request needs permission to do.</param>
        /// <param name="StateChanging">Whether it changes something, and is therefore also checked for being cross-site.</param>
        /// <param name="User">Who is behind it.</param>
        /// <param name="Refused">The response to send instead.</param>
        private Boolean TryAuthorize(HTTPRequest                             Request,
                                     Permissions                             Required,
                                     Boolean                                 StateChanging,
                                     [NotNullWhen(true)]  out IUser?         User,
                                     [NotNullWhen(false)] out HTTPResponse?  Refused)
        {

            User = null;

            if (StateChanging && RefuseCrossSite(Request) is HTTPResponse crossSite)
            {
                Refused = crossSite;
                return false;
            }

            if (!TryGetUser(Request, out User, out Refused))
                return false;

            var permissions = PermissionsOf(User);

            if (!permissions.HasFlag(Required))
            {
                Refused  = RefusePermission(Request, User, Required, null);
                User     = null;
                return false;
            }

            Refused = null;
            return true;

        }

        #endregion

        #region (private) RefusePermission(Request, User, Required, Because)

        /// <summary>
        /// The 403 for somebody signed in who may not do this, naming the roles
        /// that carry the permission they are short of.
        /// </summary>
        /// <remarks>
        /// Its own method because it is needed twice: once before a request is
        /// read, and once after - a change to the EVSEs cannot be judged until
        /// it has been compared with what the local controller has, so that refusal
        /// happens with the body already parsed. Both say the same sentence,
        /// and both leave the same line in the log.
        /// </remarks>
        /// <param name="Because">What it was about this particular request, when the route alone does not say.</param>
        private HTTPResponse RefusePermission(HTTPRequest  Request,
                                              IUser        User,
                                              Permissions  Required,
                                              String?      Because)
        {

            // HasFlag with more than one flag asks for all of them, which is
            // what a role has to carry to do a change that was several kinds at
            // once. Nobody is named who could only do half of it.
            var allowed = UserRole.All.Where(role => role.Permissions.HasFlag(Required)).
                                       Select(role => role.Name);

            Log.Warning(
                $"'{User.Id}' was refused {Required} on {Request.HTTPMethod} {Request.Path}; " +
                $"signed in as {String.Join(", ", RolesOf(User).Select(role => role.Name))}." +
                (Because is null ? "" : $" {Because}"),
                "web", "auth"
            );

            return ErrorJSON(
                       Request,
                       HTTPStatusCode.Forbidden,
                       (Because is null ? "" : Because + " ") +
                       $"This needs the {String.Join(" or ", allowed)} role."
                   );

        }

        #endregion

        #region (private static) RefuseCrossSite(Request)

        /// <summary>
        /// The 403 for a request that another site made the browser send, or
        /// null when the request is our own page's.
        /// </summary>
        /// <remarks>
        /// The cookie is SameSite=strict, so a cross-site request would arrive
        /// without a session anyway. This is the second lock on the same door:
        /// browsers say where a request came from (Sec-Fetch-Site, Origin), and
        /// a state-changing request from anywhere but this origin is refused
        /// before it is even read.
        /// </remarks>
        private static HTTPResponse? RefuseCrossSite(HTTPRequest Request)
        {

            var site = Request.GetHeaderField("Sec-Fetch-Site");

            if (site is not null && site is not ("same-origin" or "none"))
                return ErrorJSON(Request, HTTPStatusCode.Forbidden, "Cross-site requests are refused.");

            var origin = Request.GetHeaderField("Origin");

            if (origin is not null && origin != "null")
            {

                var host = Request.GetHeaderField("Host") ?? "";

                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                    !uri.Authority.Equals(host, StringComparison.OrdinalIgnoreCase))
                {
                    return ErrorJSON(Request, HTTPStatusCode.Forbidden, "Cross-site requests are refused.");
                }

            }

            return null;

        }

        #endregion

        #region (private static) TryParseJSONObject(Request, out JSON, out ErrorResponse)

        /// <summary>
        /// The request body as a JSON object, or the 400 response describing
        /// what is wrong with it.
        /// </summary>
        private static Boolean TryParseJSONObject(HTTPRequest                             Request,
                                                  [NotNullWhen(true)]  out JObject?       JSON,
                                                  [NotNullWhen(false)] out HTTPResponse?  ErrorResponse)
        {

            JSON           = null;
            ErrorResponse  = null;

            var text = Request.HTTPBodyAsUTF8String;

            if (String.IsNullOrWhiteSpace(text))
            {
                ErrorResponse = ErrorJSON(Request, HTTPStatusCode.BadRequest, "The request body must be a JSON object!");
                return false;
            }

            try
            {
                JSON = JObject.Parse(text);
                return true;
            }
            catch (JsonException e)
            {
                ErrorResponse = ErrorJSON(Request, HTTPStatusCode.BadRequest, $"Invalid JSON: {e.Message}");
                return false;
            }

        }

        #endregion

        #region (private) MeJSON(User)

        /// <summary>
        /// Who is signed in, and what they may do.
        /// </summary>
        /// <remarks>
        /// The permissions travel to the browser so that a page can grey out
        /// what this person may not do, rather than offering it and letting
        /// them find out by being refused. They are a copy of what the local controller
        /// enforces and not the enforcement: every request is checked again on
        /// arrival, so a browser that edits this list gains nothing but a
        /// button that answers 403.
        /// </remarks>
        private JObject MeJSON(IUser User)

            => new (
                   new JProperty("username",     User.Id.ToString()),
                   new JProperty("roles",        new JArray(RolesOf(User).Select(role => role.Name))),
                   new JProperty("permissions",  new JArray(PermissionsOf(User).Names()))
               );

        #endregion

        #region (private) ExpiredSessionCookie()

        /// <summary>
        /// The Set-Cookie of a sign-out: the HTTPExt API's session cookie,
        /// expired in 1970, so that the browser drops it.
        /// </summary>
        /// <remarks>
        /// Written here rather than asked of the HTTPExt API, which sets its
        /// cookies inside its own handlers and has nothing to hand one out.
        /// One HTTPCookie parsed as one: HTTPCookies.Parse(String) is made for
        /// the Cookie header of a request, where a semicolon separates cookies,
        /// and would turn "Path=/" and "HttpOnly" into cookies of their own.
        /// </remarks>
        private HTTPCookies ExpiredSessionCookie()

            => new (HTTPCookie.Parse(
                        String.Concat(ExtAPI.SessionCookieName, "=",
                                      "; Expires=", DateTimeOffset.UnixEpoch.ToRFC1123(),
                                      "; Path=/",
                                      "; SameSite=strict",
                                      "; HttpOnly")
                    ));

        #endregion

        #region (private) RolesOf(User) / PermissionsOf(User)

        /// <summary>
        /// The roles this account holds: one per group of that name it is in.
        /// </summary>
        /// <remarks>
        /// Asked of the groups on every request rather than remembered at
        /// sign-in, so that taking somebody out of a group takes effect on
        /// their next request instead of at their next sign-in. A role revoked
        /// that still works until a browser is closed is not revoked.
        /// </remarks>
        private IEnumerable<UserRole> RolesOf(IUser User)

              // IsMember compares the account by identification, which is what
              // makes this safe to ask with whatever instance authenticated the
              // request: a cookie brings one rebuilt from what the cookie holds
              // rather than the one the membership was made with.
            => UserRole.All.Where(role => ExtAPI.IsMember(User, role.GroupId));

        /// <summary>
        /// Everything those roles add up to, or nothing at all when the account
        /// is in none of the groups.
        /// </summary>
        private Permissions PermissionsOf(IUser User)

            => RolesOf(User).PermissionsOf();

        #endregion

        #region (private static) ErrorJSON(...) / JSONResponse(...)

        private static HTTPResponse ErrorJSON(HTTPRequest     Request,
                                              HTTPStatusCode  StatusCode,
                                              String          Message)

            => JSONResponse(
                   Request,
                   StatusCode,
                   new JObject(new JProperty("error", Message))
               );


        private static HTTPResponse JSONResponse(HTTPRequest     Request,
                                                 HTTPStatusCode  StatusCode,
                                                 JToken          JSON)

            => new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = StatusCode,
                   ContentType     = HTTPContentType.Application.JSON_UTF8,
                   Content         = Encoding.UTF8.GetBytes(JSON.ToString(Formatting.None)),
                   CacheControl    = "no-store"
               }.WithCommonSecurityHeaders().AsImmutable;

        #endregion

    }

}
