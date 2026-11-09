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

using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.OpenADRv3;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using OpenADRValueType = cloud.charging.open.protocols.OpenADRv3.ValueType;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// A VTN of OpenADR 3 that answers as the grid operator's does - below
    /// "/openadr3/", the client credentials grant, bearer tokens, RFC 9457
    /// problems - for one VEN, and counts what it was asked.
    /// </summary>
    /// <remarks>
    /// It keeps no time of its own: the events it lists are the ones it was
    /// given, whatever their periods, so that a test decides what is over by
    /// the clock of the controller alone.
    /// </remarks>
    internal sealed class FakeVTN : IAsyncDisposable
    {

        #region Data

        public const String  ProgramID  = "grid-limits";

        private readonly HTTPServer                  server;
        private readonly Lock                        stateLock  = new ();
        private readonly List<JObject>               events     = [];
        private readonly List<JObject>               reports    = [];
        private readonly HashSet<String>             tokens     = [];

        #endregion

        #region Properties

        public String   VENName           { get; }
        public String   Secret            { get; set; }
        public Int64    TokenLifetime     { get; set; } = 3600;
        public UInt16   Port              { get; }

        public String   VTNURL
            => $"http://127.0.0.1:{Port}/openadr3/";

        public Int32    TokenRequests     { get; private set; }
        public Int32    EventRequests     { get; private set; }
        public Int32    Refused401        { get; private set; }
        public Int32    ReportRequests    { get; private set; }

        /// <summary>
        /// The events whose reports are refused with 400.
        /// </summary>
        public HashSet<String>  RefuseReportsOn  { get; } = [];

        /// <summary>
        /// What the last token request said in its Authorization header.
        /// </summary>
        public String?  LastTokenAuthorization  { get; private set; }

        public IReadOnlyList<JObject> Reports
        {
            get { lock (stateLock) return [ .. reports ]; }
        }

        #endregion

        #region Constructor(s)

        private FakeVTN(String VENName, String Secret, UInt16 Port)
        {

            this.VENName  = VENName;
            this.Secret   = Secret;
            this.Port     = Port;

            server        = new HTTPServer(IPAddress: IPv4Address.Localhost,
                                           TCPPort:   IPPort.Parse(Port));

            var api       = new HTTPAPI(server, RootPath: HTTPPath.Parse("/openadr3"));
            var root      = HTTPPath.Root;

            api.AddHandler(root + "auth/server",  GetAuthServer,  HTTPMethod.GET);
            api.AddHandler(root + "auth/token",   PostToken,      HTTPMethod.POST);
            api.AddHandler(root + "events",       GetEvents,      HTTPMethod.GET);
            api.AddHandler(root + "reports",      GetReports,     HTTPMethod.GET);
            api.AddHandler(root + "reports",      PostReport,     HTTPMethod.POST);

        }

        /// <summary>
        /// A VTN listening on a free port of the loopback.
        /// </summary>
        public static async Task<FakeVTN> Started(String VENName, String Secret)
        {
            var vtn = new FakeVTN(VENName, Secret, TestPorts.Free());
            await vtn.server.Start();
            return vtn;
        }

        #endregion


        #region Send(Kind, KW, Start, Duration, Name = null, VENName = null)

        /// <summary>
        /// A limit, as the grid operator sends one: an event with one interval,
        /// targeted at a VEN name - this VTN's VEN unless another is given.
        /// </summary>
        public String Send(String          Kind,
                           Double          KW,
                           DateTimeOffset  Start,
                           TimeSpan        Duration,
                           String?         Name     = null,
                           String?         VENName  = null)
        {

            var id    = Event_Id.NewRandom;

            var json  = new Event(Object_Id.Parse(ProgramID),
                                  [ new Interval(Interval_Id.Parse(0),
                                                 [ new ValuesMap(OpenADRValueType.Parse(Kind), [ KW ]) ]) ],
                                  EventName:           Name,
                                  Targets:             [ new ValuesMap(OpenADRValueType.Parse("VEN_NAME"), [ VENName ?? this.VENName ]) ],
                                  PayloadDescriptors:  [ new EventPayloadDescriptor(PayloadType.Parse(Kind), UnitType.Parse("KW")) ],
                                  IntervalPeriod:      new IntervalPeriod(Start, Duration),
                                  Id:                  id,
                                  Created:             Start,
                                  LastModification:    Start).ToJSON();

            json["objectType"] = "EVENT";

            lock (stateLock)
                events.Add(json);

            return id.ToString();

        }

        /// <summary>
        /// An event as it is given, for one a grid operator might send that
        /// this VTN would not make.
        /// </summary>
        public void Add(JObject Event)
        {
            lock (stateLock)
                events.Add(Event);
        }

        public void Withdraw(String EventId)
        {
            lock (stateLock)
                events.RemoveAll(json => json.Value<String>("id") == EventId);
        }

        /// <summary>
        /// Let go of every token: a VTN that was restarted.
        /// </summary>
        public void ForgetTokens()
        {
            lock (stateLock)
                tokens.Clear();
        }

        #endregion


        #region (private) Handlers

        private Task<HTTPResponse> GetAuthServer(HTTPRequest Request)

            => JSON(Request, HTTPStatusCode.OK, new JObject(new JProperty("tokenURL", VTNURL + "auth/token")));

        private Task<HTTPResponse> PostToken(HTTPRequest Request)
        {

            TokenRequests++;

            var authorization       = Request.GetHeaderField("Authorization") ?? "";
            LastTokenAuthorization  = authorization;

            String? clientId     = null;
            String? clientSecret = null;

            if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                var pair  = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim()));
                var colon = pair.IndexOf(':');
                clientId      = Uri.UnescapeDataString(pair[..colon]);
                clientSecret  = Uri.UnescapeDataString(pair[(colon + 1)..]);
            }

            if (!(Request.HTTPBodyAsUTF8String ?? "").Contains("grant_type=client_credentials"))
                return JSON(Request, HTTPStatusCode.BadRequest, new JObject(new JProperty("error", "unsupported_grant_type")));

            if (clientId != VENName || clientSecret != Secret)
                return JSON(Request, HTTPStatusCode.Unauthorized, new JObject(
                           new JProperty("error",              "invalid_client"),
                           new JProperty("error_description",  "No local controller signs in with these client credentials.")
                       ));

            var token = Guid.NewGuid().ToString("N");

            lock (stateLock)
                tokens.Add(token);

            return JSON(Request, HTTPStatusCode.OK, new JObject(
                       new JProperty("access_token",  token),
                       new JProperty("token_type",    "Bearer"),
                       new JProperty("expires_in",    TokenLifetime)
                   ));

        }

        private Task<HTTPResponse> GetEvents(HTTPRequest Request)
        {

            if (!SignedIn(Request))
                return Unauthorized(Request);

            EventRequests++;

            JObject[] page;

            lock (stateLock)
                page = [ .. events.Skip(Int32.Parse(Request.QueryString.GetString("skip") ?? "0")).
                                   Take(Int32.Parse(Request.QueryString.GetString("limit") ?? "50")) ];

            return JSON(Request, HTTPStatusCode.OK, new JArray(page));

        }

        private Task<HTTPResponse> GetReports(HTTPRequest Request)
        {

            if (!SignedIn(Request))
                return Unauthorized(Request);

            lock (stateLock)
                return JSON(Request, HTTPStatusCode.OK, new JArray(reports));

        }

        /// <summary>
        /// A report, checked as the grid operator's VTN checks one: a report of
        /// OpenADR 3, on an event meant for this VEN, of that event's program.
        /// </summary>
        private Task<HTTPResponse> PostReport(HTTPRequest Request)
        {

            if (!SignedIn(Request))
                return Unauthorized(Request);

            ReportRequests++;

            var json = JObject.Parse(Request.HTTPBodyAsUTF8String ?? "{}");

            if (!Report.TryParse(json, out var report, out var error))
                return Problem(Request, HTTPStatusCode.BadRequest, error);

            if (RefuseReportsOn.Contains(report.EventId.ToString()))
                return Problem(Request, HTTPStatusCode.BadRequest, "This report is not taken.");

            lock (stateLock)
            {

                var gridEvent = events.FirstOrDefault(e => e.Value<String>("id") == report.EventId.ToString());

                if (gridEvent is null)
                    return Problem(Request, HTTPStatusCode.NotFound, $"There is no event '{report.EventId}' for '{VENName}' to report on.");

                if (gridEvent.Value<String>("programID") != report.ProgramId.ToString())
                    return Problem(Request, HTTPStatusCode.BadRequest, $"The event '{report.EventId}' is of another program.");

                var kept = report.ToJSON();
                kept["id"]          = Guid.NewGuid().ToString("N");
                kept["clientName"]  = VENName;
                kept["objectType"]  = "REPORT";

                reports.Add(kept);

                return JSON(Request, HTTPStatusCode.Created, kept);

            }

        }

        #endregion

        #region (private) SignedIn(Request) / Unauthorized(Request) / JSON(...) / Problem(...)

        private Boolean SignedIn(HTTPRequest Request)
        {

            var authorization = Request.GetHeaderField("Authorization") ?? "";

            lock (stateLock)
                return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
                       tokens.Contains(authorization[7..].Trim());

        }

        private Task<HTTPResponse> Unauthorized(HTTPRequest Request)
        {
            Refused401++;
            return Problem(Request, HTTPStatusCode.Unauthorized, "Sign in first.");
        }

        private static Task<HTTPResponse> JSON(HTTPRequest Request, HTTPStatusCode Status, JToken JSON)

            => Task.FromResult(
                   new HTTPResponse.Builder(Request) {
                       HTTPStatusCode  = Status,
                       ContentType     = HTTPContentType.Application.JSON_UTF8,
                       Content         = Encoding.UTF8.GetBytes(JSON.ToString(Formatting.None))
                   }.AsImmutable
               );

        private static Task<HTTPResponse> Problem(HTTPRequest Request, HTTPStatusCode Status, String Detail)

            => JSON(Request, Status, new JObject(
                   new JProperty("type",    "about:blank"),
                   new JProperty("title",   Status.Description),
                   new JProperty("status",  Status.Code),
                   new JProperty("detail",  Detail)
               ));

        #endregion

        #region DisposeAsync()

        public async ValueTask DisposeAsync()
        {
            await server.Stop();
            await server.DisposeAsync();
        }

        #endregion

    }

}
