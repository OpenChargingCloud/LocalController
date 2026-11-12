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

using cloud.charging.open.protocols.OCPP.Node;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// What OCPP shows of how this local controller resolves names and keeps its
    /// time: the components a charging station has for them, in the controller's
    /// own device model, read from this node at every read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same bridge a charging station has (WWCP_OCPP_Node's DeviceModelBridge):
    /// DNSClientController with a DNSServerController for each name server,
    /// ClockCtrlr, NTPController, an NTPClientGroupController for each group of
    /// time servers and an NTPClientController for each server, and the roots of
    /// both. A CSMS changes the name servers as the page does, and only after the
    /// name of its own line was resolved with the change.
    /// </para>
    /// <para>
    /// Who may change and read which of them is the signature policy's to decide,
    /// entry by entry: "dns" and "nts" (LocalController.SignaturePolicy.cs).
    /// </para>
    /// <para>
    /// OCPP 2.1 only: this controller speaks no OCPP 1.6 up to its CSMS, so the
    /// configuration keys of 1.6 the bridge has are not given to any node.
    /// </para>
    /// </remarks>
    public partial class LocalController
    {

        #region Data

        private readonly DeviceModelBridge deviceModelBridge;

        #endregion

        #region (internal) DeviceModelBridge

        /// <summary>
        /// What OCPP reads and changes of this controller's name servers and time
        /// servers, for a test that says what the node said.
        /// </summary>
        internal DeviceModelBridge DeviceModelBridge
            => deviceModelBridge;

        #endregion


        #region (private) NewDeviceModelBridge()

        /// <summary>
        /// The bridge between this node's name servers and time servers and the
        /// device model of its OCPP node.
        /// </summary>
        private DeviceModelBridge NewDeviceModelBridge()

            => new (this,
                    CSMSNames,
                    DialsOCPP2_1: () => csmsSettings.URL is { Length: > 0 },
                    DialsOCPP1_6: () => false);

        #endregion

        #region (private) CSMSNames()

        /// <summary>
        /// The name the line up to the CSMS is dialled by - none where it is
        /// dialled by an address, or where there is no CSMS: what a change of the
        /// name servers through OCPP must keep resolving.
        /// </summary>
        private IEnumerable<String> CSMSNames()
        {

            if (csmsSettings.URL is not { Length: > 0 } url ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return [];

            var host = uri.Host.Trim('[', ']');

            return host.Length == 0 || System.Net.IPAddress.TryParse(host, out _)
                       ? []
                       : [ host ];

        }

        #endregion

    }

}
