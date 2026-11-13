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

using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.OCPP.Node.KeyPins;

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



        }

        #endregion


        #region (private) GetCSMS(Request) / PutCSMS(Request)

        /// <summary>
        /// GET .../csms: where this controller reports to, and whether the line
        /// is up.
        /// </summary>
        private Task<HTTPResponse> GetCSMS(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(LocalControllerAccess.CSMS), false, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Edit(LocalControllerAccess.CSMS), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Controller.TryUpdateCSMSConfiguration(json, out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

            Log.Info($"'{user.Id}' changed the CSMS connection.", "ocpp", "csms", "config", "web");

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.CSMSConfigurationJSON())
                   );

        }

        #endregion

    }

}
