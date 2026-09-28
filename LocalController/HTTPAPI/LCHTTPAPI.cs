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

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Logging;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The JSON API the browser talks to, registered at "/api": what every
    /// node has - see <see cref="NodeHTTPAPI"/> - and what only a local
    /// controller has, its CSMS and the charging stations below it.
    /// </summary>
    /// <remarks>
    /// The sign-in, the configuration, name resolution and the time servers,
    /// the certificate store, the log and the event stream are the node's, as
    /// they are the vehicle's and the charging station's; this class used to
    /// have its own copy of all of them. What is left here is registered on
    /// top: see LCHTTPAPI.CSMS.cs and LCHTTPAPI.OCPPServer.cs.
    /// </remarks>
    public partial class LCHTTPAPI : NodeHTTPAPI
    {

        #region Properties

        /// <summary>
        /// The local controller this API speaks for.
        /// </summary>
        public LocalController  Controller  { get; }

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
                   Controller,
                   ExtAPI,
                   Log,
                   APIPath,
                   Version ?? typeof(LCHTTPAPI).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")

        {

            this.Controller = Controller;

            // The charging station server, its certificates, the chains it
            // accepts and the stations that may sign in; see
            // LCHTTPAPI.OCPPServer.cs. And the CSMS above; see LCHTTPAPI.CSMS.cs.
            RegisterOCPPServerRoutes();
            RegisterCSMSRoutes();

        }

        #endregion


        #region (protected override) ProductStatus()

        /// <summary>
        /// Who this local controller is towards its CSMS and its stations.
        /// </summary>
        protected override IEnumerable<JProperty> ProductStatus()
        {
            yield return new JProperty("ocppId", Controller.Node.Id.ToString());
        }

        #endregion

    }

}
