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

using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.OpenADRv3;

#endregion

namespace cloud.charging.open.LocalController.OpenADR
{

    /// <summary>
    /// What a VTN said no to, or why it could not be asked: the sentence of its
    /// problem (RFC 9457) or of its OAuth error, and the status it came with -
    /// null where no answer came at all.
    /// </summary>
    public sealed class VTNException(String           Message,
                                     HttpStatusCode?  Status      = null,
                                     Exception?       Inner       = null)

        : Exception(Message, Inner)

    {

        /// <summary>
        /// The HTTP status of the answer, or null where there was none.
        /// </summary>
        public HttpStatusCode?  Status  { get; } = Status;

    }


    /// <summary>
    /// The HTTP client of a VEN of OpenADR 3: it signs in at the VTN with the
    /// client credentials grant of OAuth 2.0, keeps the bearer token it gets,
    /// renews it before it expires and when the VTN no longer takes it, and
    /// reads the events meant for this VEN and sends its reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where the token is had is asked of the VTN - <c>GET auth/server</c> -
    /// and is <c>auth/token</c> below the VTN where it does not say. A VTN
    /// reached over HTTPS that names a token URL of plain HTTP is not believed:
    /// the secret would go there in the clear.
    /// </para>
    /// <para>
    /// The secret is asked for at every sign-in rather than kept here, so that
    /// a secret set anew is the one the next sign-in sends.
    /// </para>
    /// </remarks>
    public sealed class VENClient : IDisposable
    {

        #region Data

        /// <summary>
        /// The most objects one page of the VTN lists, as OpenADR 3 defaults to.
        /// </summary>
        public const Int32  PageSize  = 50;

        /// <summary>
        /// The most pages read in one go: two thousand five hundred events meant
        /// for one charging pool are not a list, they are a VTN gone wrong.
        /// </summary>
        public const Int32  MaxPages  = 50;

        /// <summary>
        /// How long before it expires a token is renewed at most: five minutes,
        /// or a quarter of its life where it is shorter than twenty.
        /// </summary>
        public static readonly TimeSpan  MaxRenewBefore  = TimeSpan.FromMinutes(5);

        private readonly HttpClient      http;
        private readonly Func<String?>   secret;
        private readonly SemaphoreSlim   signInLock  = new (1, 1);

        private String?          token;
        private DateTimeOffset?  tokenExpires;
        private DateTimeOffset?  tokenRenewAt;
        private Uri?             tokenURL;

        #endregion

        #region Properties

        /// <summary>
        /// Where the VTN is, ending with a slash.
        /// </summary>
        public Uri           VTNURL          { get; }

        /// <summary>
        /// Who this VEN signs in as: its VEN name.
        /// </summary>
        public String        ClientID        { get; }

        /// <summary>
        /// The clock tokens expire by.
        /// </summary>
        public TimeProvider  TimeProvider    { get; }

        /// <summary>
        /// Where the token is had, once it was asked; null before.
        /// </summary>
        public Uri?          TokenURL
            => tokenURL;

        /// <summary>
        /// When the token this VEN holds expires; null while it holds none.
        /// </summary>
        public DateTimeOffset?  TokenExpires
            => tokenExpires;

        /// <summary>
        /// How many tokens this client was given since it was made.
        /// </summary>
        public Int32         TokensReceived  { get; private set; }

        #endregion

        #region Events

