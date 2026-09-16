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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.LocalController.OCPP;
using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The part of the JSON API that is about the charging station server: its
    /// socket, the certificates it presents, the chains it accepts and the
    /// charging stations that may sign in.
    /// </summary>
    /// <remarks>
    /// Four pages worth of routes, in four groups, and the groups are not the
    /// same permission. Reading is reading; changing the server and the list of
    /// charging stations is a day's work on a site; generating a key and naming
    /// a certificate authority is neither. See <see cref="Permissions"/>.
    /// </remarks>
    public partial class LCHTTPAPI
    {

        #region (private) RegisterOCPPServerRoutes()

        /// <summary>
        /// Everything under /v1/configuration/ocpp-server.
        /// </summary>
        private void RegisterOCPPServerRoutes()
        {

            var root = HTTPPath.Root + "v1/configuration/ocpp-server";

            AddHandler(root,                          GetOCPPServer,            HTTPMethod.GET);
            AddHandler(root,                          PutOCPPServer,            HTTPMethod.PUT);

            AddHandler(root + "certificates",         GetCertificates,          HTTPMethod.GET);
            AddHandler(root + "certificates",         PostCertificateRequest,   HTTPMethod.POST);
            AddHandler(root + "certificates/{id}/csr", GetCertificateRequest,   HTTPMethod.GET);
            AddHandler(root + "certificates/{id}",    PutCertificate,           HTTPMethod.PUT);
            AddHandler(root + "certificates/{id}",    DeleteCertificate,        HTTPMethod.DELETE);

            AddHandler(root + "trust",                GetTrust,                 HTTPMethod.GET);
            AddHandler(root + "trust",                PostTrust,                HTTPMethod.POST);
            AddHandler(root + "trust/{id}",           PutTrust,                 HTTPMethod.PUT);
            AddHandler(root + "trust/{id}",           DeleteTrust,              HTTPMethod.DELETE);

            AddHandler(root + "stations",             GetStations,              HTTPMethod.GET);
            AddHandler(root + "stations",             PostStation,              HTTPMethod.POST);
            AddHandler(root + "stations/{id}",        PutStation,               HTTPMethod.PUT);
            AddHandler(root + "stations/{id}",        DeleteStation,            HTTPMethod.DELETE);

        }

        #endregion


        #region (private) GetOCPPServer(Request) / PutOCPPServer(Request)

        /// <summary>
        /// GET /api/v1/configuration/ocpp-server: the server the charging
        /// stations connect to.
        /// </summary>
        private Task<HTTPResponse> GetOCPPServer(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.OCPPServerConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/ocpp-server: change it.
        /// </summary>
        /// <remarks>
        /// Answers with the whole thing as it now stands - including
        /// <c>state.waitingForARestart</c>, which is how the page finds out
        /// that the port it just changed is not the port that is listening.
        /// </remarks>
        private Task<HTTPResponse> PutOCPPServer(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeStationSettings, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Controller.TryUpdateOCPPServerConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            Log.Info($"'{session.UserId}' changed the charging station server.", "ocpp", "station", "config", "web");

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.OCPPServerConfigurationJSON())
                   );

        }

        #endregion

        #region (private) The certificates this controller presents

        /// <summary>
        /// GET .../certificates: the keys, their signing requests and the
        /// certificates that answered them.
        /// </summary>
        private Task<HTTPResponse> GetCertificates(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            // Recomputed before it is shown: what is wrong with a certificate
            // depends on the names this controller is reachable under and on
            // what day it is, and both change without the certificate moving.
            Controller.ServerCertificates.CheckExpiry(Controller.OCPPServerSettings.ReachableAs ?? []);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.ServerCertificates.ToJSON())
                   );

        }

        /// <summary>
        /// POST .../certificates with {"subject", "algorithm"}: generate a key
        /// pair and the signing request that goes with it.
        /// </summary>
        /// <remarks>
        /// The request comes back in the response as well as being kept, so
        /// that the page can offer it for download without a second round trip.
        /// The private key does not come back, and there is no route that would
        /// hand it out.
        /// </remarks>
        private Task<HTTPResponse> PostCertificateRequest(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ManageCertificates, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            var reachableAs = Controller.OCPPServerSettings.ReachableAs ?? [];

            // Asked first, because without it the store would refuse for the
            // want of a subject - and the subject is not what is missing.
            if (reachableAs.Count == 0)
                return Task.FromResult(
                           ErrorJSON(
                               Request,
                               HTTPStatusCode.BadRequest,
                               "This local controller has not been told what it is reachable as, and a signing request without " +
                               "those names produces a certificate no charging station will accept. Fill in 'reachable as' first."
                           )
                       );

            if (!Controller.ServerCertificates.TryCreateKey(
                     json.Value<String>("subject") ?? (reachableAs.Count > 0 ? reachableAs[0] : ""),
                     reachableAs,
                     json.Value<String>("algorithm"),
                     out var id,
                     out var csr,
                     out var error))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));
            }

            Log.Notice($"'{session.UserId}' had this local controller generate the key '{id}'.", "ocpp", "tls", "web");

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.Created,
                           new JObject(
                               new JProperty("id",   id),
                               new JProperty("csr",  csr)
                           )
                       )
                   );

        }

        /// <summary>
        /// GET .../certificates/{id}/csr: the signing request, as a file to
        /// hand to a certificate authority.
        /// </summary>
        private Task<HTTPResponse> GetCertificateRequest(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryGetId(Request, out var id, out var badRequest))
                return Task.FromResult(badRequest);

            if (!Controller.ServerCertificates.TryReadCSR(id, out var csr, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.NotFound, error));

            return Task.FromResult(
                       new HTTPResponse.Builder(Request) {
                           HTTPStatusCode  = HTTPStatusCode.OK,
                           ContentType     = HTTPContentType.Text.PLAIN,
                           Content         = Encoding.UTF8.GetBytes(csr),
                           CacheControl    = "no-store",
                           ContentDisposition = $"attachment; filename=\"{id}.csr.pem\""
                       }.WithCommonSecurityHeaders().AsImmutable
                   );

        }

        /// <summary>
        /// PUT .../certificates/{id} with {"pem"}: take in the certificate that
        /// answers a signing request, and whatever intermediates came with it.
        /// </summary>
        /// <remarks>
        /// The identification in the path is what the caller believes it is
        /// uploading for; which key the certificate actually belongs to is
        /// worked out from the certificate itself, and a mismatch is an error
        /// rather than a silent filing under the right name. Somebody who
        /// uploads the wrong file to the right row should be told.
        /// </remarks>
        private Task<HTTPResponse> PutCertificate(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ManageCertificates, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryGetId(Request, out var id, out var badRequest))
                return Task.FromResult(badRequest);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            var pem = json.Value<String>("pem") ?? "";

            if (pem.Length == 0)
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "A 'pem' with the certificate in it is required."));

            if (!Controller.ServerCertificates.TryAddCertificate(
                     pem,
                     Controller.OCPPServerSettings.ReachableAs ?? [],
                     out var actual,
                     out var warnings,
                     out var error))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));
            }

            if (actual != id)
                return Task.FromResult(
                           ErrorJSON(
                               Request,
                               HTTPStatusCode.Conflict,
                               $"This certificate belongs to the key '{actual}', not to '{id}'. It has been taken in under '{actual}'."
                           )
                       );

            Log.Notice($"'{session.UserId}' uploaded a certificate for the key '{actual}'.", "ocpp", "tls", "web");

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.OK,
                           new JObject(
                               new JProperty("id",        actual),
                               new JProperty("warnings",  new JArray(warnings))
                           )
                       )
                   );

        }

        /// <summary>
        /// DELETE .../certificates/{id}: throw a key and its certificate away.
        /// </summary>
        private Task<HTTPResponse> DeleteCertificate(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ManageCertificates, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryGetId(Request, out var id, out var badRequest))
                return Task.FromResult(badRequest);

            if (!Controller.ServerCertificates.TryRemove(id, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.Conflict, error));

            Log.Notice($"'{session.UserId}' removed the key '{id}'.", "ocpp", "tls", "web");

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.ServerCertificates.ToJSON())
                   );

        }

        #endregion

        #region (private) The chains this controller accepts

        /// <summary>
        /// GET .../trust: which certificate authorities a charging station may
        /// be vouched for by.
        /// </summary>
        private Task<HTTPResponse> GetTrust(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.ClientTrust.ToJSON())
                   );

        }

        /// <summary>
        /// POST .../trust with {"pem", "name"}: accept charging stations whose
        /// certificate leads to the one in this file.
        /// </summary>
        private Task<HTTPResponse> PostTrust(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ManageCertificates, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            var pem = json.Value<String>("pem") ?? "";

            if (pem.Length == 0)
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "A 'pem' with the certificate authority in it is required."));

            if (!Controller.ClientTrust.TryAdd(pem, json.Value<String>("name"), out var id, out var warnings, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            Log.Notice($"'{session.UserId}' added the accepted chain '{id}'.", "ocpp", "tls", "trust", "web");

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.Created,
                           new JObject(
                               new JProperty("id",        id),
                               new JProperty("warnings",  new JArray(warnings))
                           )
                       )
                   );

        }

        /// <summary>
        /// PUT .../trust/{id} with {"enabled"} and/or {"name"}: switch a chain
        /// off without losing it, or give it another name.
        /// </summary>
        private Task<HTTPResponse> PutTrust(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ManageCertificates, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryGetId(Request, out var id, out var badRequest))
                return Task.FromResult(badRequest);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (json.Value<String>("name") is { Length: > 0 } name &&
                !Controller.ClientTrust.TryRename(id, name, out var renameError))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, renameError));

            if (json.Value<Boolean?>("enabled") is Boolean enabled &&
                !Controller.ClientTrust.TrySetEnabled(id, enabled, out var enableError))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, enableError));

            Log.Info($"'{session.UserId}' changed the accepted chain '{id}'.", "ocpp", "tls", "trust", "web");

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.ClientTrust.ToJSON())
                   );

        }

        /// <summary>
        /// DELETE .../trust/{id}: stop accepting stations that lead to it.
        /// </summary>
        private Task<HTTPResponse> DeleteTrust(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ManageCertificates, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryGetId(Request, out var id, out var badRequest))
                return Task.FromResult(badRequest);

            if (!Controller.ClientTrust.TryRemove(id, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.NotFound, error));

            Log.Notice($"'{session.UserId}' removed the accepted chain '{id}'.", "ocpp", "tls", "trust", "web");

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.ClientTrust.ToJSON())
                   );

        }

        #endregion

        #region (private) The charging stations that may sign in

        /// <summary>
        /// GET .../stations: which charging stations may sign in.
        /// </summary>
        private Task<HTTPResponse> GetStations(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.StationLogins.ToJSON())
                   );

        }

        /// <summary>
        /// POST .../stations with {"id", "password", "note"}: add a charging
        /// station, or give one a new password.
        /// </summary>
        /// <remarks>
        /// An empty password means "make one up", and the made-up one comes
        /// back in this response and nowhere else - it is kept only as a hash,
        /// so a second look is a second password. The page has to show it once
        /// and say so.
        /// </remarks>
        private Task<HTTPResponse> PostStation(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeStationSettings, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            var id = json.Value<String>("id") ?? "";

            if (!Controller.StationLogins.TrySetPassword(
                     id,
                     json.Value<String>("password"),
                     json.Value<String>("note"),
                     out var generated,
                     out var error))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));
            }

            Log.Notice($"'{session.UserId}' set the password of the charging station '{id}'.", "ocpp", "station", "auth", "web");

            var response = new JObject(
                               new JProperty("id",        id),
                               new JProperty("stations",  Controller.StationLogins.ToJSON())
                           );

            if (generated is not null)
                response.Add("password", generated);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, response));

        }

        /// <summary>
        /// PUT .../stations/{id} with {"enabled"}: let a charging station in,
        /// or stop letting it in, without losing its password.
        /// </summary>
        private Task<HTTPResponse> PutStation(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeStationSettings, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryGetId(Request, out var id, out var badRequest))
                return Task.FromResult(badRequest);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (json.Value<Boolean?>("enabled") is not Boolean enabled)
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "An 'enabled' of true or false is required."));

            if (!Controller.StationLogins.TrySetEnabled(id, enabled, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.NotFound, error));

            Log.Info($"'{session.UserId}' {(enabled ? "let in" : "shut out")} the charging station '{id}'.", "ocpp", "station", "auth", "web");

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.StationLogins.ToJSON())
                   );

        }

        /// <summary>
        /// DELETE .../stations/{id}: forget a charging station.
        /// </summary>
        private Task<HTTPResponse> DeleteStation(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeStationSettings, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryGetId(Request, out var id, out var badRequest))
                return Task.FromResult(badRequest);

            if (!Controller.StationLogins.TryRemove(id, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.NotFound, error));

            Log.Notice($"'{session.UserId}' removed the charging station '{id}'.", "ocpp", "station", "auth", "web");

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.StationLogins.ToJSON())
                   );

        }

        #endregion

        #region (private static) TryGetId(Request, out Id, out ErrorResponse)

        /// <summary>
        /// The identification out of the path, or the 400 that says there was
        /// none.
        /// </summary>
        private static Boolean TryGetId(HTTPRequest         Request,
                                        out String          Id,
                                        out HTTPResponse    ErrorResponse)
        {

            Id             = Request.ParsedURLParameters.FirstOrDefault()?.Trim() ?? "";
            ErrorResponse  = default!;

            if (Id.Length == 0)
            {
                ErrorResponse = ErrorJSON(Request, HTTPStatusCode.BadRequest, "An identification is required.");
                return false;
            }

            return true;

        }

        #endregion

    }

}
