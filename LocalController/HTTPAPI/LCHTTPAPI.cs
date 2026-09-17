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

using cloud.charging.open.LocalController.Logging;
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
    /// Everything below /api/v1 except the sign-in itself needs the session
    /// cookie - the event stream included, which is why the stream is opened
    /// here by hand rather than through Hermod's MapEventSource.
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
        /// How long a failed sign-in waits before it answers. Not a lock-out,
        /// just enough to make guessing a slow business.
        /// </summary>
        public static readonly TimeSpan  FailedLoginDelay    = TimeSpan.FromMilliseconds(500);

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
        /// The signed-in browsers.
        /// </summary>
        public WebSessions               Sessions    { get; }

        /// <summary>
        /// The version reported by the status resource.
        /// </summary>
        public String                    Version     { get; }

        /// <summary>
        /// The Server-Sent Events source every browser hangs on (/api/v1/events).
        /// </summary>
        public HTTPEventSource<JObject>  Events      { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create and register the JSON API within the given HTTP server.
        /// </summary>
        /// <param name="HTTPServer">The HTTP server.</param>
        /// <param name="Controller">The local controller this API speaks for.</param>
        /// <param name="Sessions">The web sessions.</param>
        /// <param name="Log">Everything that happens inside this local controller.</param>
        /// <param name="APIPath">The root path of the API, "/api" by default.</param>
        /// <param name="Version">The version reported by the status resource.</param>
        public LCHTTPAPI(HTTPServer       HTTPServer,
                         LocalController  Controller,
                         WebSessions      Sessions,
                         EventLog         Log,
                         HTTPPath?        APIPath   = null,
                         String?          Version   = null)

            : base(HTTPServer,
                   RootPath:     APIPath ?? DefaultAPIPath,
                   Description:  I18NString.Create("The JSON API of this local controller"))

        {

            this.Controller  = Controller;
            this.Sessions    = Sessions;
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

            AddHandler(HTTPPath.Root + "v1/auth/login",    Login,             HTTPMethod.POST);
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


        #region (private) Login           (Request)

        /// <summary>
        /// POST /api/v1/auth/login with {"username", "password"}: the session
        /// cookie, or 401 after a short pause.
        /// </summary>
        private async Task<HTTPResponse> Login(HTTPRequest Request)
        {

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            if (!Sessions.TryLogin(json.Value<String>("username"),
                                   json.Value<String>("password"),
                                   out var session))
            {

                Log.Warning($"Sign-in refused for {Request.RemoteSocket}.", "web", "auth");

                await Task.Delay(FailedLoginDelay, Request.CancellationToken);

                return ErrorJSON(Request, HTTPStatusCode.Unauthorized, "Wrong username or password.");

            }

            Log.Notice($"'{session.UserId}' signed in from {Request.RemoteSocket} as {String.Join(", ", Sessions.Roles.Select(role => role.Name))}.", "web", "auth");

            return new HTTPResponse.Builder(Request) {
                       HTTPStatusCode  = HTTPStatusCode.OK,
                       ContentType     = HTTPContentType.Application.JSON_UTF8,
                       Content         = Encoding.UTF8.GetBytes(MeJSON(session).ToString(Formatting.None)),
                       CacheControl    = "no-store",
                       SetCookie       = Sessions.SessionCookie(session)
                   }.WithCommonSecurityHeaders().AsImmutable;

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

            if (Sessions.SignOut(Request))
                Log.Notice($"'{Sessions.Username}' signed out from {Request.RemoteSocket}.", "web", "auth");

            return Task.FromResult(
                       new HTTPResponse.Builder(Request) {
                           HTTPStatusCode  = HTTPStatusCode.NoContent,
                           CacheControl    = "no-store",
                           SetCookie       = Sessions.ExpiredCookie()
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
                   TryGetSession(Request, out var session, out var unauthorized)
                       ? JSONResponse(Request, HTTPStatusCode.OK, MeJSON(session))
                       : unauthorized
               );

        #endregion


        #region (private) GetStatus       (Request)

        /// <summary>
        /// GET /api/v1/status: how this local controller is doing right now.
        /// </summary>
        private Task<HTTPResponse> GetStatus(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
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
                               new JProperty("sessions",   Sessions.Count),
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
        /// POST /api/v1/configuration/dns/query with {"name", "recordTypes"}:
        /// make this local controller look a name up and say what came back.
        /// </summary>
        /// <remarks>
        /// A POST although it changes nothing here, because it makes this
        /// local controller send traffic to a host somebody named - which is not
        /// something to leave sitting in a URL that a browser may repeat,
        /// prefetch or put in a history.
        /// </remarks>
        private async Task<HTTPResponse> PostDNSQuery(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var session, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            var name = json.Value<String>("name")?.Trim();

            if (String.IsNullOrEmpty(name))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, "A 'name' to look up is required.");

            if (!LocalController.TryParseRecordTypes(json["recordTypes"], out var recordTypes, out var problem))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, problem);

            Log.Info($"'{session.UserId}' asked this local controller to resolve '{name}'.", "dns", "test", "web");

            return JSONResponse(
                       Request,
                       HTTPStatusCode.OK,
                       await Controller.ResolveAsync(name, recordTypes, Request.CancellationToken)
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

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var session, out var refused))
                return refused;

            Log.Info($"'{session.UserId}' asked this local controller to synchronise its time.", "nts", "test", "web");

            var result = await Controller.SyncTimeAsync(Request.CancellationToken);

            var json   = Controller.NTSConfigurationJSON();

            json["result"] = result;

            return JSONResponse(Request, HTTPStatusCode.OK, json);

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

            if (!TryGetSession(Request, out _, out var unauthorized))
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
        private Task<HTTPResponse> StreamEvents(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            var clientId = Request.RemoteSocket.ToString();

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

                                   await foreach (var httpEvent in Events.GetAllEventsGreater(
                                                                       clientId,
                                                                       Request.GetHeaderField(HTTPRequestHeaderField.LastEventId),
                                                                       ending.Token
                                                                   ))
                                   {
                                       await stream.WriteAsync(httpEvent.SerializedHeader);
                                       await stream.WriteAsync(httpEvent.SerializedData);
                                       await stream.WriteAsync("\n\n");
                                       await stream.FlushAsync(ending.Token);
                                   }

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

                       }.WithCommonSecurityHeaders().AsImmutable
                   );

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

        #region (private) TryGetSession(Request, out Session, out Unauthorized)

        /// <summary>
        /// The live session behind the request, or the 401 response - which
        /// also expires a stale cookie, so that the browser stops sending it.
        /// </summary>
        private Boolean TryGetSession(HTTPRequest                             Request,
                                      [NotNullWhen(true)]  out Session?        Session,
                                      [NotNullWhen(false)] out HTTPResponse?  Unauthorized)
        {

            if (Sessions.TryGetSession(Request, out Session))
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

            if (Sessions.HasCookie(Request))
                builder.SetCookie = Sessions.ExpiredCookie();

            Unauthorized = builder.WithCommonSecurityHeaders().AsImmutable;
            return false;

        }

        #endregion

        #region (private) TryAuthorize(Request, Required, StateChanging, out Session, out Refused)

        /// <summary>
        /// The live session behind the request, when it is allowed to do this -
        /// or the response that says why not.
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
        /// <param name="Session">The session behind it.</param>
        /// <param name="Refused">The response to send instead.</param>
        private Boolean TryAuthorize(HTTPRequest                             Request,
                                     Permissions                             Required,
                                     Boolean                                 StateChanging,
                                     [NotNullWhen(true)]  out Session?       Session,
                                     [NotNullWhen(false)] out HTTPResponse?  Refused)
        {

            Session = null;

            if (StateChanging && RefuseCrossSite(Request) is HTTPResponse crossSite)
            {
                Refused = crossSite;
                return false;
            }

            if (!TryGetSession(Request, out Session, out Refused))
                return false;

            var permissions = Sessions.PermissionsOf(Session);

            if (!permissions.HasFlag(Required))
            {
                Refused  = RefusePermission(Request, Session, Required, null);
                Session  = null;
                return false;
            }

            Refused = null;
            return true;

        }

        #endregion

        #region (private) RefusePermission(Request, Session, Required, Because)

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
                                              Session      Session,
                                              Permissions  Required,
                                              String?      Because)
        {

            // HasFlag with more than one flag asks for all of them, which is
            // what a role has to carry to do a change that was several kinds at
            // once. Nobody is named who could only do half of it.
            var allowed = UserRole.All.Where(role => role.Permissions.HasFlag(Required)).
                                       Select(role => role.Name);

            Log.Warning(
                $"'{Session.UserId}' was refused {Required} on {Request.HTTPMethod} {Request.Path}; " +
                $"signed in as {String.Join(", ", Sessions.Roles.Select(role => role.Name))}." +
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

        #region (private) MeJSON(Session)

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
        private JObject MeJSON(Session Session)

            => new (
                   new JProperty("username",     Session.UserId.ToString()),
                   new JProperty("roles",        new JArray(Sessions.Roles.Select(role => role.Name))),
                   new JProperty("permissions",  new JArray(Sessions.PermissionsOf(Session).Names())),
                   new JProperty("session",      new JObject(
                                                     new JProperty("createdAt",  Session.CreatedAt.ToString("o")),
                                                     new JProperty("expiresAt",  Session.ExpiresAt.ToString("o"))
                                                 ))
               );

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
