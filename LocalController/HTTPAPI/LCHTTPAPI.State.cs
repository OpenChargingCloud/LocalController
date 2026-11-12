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

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The part of the JSON API that says what is going on, apart from how
    /// this local controller is configured.
    /// </summary>
    public partial class LCHTTPAPI
    {

        #region (private) RegisterStateRoutes()

        /// <summary>
        /// /v1/state.
        /// </summary>
        private void RegisterStateRoutes()
        {
            AddHandler(HTTPPath.Root + "v1/state", GetState, HTTPMethod.GET);
        }

        #endregion

        #region (private) GetState(Request)

        /// <summary>
        /// GET /api/v1/state: whether the line up to the CSMS stands and since
        /// when, which charging stations were seen when, and the limits in
        /// force - with "state:read", which the support has and which reads
        /// none of the configuration.
        /// </summary>
        private Task<HTTPResponse> GetState(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(NodeResources.State), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.StateJSON())
                   );

        }

        #endregion

    }

}
