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

using System.Collections.Frozen;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.Messaging;

using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.OCPP.NetworkingNode;
using cloud.charging.open.protocols.OCPPv2_1.NetworkingNode;

using OCPPv2_1    = cloud.charging.open.protocols.OCPPv2_1;
using OCPPv2_1_LC = cloud.charging.open.protocols.OCPPv2_1.LocalController;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// What only the CSMS asks of this local controller, it asks over the controller's own line up to it: a
    /// request of that kind addressed to this controller itself that comes over a connection the controller
    /// accepted - a charging station's, on the station port or over SSH - is refused at the door.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A request in the frame of the overlay network names its destination and where it comes from as its
    /// sender wrote them: a station on the station port can address this controller and call itself the
    /// CSMS. Without this gate it could install an SSH key for another station's identity and sign in as
    /// that station next (InstallSSHKey), lock another station out (DeleteSSHKey), switch the key every
    /// station of the site pins (SetVariables of EVQICtrlr[TLSServer].ActiveKeyPin), reset the controller,
    /// or read its Device Model and its stations' keys.
    /// </para>
    /// <para>
    /// Decided by the connection, which the request does not carry: so here, at the door, and not in each
    /// of the handlers. Not by the signer's rights either, which signed requests to the controller will be
    /// held to: this says only that a station is not the CSMS. What a station sends on to the CSMS, and
    /// what a station may ask of a controller - a DataTransfer, say - passes as before.
    /// </para>
    /// </remarks>
    public partial class LocalController
    {

        #region (static) CSMSOnlyActions

        /// <summary>
        /// The actions only a CSMS asks of a node it manages: every request of OCPP 2.1 that a CSMS sends -
        /// the requests of the namespace OCPPv2_1.CSMS, Reset, SetVariables, InstallSSHKey and all the
        /// others, also those added later - and the networking-node requests that manage a node: its
        /// signature policies, its user roles and its files.
        /// </summary>
        /// <remarks>
        /// Found by the request types rather than listed, so that a request the library adds for the CSMS
        /// is held back here without anyone having to remember it. The few requests of that namespace a
        /// station sends - NotifyDERAlarm, say - are no loss: a station sends them to the CSMS, not to this
        /// controller.
        /// </remarks>
        public static IReadOnlySet<String> CSMSOnlyActions { get; }

            = CSMSRequestTypes().
                  Concat([
                      typeof(OCPPv2_1.NetworkingNode.AddSignaturePolicyRequest),
                      typeof(OCPPv2_1.NetworkingNode.UpdateSignaturePolicyRequest),
                      typeof(OCPPv2_1.NetworkingNode.DeleteSignaturePolicyRequest),
                      typeof(OCPPv2_1.NetworkingNode.AddUserRoleRequest),
                      typeof(OCPPv2_1.NetworkingNode.UpdateUserRoleRequest),
                      typeof(OCPPv2_1.NetworkingNode.DeleteUserRoleRequest),
                      typeof(OCPPv2_1.NetworkingNode.GetFileRequest),
                      typeof(OCPPv2_1.NetworkingNode.SendFileRequest),
                      typeof(OCPPv2_1.NetworkingNode.DeleteFileRequest),
                      typeof(OCPPv2_1.NetworkingNode.ListDirectoryRequest)
                  ]).
                  Select(type => type.Name[..^"Request".Length]).
                  ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The request types of OCPP 2.1 a CSMS sends.
        /// </summary>
        private static IEnumerable<Type> CSMSRequestTypes()
        {

            var csms = typeof(OCPPv2_1.CSMS.ResetRequest);

            return csms.Assembly.GetTypes().
                       Where(type => type.Namespace == csms.Namespace         &&
                                     type.IsClass && !type.IsAbstract         &&
                                     type.Name.EndsWith("Request", StringComparison.Ordinal) &&
                                     IsARequest(type));

        }

        private static Boolean IsARequest(Type Type)
        {

            for (var baseType = Type.BaseType; baseType is not null; baseType = baseType.BaseType)
                if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(OCPPv2_1.ARequest<>))
                    return true;

            return false;

        }

        #endregion

        #region (private) AttachRequestGate(Node)

        /// <summary>
        /// Give the node its gate: what only the CSMS asks comes over the controller's line up to it.
        /// </summary>
        private static void AttachRequestGate(OCPPv2_1_LC.TestLocalControllerNode Node)
        {

            // The node's only gate: nothing else of this controller sets one.
            Node.OCPP.IN.RequestGate = OnlyTheCSMS;

        }

        #endregion

        #region (private) AttachForwardingGate(Node)

        /// <summary>
        /// Give the node's forwarding its gate: what a charging station sends on goes up to the CSMS, under
        /// the station's own name, and to no other station.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A frame in the overlay network names where it goes and who sent it, as its sender wrote them.
        /// Forwarded as written, a station could send another station a Reset, a SetVariables or a
        /// RequestStartTransaction and call itself the CSMS, and the other station would take it for one of
        /// its CSMS's - or send the CSMS a TransactionEvent in another station's name.
        /// </para>
        /// <para>
        /// So, for what comes over a connection this controller accepted: a request, or a message, whose
        /// sender is not the station of that connection is dropped - not refused, as a refusal would go to
        /// the sender it names, which did not send it. One whose destination is reached over this
        /// controller's own servers, another station, is refused, and the station is told so. What goes up
        /// over the controller's line to the CSMS, and everything that comes down it, passes as before.
        /// </para>
        /// <para>
        /// A station is the one end of its path: a networking node behind a station, which would send on
        /// in the names of the nodes behind it, is not something a station of this controller is.
        /// </para>
        /// </remarks>
        private void AttachForwardingGate(OCPPv2_1_LC.TestLocalControllerNode Node)
        {

            Node.OCPP.FORWARD.OnAnyJSONRequestFilter       += (timestamp, sender, connection, request, cancellationToken) =>
                Task.FromResult(AsRequestDecision(WhyNotForward(Node, connection, request.Action, request.Destination, request.NetworkPath)));

            Node.OCPP.FORWARD.OnAnyBinaryRequestFilter     += (timestamp, sender, connection, request, cancellationToken) =>
                Task.FromResult(AsRequestDecision(WhyNotForward(Node, connection, request.Action, request.Destination, request.NetworkPath)));

            Node.OCPP.FORWARD.OnAnyJSONSendMessageFilter   += (timestamp, sender, connection, message, cancellationToken) =>
                Task.FromResult(AsMessageDecision(WhyNotForward(Node, connection, message.Action, message.Destination, message.NetworkPath)));

            Node.OCPP.FORWARD.OnAnyBinarySendMessageFilter += (timestamp, sender, connection, message, cancellationToken) =>
                Task.FromResult(AsMessageDecision(WhyNotForward(Node, connection, message.Action, message.Destination, message.NetworkPath)));

        }

        #endregion

        #region (private) WhyNotForward(Node, Connection, Action, Destination, NetworkPath)

        /// <summary>
        /// What is wrong with forwarding what came over the given connection - a forged sender, or another
        /// station as the destination - or nothing.
        /// </summary>
        private (Boolean Forged, String Why)? WhyNotForward(OCPPv2_1_LC.TestLocalControllerNode  Node,
                                                           IMessageConnection                   Connection,
                                                           String                               Action,
                                                           SourceRouting                        Destination,
                                                           NetworkPath                          NetworkPath)
        {

            // Over the controller's line to the CSMS: the CSMS's to send.
            if (Connection.Role != ConnectionRole.Server)
                return null;

            var station = Connection.NetworkingNodeIdOf();

            if (station is null || NetworkPath.Source != station.Value)
            {

                var why = $"The charging station '{station}' sent '{Action}' for '{Destination.Next}' as '{NetworkPath.Source}': dropped, a station sends in its own name.";

                Log.Warning(why, "ocpp", "station", "security");

                return (true, why);

            }

            if (Node.Routing.LookupNetworkingNode(Destination.Next, out var reachability) &&
                reachability.Client is null)
            {

                var why = $"'{Action}' from the charging station '{station}' is not sent on to '{Destination.Next}': a charging station's requests go to the CSMS, not to another station of this local controller.";

                Log.Warning(why, "ocpp", "station", "security");

                return (false, why);

            }

            return null;

        }

        private static RequestForwardingDecision AsRequestDecision((Boolean Forged, String Why)? Refusal)

            => Refusal switch {
                   null                     => new RequestForwardingDecision(ForwardingDecisions.NEXT),
                   { Forged: true  } forged => new RequestForwardingDecision(ForwardingDecisions.DROP,   LogMessage:    forged.Why),
                   { } refused              => new RequestForwardingDecision(ForwardingDecisions.REJECT, RejectMessage: refused.Why)
               };

        private static MessageForwardingDecision AsMessageDecision((Boolean Forged, String Why)? Refusal)

            // A message is not answered, so not refused either: dropped.
            => Refusal is { } refusal
                   ? new MessageForwardingDecision(ForwardingDecisions.DROP, LogMessage: refusal.Why)
                   : new MessageForwardingDecision(ForwardingDecisions.NEXT);

        #endregion

        #region (private static) OnlyTheCSMS(Connection, Action, Payload)

        /// <summary>
        /// Why a request addressed to this controller itself is not processed: where it is one of the
        /// <see cref="CSMSOnlyActions"/> and came over a connection this controller accepted rather than
        /// over the one it dialled - or null.
        /// </summary>
        private static String? OnlyTheCSMS(IMessageConnection  Connection,
                                           String              Action,
                                           JObject?            Payload)

            => Connection.Role == ConnectionRole.Server &&
               CSMSOnlyActions.Contains(Action)

                   ? $"'{Action}' is for this local controller's CSMS to ask, over the controller's own connection to it - not for a charging station connected to the controller."
                   : null;

        #endregion

    }

}
