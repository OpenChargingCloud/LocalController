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
using org.GraphDefined.Vanaheimr.Hermod.Messaging;

using cloud.charging.open.protocols.OCPPv2_1.NetworkingNode;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// A charging station's Authorize request, sent on to the CSMS above this
    /// local controller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which cards may charge is the operator's to say, and the operator is at
    /// the CSMS: this controller has no tokens of its own and decides on none.
    /// It forwards the question, and the answer finds its way back down to the
    /// station that asked.
    /// </para>
    /// <para>
    /// The OCPP node forwards an Authorize only for the stations on a list of
    /// its own, which nothing in this controller fills - so every card of
    /// every station was answered "Unauthorized!" here and never reached the
    /// CSMS. Who may ask is decided at the door instead: a station that got as
    /// far as sending a request signed in at this controller's station port or
    /// over SSH, with a login this controller knows.
    /// </para>
    /// <para>
    /// Where the line to the CSMS is down there is nobody to send it to, and
    /// the station is told so at once rather than left to wait for an answer
    /// that cannot come.
    /// </para>
    /// </remarks>
    public partial class LocalController
    {

        #region (private) BuildAuthorization()

        /// <summary>
        /// Have Authorize requests of the stations forwarded to the CSMS - and
        /// not decided by the node's own list of stations.
        /// </summary>
        private void BuildAuthorization()
        {
            lc01.AuthorizeRequestFilter = ForwardAuthorize;
        }

        #endregion

        #region (private) ForwardAuthorize(Timestamp, Sender, Connection, Request, CancellationToken)

        /// <summary>
        /// A charging station's Authorize request: forwarded, where the line to
        /// the CSMS is up.
        /// </summary>
        private Task<RequestForwardingDecision<OCPPv2_1.CS.AuthorizeRequest, OCPPv2_1.CSMS.AuthorizeResponse>>

            ForwardAuthorize(DateTimeOffset               Timestamp,
                             IEventSender                 Sender,
                             IMessageConnection           Connection,
                             OCPPv2_1.CS.AuthorizeRequest  Request,
                             CancellationToken            CancellationToken)

        {

            var station = Request.NetworkPath.Source.ToString();

            if (!csmsConnected)
            {

                var why = CSMSEnabled
                              ? "the line to the CSMS is down"
                              : "this local controller reports to no CSMS";

                Log.Warning($"The Authorize of '{station}' was not sent on: {why}, and this controller decides on no card itself.", "ocpp", "authorization", "forward");

                return Task.FromResult(
                           RequestForwardingDecision<OCPPv2_1.CS.AuthorizeRequest, OCPPv2_1.CSMS.AuthorizeResponse>.REJECT(
                               Request,
                               $"No CSMS to ask: {why}."
                           )
                       );

            }

            Log.Info($"The Authorize of '{station}' is sent on to the CSMS.", "ocpp", "authorization", "forward");

            return Task.FromResult(
                       RequestForwardingDecision<OCPPv2_1.CS.AuthorizeRequest, OCPPv2_1.CSMS.AuthorizeResponse>.FORWARD(Request)
                   );

        }

        #endregion

    }

}
