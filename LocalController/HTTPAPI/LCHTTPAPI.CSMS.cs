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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The part of the JSON API that is about the charging station management
    /// system above this local controller.
    /// </summary>
    public partial class LCHTTPAPI
    {

        #region (private) RegisterCSMSRoutes()

        /// <summary>
        /// Everything under /v1/configuration/csms.
        /// </summary>
        private void RegisterCSMSRoutes()
        {

            var root = HTTPPath.Root + "v1/configuration/csms";

            AddHandler(root,                GetCSMS,              HTTPMethod.GET);
            AddHandler(root,                PutCSMS,              HTTPMethod.PUT);

            AddHandler(root + "credentials", PutCSMSCredentials,   HTTPMethod.PUT);
            AddHandler(root + "credentials", DeleteCSMSCredentials, HTTPMethod.DELETE);

        }

        #endregion


        #region (private) GetCSMS(Request) / PutCSMS(Request)

        /// <summary>
        /// GET .../csms: where this controller reports to, and whether the line
        /// is up.
        /// </summary>
        private Task<HTTPResponse> GetCSMS(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.CSMSConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT .../csms: change it.
        /// </summary>
        /// <remarks>
        /// Answers with the whole thing as it now stands - including
        /// <c>state.waitingForARestart</c>, which is how the page finds out that
        /// the address it just changed is not the address that is connected. A
        /// WebSocket client cannot be re-aimed while it is dialled.
        /// </remarks>
        private Task<HTTPResponse> PutCSMS(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeStationSettings, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Controller.TryUpdateCSMSConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            Log.Info($"'{session.UserId}' changed the CSMS connection.", "ocpp", "csms", "config", "web");

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.CSMSConfigurationJSON())
                   );

        }

        #endregion

        #region (private) PutCSMSCredentials(Request) / DeleteCSMSCredentials(Request)

        /// <summary>
        /// PUT .../csms/credentials with {"username", "password"} or
        /// {"username", "sharedSecret", ...}: what this controller signs in
        /// with.
        /// </summary>
        /// <remarks>
        /// Neither is made up here, unlike the passwords of the charging
        /// stations below: both are issued by whoever runs the CSMS, and typed
        /// in. Nothing comes back but the list as it now stands - the secret
        /// went the other way.
        /// </remarks>
        private Task<HTTPResponse> PutCSMSCredentials(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeStationSettings, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            var username      = json.Value<String>("username");
            var password      = json.Value<String>("password");
            var sharedSecret  = json.Value<String>("sharedSecret");

            if (password is null && sharedSecret is null)
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest,
                                                 "Either a 'password' or a 'sharedSecret' is required."));

            #region A one-time token

            if (sharedSecret is not null)
            {

                TOTPHashAlgorithm? hash = null;

                if (json.Value<String>("hashAlgorithm")?.Trim() is { Length: > 0 } hashText)
                {

                    hash = hashText.ToUpperInvariant() switch {
                               "SHA256"  => TOTPHashAlgorithm.SHA256,
                               "SHA384"  => TOTPHashAlgorithm.SHA384,
                               "SHA512"  => TOTPHashAlgorithm.SHA512,
                               _         => null
                           };

                    if (hash is null)
                        return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest,
                                                         $"'{hashText}' is not a TOTP hash algorithm; there are SHA256, SHA384 and SHA512."));

                }

                var seconds = json.Value<Double?>("validitySeconds");

                if (!Controller.CSMSLogin.TrySetTOTP(
                         username,
                         sharedSecret,
                         seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : null,
                         json.Value<UInt32?>("length"),
                         json.Value<String>("alphabet"),
                         hash,
                         out var totpError))
                {
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, totpError));
                }

            }

            #endregion

            #region A password

            if (password is not null &&
                !Controller.CSMSLogin.TrySetPassword(username, password, out var passwordError))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, passwordError));
            }

            #endregion

            Log.Notice($"'{session.UserId}' set the credentials this local controller signs in to the CSMS with.",
                       "ocpp", "csms", "auth", "web");

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.CSMSConfigurationJSON())
                   );

        }

        /// <summary>
        /// DELETE .../csms/credentials: forget them.
        /// </summary>
        private Task<HTTPResponse> DeleteCSMSCredentials(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeStationSettings, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!Controller.CSMSLogin.TryClear(out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            Log.Notice($"'{session.UserId}' removed the credentials this local controller signs in to the CSMS with.",
                       "ocpp", "csms", "auth", "web");

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.CSMSConfigurationJSON())
                   );

        }

        #endregion

    }

}