        /// <summary>
        /// A token was had: when it expires, and whether it replaced one.
        /// </summary>
        public event Action<DateTimeOffset, Boolean>?  OnSignedIn;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// A VEN client for the given VTN.
        /// </summary>
        /// <param name="VTNURL">Where the VTN is.</param>
        /// <param name="ClientID">The VEN name this local controller signs in as.</param>
        /// <param name="Secret">Where its client secret is read at every sign-in.</param>
        /// <param name="Handler">What carries the requests: one that resolves names and judges certificates as the controller does.</param>
        /// <param name="RequestTimeout">How long one request may take.</param>
        /// <param name="TimeProvider">The clock tokens expire by.</param>
        public VENClient(Uri                 VTNURL,
                         String              ClientID,
                         Func<String?>       Secret,
                         HttpMessageHandler  Handler,
                         TimeSpan            RequestTimeout,
                         TimeProvider?       TimeProvider   = null)
        {

            this.VTNURL        = new Uri(Configuration.OpenADRConfiguration.NormalizedURL(VTNURL.ToString()));
            this.ClientID      = ClientID;
            this.secret        = Secret;
            this.TimeProvider  = TimeProvider ?? System.TimeProvider.System;

            this.http          = new HttpClient(Handler, disposeHandler: true) {
                                     Timeout = RequestTimeout
                                 };

            http.DefaultRequestHeaders.UserAgent.ParseAdd($"LocalController/{typeof(VENClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        }

        #endregion


        #region SignIn(Renew, CancellationToken)

        /// <summary>
        /// Have a token: the one held where it is not about to expire, and a new
        /// one otherwise - or always, with <paramref name="Renew"/>.
        /// </summary>
        public async Task<String> SignIn(Boolean            Renew               = false,
                                         CancellationToken  CancellationToken   = default)
        {

            await signInLock.WaitAsync(CancellationToken).ConfigureAwait(false);

            try
            {

                if (!Renew &&
                    token is String held &&
                    TimeProvider.GetUtcNow() < tokenRenewAt)
                {
                    return held;
                }

                var secretNow = secret() ?? throw new VTNException($"There is no client secret to sign in at {VTNURL} with.");

                tokenURL ??= await AskTokenURL(CancellationToken).ConfigureAwait(false);

                using var request = new HttpRequestMessage(HttpMethod.Post, tokenURL) {
                                        Content = new FormUrlEncodedContent([
                                                      new KeyValuePair<String, String>("grant_type", "client_credentials")
                                                  ])
                                    };

                // RFC 6749, 2.3.1: both parts form-encoded before they are joined.
                request.Headers.Authorization = new AuthenticationHeaderValue(
                                                    "Basic",
                                                    Convert.ToBase64String(Encoding.UTF8.GetBytes(
                                                        $"{Uri.EscapeDataString(ClientID)}:{Uri.EscapeDataString(secretNow)}"))
                                                );

                var (status, json) = await Send(request, CancellationToken).ConfigureAwait(false);

                if (status != HttpStatusCode.OK)
                {

                    // A token URL somebody else answers - nothing there, a page
                    // of a proxy - is asked anew next time.
                    if (status == HttpStatusCode.NotFound)
                        tokenURL = null;

                    throw new VTNException($"The VTN did not sign this local controller in as '{ClientID}': {Said(status, json)}", status);

                }

                if (json is not JObject answer ||
                    answer.Value<String>("access_token") is not { Length: > 0 } accessToken)
                {
                    throw new VTNException($"The VTN signed this local controller in at {tokenURL}, but gave it no access_token.", status);
                }

                var now       = TimeProvider.GetUtcNow();
                var lifetime  = answer.Value<Double?>("expires_in") is Double seconds && seconds > 0
                                    ? TimeSpan.FromSeconds(seconds)
                                    : TimeSpan.FromHours(1);

                var renewed   = token is not null;

                token         = accessToken;
                tokenExpires  = now + lifetime;
                tokenRenewAt  = tokenExpires - (lifetime / 4 < MaxRenewBefore ? lifetime / 4 : MaxRenewBefore);

                TokensReceived++;

                OnSignedIn?.Invoke(tokenExpires.Value, renewed);

                return accessToken;

            }
            finally
            {
                signInLock.Release();
            }

        }

        #endregion

        #region SignOut()

        /// <summary>
        /// Forget the token, so that the next request signs in anew.
        /// </summary>
        public void SignOut()
        {
            token         = null;
            tokenExpires  = null;
            tokenRenewAt  = null;
        }

        #endregion


        #region GetEvents(CancellationToken)

        /// <summary>
        /// Every event the VTN has for this VEN, page by page.
        /// </summary>
        public async Task<IReadOnlyList<JObject>> GetEvents(CancellationToken CancellationToken = default)

            => await GetAll("events", CancellationToken).ConfigureAwait(false);

        #endregion

        #region GetReports(CancellationToken)

        /// <summary>
        /// Every report this VEN has sent, as the VTN keeps them.
        /// </summary>
        public async Task<IReadOnlyList<JObject>> GetReports(CancellationToken CancellationToken = default)

            => await GetAll("reports", CancellationToken).ConfigureAwait(false);

        #endregion

        #region PostReport(Report, CancellationToken)

        /// <summary>
        /// Send a report, and get it back as the VTN keeps it.
        /// </summary>
        public async Task<JObject> PostReport(Report             Report,
                                              CancellationToken  CancellationToken = default)
        {

            var json = Report.ToJSON();
            json["objectType"] = "REPORT";

            var (status, answer) = await Authorized(() => new HttpRequestMessage(HttpMethod.Post, new Uri(VTNURL, "reports")) {
                                                              Content = new StringContent(json.ToString(Formatting.None), Encoding.UTF8, "application/json")
                                                          },
                                                    CancellationToken).ConfigureAwait(false);

            if (status != HttpStatusCode.Created && status != HttpStatusCode.OK)
                throw new VTNException($"The VTN did not take the report on the event '{Report.EventId}': {Said(status, answer)}", status);

            return answer as JObject ?? new JObject();

        }

        #endregion


        #region (private) GetAll(Path, CancellationToken)

        /// <summary>
        /// A list of the VTN, "?skip" and "?limit" until a page is not full.
        /// </summary>
        private async Task<IReadOnlyList<JObject>> GetAll(String             Path,
                                                          CancellationToken  CancellationToken)
        {

            var all = new List<JObject>();

            for (var page = 0; page < MaxPages; page++)
            {

                var (status, json) = await Authorized(() => new HttpRequestMessage(HttpMethod.Get, new Uri(VTNURL, $"{Path}?skip={page * PageSize}&limit={PageSize}")),
                                                      CancellationToken).ConfigureAwait(false);

                if (status != HttpStatusCode.OK)
                    throw new VTNException($"The VTN did not list its {Path}: {Said(status, json)}", status);

                if (json is not JArray array)
                    throw new VTNException($"The VTN listed its {Path} as something else than a JSON array.", status);

                all.AddRange(array.OfType<JObject>());

                if (array.Count < PageSize)
                    return all;

            }

            throw new VTNException($"The VTN listed more than {MaxPages * PageSize} {Path}; this local controller stopped reading there.");

        }

        #endregion

        #region (private) Authorized(Request, CancellationToken)

        /// <summary>
        /// A request with the bearer token - and once more with a new one where
        /// the VTN says 401 to the one held: a VTN that was restarted, or that
        /// let the token go before its time.
        /// </summary>
        private async Task<(HttpStatusCode Status, JToken? JSON)> Authorized(Func<HttpRequestMessage>  Request,
                                                                              CancellationToken         CancellationToken)
        {

            var bearer = await SignIn(false, CancellationToken).ConfigureAwait(false);

            using (var first = Request())
            {

                first.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

                var answer = await Send(first, CancellationToken).ConfigureAwait(false);

                if (answer.Status != HttpStatusCode.Unauthorized)
                    return answer;

            }

            bearer = await SignIn(true, CancellationToken).ConfigureAwait(false);

            using var second = Request();

            second.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            return await Send(second, CancellationToken).ConfigureAwait(false);

        }

        #endregion

        #region (private) AskTokenURL(CancellationToken)

        /// <summary>
        /// Where the VTN says its token is had - or auth/token below it, where it
        /// says nothing this local controller may believe.
        /// </summary>
        private async Task<Uri> AskTokenURL(CancellationToken CancellationToken)
        {

            var fallback = new Uri(VTNURL, "auth/token");

            try
            {

                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(VTNURL, "auth/server"));

                var (status, json) = await Send(request, CancellationToken).ConfigureAwait(false);

                if (status == HttpStatusCode.OK &&
                    json is JObject answer &&
                    answer.Value<String>("tokenURL") is String said &&
                    Uri.TryCreate(said, UriKind.Absolute, out var told) &&
                    (told.Scheme == Uri.UriSchemeHttps ||
                     told.Scheme == Uri.UriSchemeHttp && VTNURL.Scheme == Uri.UriSchemeHttp))
                {
                    return told;
                }

            }
            catch (VTNException)
            {
                // Asked again at the token URL itself, which says what is wrong.
            }

            return fallback;

        }

        #endregion

        #region (private) Send(Request, CancellationToken)

        /// <summary>
        /// One request, and its answer as JSON where it is JSON.
        /// </summary>
        private async Task<(HttpStatusCode Status, JToken? JSON)> Send(HttpRequestMessage  Request,
                                                                        CancellationToken   CancellationToken)
        {

            HttpResponseMessage response;

            try
            {
                response = await http.SendAsync(Request, CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException e)
            {
                throw new VTNException($"The VTN at {Request.RequestUri?.GetLeftPart(UriPartial.Authority)} did not answer within {http.Timeout.TotalSeconds:0} second(s).", null, e);
            }
            catch (HttpRequestException e)
            {
                throw new VTNException($"The VTN at {Request.RequestUri?.GetLeftPart(UriPartial.Authority)} could not be reached: {Innermost(e).TrimEnd('.')}.", null, e);
            }

            using (response)
            {

                var text = await response.Content.ReadAsStringAsync(CancellationToken).ConfigureAwait(false);

                JToken? json = null;

                if (text.Length > 0)
                {
                    try
                    {
                        json = JToken.Parse(text);
                    }
                    catch (JsonException)
                    {
                        // Not JSON: a page of a proxy, an empty answer. What it
                        // was is said by its status.
                    }
                }

                return (response.StatusCode, json);

            }

        }

        #endregion

        #region (private static) Said(Status, JSON) / Innermost(Exception)

        /// <summary>
        /// What an answer that is no yes says: the detail of an RFC 9457 problem,
        /// the description of an OAuth error, or the status alone.
        /// </summary>
        private static String Said(HttpStatusCode  Status,
                                   JToken?         JSON)
        {

            var code  = $"{(Int32) Status} {Status}";

            if (JSON is JObject answer)
            {

                if (answer.Value<String>("detail") is { Length: > 0 } detail)
                    return $"{code}, {detail.TrimEnd('.')}.";

                if (answer.Value<String>("error") is { Length: > 0 } error)
                    return $"{code}, {error}" +
                           (answer.Value<String>("error_description") is { Length: > 0 } description ? $": {description.TrimEnd('.')}." : ".");

                if (answer.Value<String>("title") is { Length: > 0 } title)
                    return $"{code}, {title.TrimEnd('.')}.";

            }

            return code + ".";

        }

        private static String Innermost(Exception Exception)
        {

            while (Exception.InnerException is not null)
                Exception = Exception.InnerException;

            return Exception.Message;

        }

        #endregion

        #region Dispose()

        public void Dispose()
        {
            http.Dispose();
            signInLock.Dispose();
        }

        #endregion

    }

}
