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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Certificates;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The part of the JSON API that is about this local controller's own
    /// certificate store: the TLS roots of the servers it connects to, their
    /// certificates, and what it presents in TLS itself.
    /// </summary>
    /// <remarks>
    /// The vehicle's routes, over the node's store, and the same answers - so
    /// that one page and one set of habits serve both. Not the charging station
    /// port's certificates and trusted chains, which have stores of their own
    /// under /v1/configuration/ocpp-server.
    /// </remarks>
    public partial class LCHTTPAPI
    {

        #region (private) RegisterCertificateStoreRoutes()

        /// <summary>
        /// Everything under /v1/certificates.
        /// </summary>
        private void RegisterCertificateStoreRoutes()
        {

            var root = HTTPPath.Root + "v1/certificates";

            AddHandler(root,               GetStoreCertificates,        HTTPMethod.GET);
            AddHandler(root,               PostStoreCertificate,        HTTPMethod.POST);
            AddHandler(root + "/reload",   PostStoreCertificateReload,  HTTPMethod.POST);
            AddHandler(root + "/{id}",     GetStoreCertificate,         HTTPMethod.GET);
            AddHandler(root + "/{id}",     PatchStoreCertificate,       HTTPMethod.PATCH);
            AddHandler(root + "/{id}",     DeleteStoreCertificate,      HTTPMethod.DELETE);

        }

        #endregion


        #region (private) GetStoreCertificates(Request) / PostStoreCertificate(Request)

        /// <summary>
        /// GET /api/v1/certificates: everything in this local controller's store.
        /// </summary>
        /// <remarks>
        /// At the reading permission: what certificates a local controller
        /// holds is not a secret from anybody who may look at it at all, and
        /// the private keys are not in the answer. Changing any of it needs
        /// "certificates:edit", which only the administrators have unless the
        /// configuration file says otherwise.
        /// </remarks>
        private Task<HTTPResponse> GetStoreCertificates(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(NodeResources.Certificates), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.CertificatesJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/certificates with {"kind", "content", "password", "label",
        /// "usages"}: put a certificate into the store.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The file arrives as base64 in <c>content</c>, which is what an
        /// upload from the browser turns into. The password is what opens it if
        /// it is a protected PKCS#12, is used once here, and is not kept: the
        /// store writes what it holds without one.
        /// </para>
        /// <para>
        /// Answered with 200 rather than 201 when the certificate was already
        /// there. Importing the same file twice is the same entry - the id is
        /// its fingerprint - so the second import created nothing.
        /// </para>
        /// <para>
        /// <c>usages</c> says what a TLS root or a server certificate is for -
        /// ["dns", "nts"] - and is left out, or null, for every use.
        /// </para>
        /// </remarks>
        private Task<HTTPResponse> PostStoreCertificate(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(NodeResources.Certificates), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!CertificateKindExtensions.TryParseKind(json.Value<String>("kind"), out var kind) ||
                !Controller.Certificates.Kinds.Contains(kind))
            {
                return Task.FromResult(
                           ErrorJSON(Request, HTTPStatusCode.BadRequest,
                                     "'kind' has to be one of " +
                                     String.Join(", ", Controller.Certificates.Kinds.Select(one => one.AsText())) + ".")
                       );
            }

            var content = json.Value<String>("content")?.Trim();

            if (content is null or { Length: 0 })
                return Task.FromResult(
                           ErrorJSON(Request, HTTPStatusCode.BadRequest,
                                     "'content' has to be the certificate file, base64-encoded.")
                       );

            Byte[] bytes;

            try
            {
                bytes = Convert.FromBase64String(content);
            }
            catch (FormatException)
            {
                return Task.FromResult(
                           ErrorJSON(Request, HTTPStatusCode.BadRequest, "'content' is not valid base64.")
                       );
            }

            if (!TryReadUsages(json, out var usages, out var usagesError))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, usagesError));

            var existed = Controller.Certificates.Entries.Count;

            if (!Controller.Certificates.Import(bytes,
                                                kind,
                                                json.Value<String>("password"),
                                                json.Value<String>("label"),
                                                usages,
                                                out var entry,
                                                out var error))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));
            }

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           Controller.Certificates.Entries.Count > existed
                               ? HTTPStatusCode.Created
                               : HTTPStatusCode.OK,
                           entry.ToJSON(WithDiagnostics: true)
                       )
                   );

        }

        #endregion

        #region (private) GetStoreCertificate(Request) / PatchStoreCertificate(Request) / DeleteStoreCertificate(Request)

        /// <summary>
        /// GET /api/v1/certificates/{id}: one certificate.
        /// </summary>
        private Task<HTTPResponse> GetStoreCertificate(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(NodeResources.Certificates), false, out _, out var refused))
                return Task.FromResult(refused);

            var entry = Controller.Certificates.Get(HandleOf(Request));

            return Task.FromResult(
                       entry is null
                           ? ErrorJSON(Request, HTTPStatusCode.NotFound, "There is no such certificate in this store.")
                           : JSONResponse(Request, HTTPStatusCode.OK, entry.ToJSON(WithDiagnostics: true))
                   );

        }

        /// <summary>
        /// PATCH /api/v1/certificates/{id} with {"active"}, {"label"} and/or
        /// {"usages"}: switch a certificate on or off, rename it, or say what
        /// it is for.
        /// </summary>
        /// <remarks>
        /// Three things in one request because they are the only three things
        /// about a stored certificate that can be changed at all - everything
        /// else about it is read out of the file and is not somebody's to edit.
        /// "usages" set to null is every use again; left out, it is left alone.
        /// </remarks>
        private Task<HTTPResponse> PatchStoreCertificate(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(NodeResources.Certificates), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            var handle = HandleOf(Request);

            if (Controller.Certificates.Get(handle) is null)
                return Task.FromResult(
                           ErrorJSON(Request, HTTPStatusCode.NotFound, "There is no such certificate in this store.")
                       );

            if (json.TryGetValue("label", out var label) && label.Type != JTokenType.Undefined)
            {
                if (!Controller.Certificates.Relabel(handle, label.Value<String>(), out _, out var relabelError))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, relabelError));
            }

            if (json.TryGetValue("active", out var active))
            {

                if (active.Type != JTokenType.Boolean)
                    return Task.FromResult(
                               ErrorJSON(Request, HTTPStatusCode.BadRequest, "'active' has to be true or false.")
                           );

                if (!Controller.Certificates.SetActive(handle, active.Value<Boolean>(), out _, out var activeError))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, activeError));

            }

            // Present, even as null, is something to set: null is every use.
            if (json.ContainsKey("usages"))
            {

                if (!TryReadUsages(json, out var usages, out var usagesError))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, usagesError));

                if (!Controller.Certificates.SetUsages(handle, usages, out _, out var usagesRefused))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, usagesRefused));

            }

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK,
                                    Controller.Certificates.Get(handle)!.ToJSON(WithDiagnostics: true))
                   );

        }

        /// <summary>
        /// DELETE /api/v1/certificates/{id}: take a certificate out of the
        /// store and delete its file.
        /// </summary>
        private Task<HTTPResponse> DeleteStoreCertificate(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(NodeResources.Certificates), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!Controller.Certificates.Remove(HandleOf(Request), out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.NotFound, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.CertificatesJSON())
                   );

        }

        #endregion

        #region (private) PostStoreCertificateReload(Request)

        /// <summary>
        /// POST /api/v1/certificates/reload: read the store directory again.
        /// </summary>
        /// <remarks>
        /// What the store does at every start, on demand: certificates somebody
        /// copied into the directory are adopted, and entries whose files are
        /// gone are dropped. It exists because putting a file in a directory is
        /// a perfectly good way to install a certificate on a machine somebody
        /// already has a shell on, and having to restart the local controller
        /// to be noticed would make it a worse one.
        /// </remarks>
        private Task<HTTPResponse> PostStoreCertificateReload(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(NodeResources.Certificates), true, out _, out var refused))
                return Task.FromResult(refused);

            Controller.Certificates.Reload();

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.CertificatesJSON())
                   );

        }

        #endregion


        #region (private static) TryReadUsages(JSON, out Usages, out Error)

        /// <summary>
        /// The "usages" of a request: absent or null for every use, or a list
        /// of usages - or why not.
        /// </summary>
        /// <remarks>
        /// Whether each of them is a usage the store knows is the store's to
        /// say, and it says so in a sentence that names the ones it knows.
        /// </remarks>
        private static Boolean TryReadUsages(JObject                           JSON,
                                             out IReadOnlyList<String>?        Usages,
                                             [NotNullWhen(false)] out String?  Error)
        {

            Usages  = null;
            Error   = null;

            if (!JSON.TryGetValue("usages", out var token) || token.Type == JTokenType.Null)
                return true;

            if (token is not JArray array || array.Any(usage => usage.Type != JTokenType.String))
            {
                Error = "'usages' has to be a list of usages, such as [\"dns\", \"nts\"], or null for every use.";
                return false;
            }

            Usages = [.. array.Select(usage => usage.Value<String>()!)];
            return true;

        }

        #endregion

        #region (private static) HandleOf(Request)

        /// <summary>
        /// The certificate handle out of the request's path.
        /// </summary>
        private static String HandleOf(HTTPRequest Request)

            => Request.ParsedURLParameters.Length > 0
                   ? Request.ParsedURLParameters[0].Trim()
                   : "";

        #endregion

    }

}
