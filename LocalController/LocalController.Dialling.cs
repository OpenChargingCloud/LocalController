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

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.protocols.OCPP.Node.Connections;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// What a local controller says of itself to the dialling it shares with
    /// every node that dials a CSMS: that it is a local controller, that it
    /// speaks OCPP 2.1 upwards and no OCPP 1.6, and where its connections,
    /// its client certificates and its SSH key are kept.
    /// </summary>
    public partial class LocalController
    {

        #region (static) Constructor

        /// <summary>
        /// What the connections of this process say of the node they are
        /// the connections of: it is a local controller.
        /// </summary>
        static LocalController()
        {
            NodeWording.Is("local controller");
        }

        #endregion

        #region Node16 / Node21

        /// <summary>
        /// A local controller speaks no OCPP 1.6 upwards.
        /// </summary>
        protected override OCPPv1_6.NetworkingNode.AOCPPNetworkingNode? Node16
            => null;

        /// <summary>
        /// The node that speaks OCPP 2.1 for this controller.
        /// </summary>
        protected override OCPPv2_1.NetworkingNode.AOCPPNetworkingNode Node21
            => lc01;

        #endregion

        #region (private) BuildConnectionStores()

        /// <summary>
        /// The stores of what this controller dials - the connections and
        /// their credentials, the keys and certificates it holds up, the key
        /// it signs in with over SSH - beside the configuration file.
        /// </summary>
        private void BuildConnectionStores()
        {

            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(ConfigFile.Path)) ?? ".";

            ClientCertificates = new ClientCertificateStore(
                                     System.IO.Path.Combine(directory, ClientCertificateStore.DefaultDirectoryName),
                                     TimeProvider
                                 );

            ClientCertificates.OnNotice += (level, message) => Log.Log(level, message, "ocpp", "certificates");

            Connections = new ConnectionStore(
                              System.IO.Path.Combine(directory, ConnectionStore.DefaultDirectoryName),
                              TimeProvider
                          );

            Connections.KnownCertificateIds  = () => ClientCertificates.Entries.Select(entry => entry.Id);
            Connections.OnNotice            += (level, message) => Log.Log(level, message, "ocpp", "connections");

            SshKey = new SshClientKey(
                         System.IO.Path.Combine(Connections.Path, SshClientKey.DirectoryName),
                         $"localcontroller@{Environment.MachineName}"
                     );

            SshKey.OnNotice += (level, message) => Log.Log(level, message, "ocpp", "connections", "ssh", "security");

        }

        #endregion

        #region (protected override) FlushOfflineQueueOf(Version) / QueueSecurityEvent(Version, Type, TechInfo)

        /// <summary>
        /// Send what this controller kept for its CSMS while it was out of
        /// reach.
        /// </summary>
        protected override Task FlushOfflineQueueOf(OCPPVersion Version)

            => lc01.FlushOfflineQueueAsync();

        /// <summary>
        /// Tell the CSMS of a security event, now or once it is in reach.
        /// </summary>
        protected override void QueueSecurityEvent(OCPPVersion  Version,
                                                   String       Type,
                                                   String       TechInfo)
        {
            _ = lc01.QueueSecurityEventAsync(OCPPv2_1.SecurityEventType.Parse(Type), TechInfo);
        }

        #endregion

    }

}
