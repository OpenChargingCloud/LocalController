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

using org.GraphDefined.Vanaheimr.Hermod.Messaging;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.protocols.OCPP.Node.KeyPins;

using OCPPv2_1    = cloud.charging.open.protocols.OCPPv2_1;
using OCPPv2_1_LC = cloud.charging.open.protocols.OCPPv2_1.LocalController;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// What the CSMS sees and writes of how this controller believes the
    /// servers of its connections - TLSClientController of its device
    /// model, an instance for every wss:// connection and one for its own
    /// key - and what a connection on probation keeps from the stations
    /// behind this controller.
    /// </summary>
    /// <remarks>
    /// The device model, the pin sets and the probation are those of every
    /// node that dials a CSMS (OCPPDiallingNode.TLSClient.cs and
    /// OCPPDiallingNode.KeyPins.cs). What is a local controller's own is
    /// that it forwards: a CSMS it believes by a key pin it only learned
    /// reaches no station through it until a signed pin set confirms the
    /// pin.
    /// </remarks>
    public partial class LocalController
    {

        #region (private) AttachUplinkDeviceModel(Node)

        /// <summary>
        /// What the node answers of the TLS client of this controller, and
        /// who may write a pin set.
        /// </summary>
        private void AttachUplinkDeviceModel(OCPPv2_1_LC.TestLocalControllerNode Node)
        {

            AttachTLSClient(Node);

            // A pin set is held to the signers of pin sets by this controller
            // itself, where its TLSClientController takes it: the signature
            // policy leaves the two variables of one to it.
            Node.OCPP.SignaturePolicy.AddPolicyEntries(
                new[] { "ServerKeyPins", "ServerKeyPinsVersion" }.Select(variable => new MessagePolicyEntry(
                    Id:           $"pin set TLSClientController.{variable}",
                    Action:       "SetVariables",
                    Require:      SignerRequirement.Unsigned,
                    Direction:    "CSMS->CS",
                    Component:    nameof(OCPPv2_1.TLSClientController),
                    Variable:     variable,
                    Description:  "A pin set of a connection up to the CSMS: held to the signers of pin sets by the local controller itself"
                ))
            );

        }

        #endregion

        #region (internal) NameOfTheLine(Source)

        /// <summary>
        /// What a node a request came from is called where it is said to
        /// somebody: a line up to the CSMS, which the routing of the node
        /// knows as "connection-" and the id of its connection, by the
        /// description of that connection; every other node by its name.
        /// </summary>
        internal String NameOfTheLine(NetworkingNode_Id Source)
        {

            const String prefix = "connection-";

            var name = Source.ToString();

            return name.StartsWith(prefix, StringComparison.Ordinal) &&
                   Connections.Connections.FirstOrDefault(connection => connection.Id == name[prefix.Length..]) is { } line
                       ? line.Description
                       : name;

        }

        #endregion

        #region (private) WhyNotForwardOnProbation(Connection, Action)

        /// <summary>
        /// Why a request that came over the given connection is not passed
        /// on to a station, where the connection is on probation; null
        /// where it is not.
        /// </summary>
        private String? WhyNotForwardOnProbation(IMessageConnection  Connection,
                                                 String              Action)

            => probations.Gate(Connection, Action, null, (probation, action, payload) =>
                   $"This local controller believes its CSMS by the key pin {(probation.Trust as ServerKeyPinTrust)?.Learned}, which it learned on its first " +
                   $"connection and which is on probation: until a signed pin set confirms it, nothing it sends is passed on to a station ('{action}').");

        #endregion

    }

}
