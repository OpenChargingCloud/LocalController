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

using System.Collections.Concurrent;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.OCPP.SSH;

using OCPPWebSockets = cloud.charging.open.protocols.OCPP.WebSockets;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// What is going on, apart from how this local controller is configured:
    /// whether the line up to the CSMS stands and since when, which charging
    /// stations were seen when, and the grid operator's limits in force
    /// (white paper "Signed OCPP Messages and Signer Roles", section 5.3, Q12:
    /// the resource "state").
    /// </summary>
    /// <remarks>
    /// Apart, because somebody who is to see that a station has gone quiet is
    /// not thereby somebody who is to read where this controller dials, whom
    /// it lets in and what it signs in with: the support reads this and none
    /// of the configuration. Nothing here says an address, a login or a
    /// secret, and no sentence of a problem either, which may name them.
    /// </remarks>
    public partial class LocalController
    {

        #region Data

        /// <summary>
        /// A charging station as this controller last saw it: when, since when
        /// it is connected, and over what.
        /// </summary>
        private sealed record StationSeen(DateTimeOffset   LastSeen,
                                          DateTimeOffset?  ConnectedSince,
                                          String           Over);

        /// <summary>
        /// The charging stations seen since this controller started, by their
        /// identity as it let them in.
        /// </summary>
        private readonly ConcurrentDictionary<String, StationSeen> stationsSeen = new (StringComparer.Ordinal);

        private const String OverWebSocket  = "WebSocket";
        private const String OverSSH        = "SSH";

        #endregion


        #region (private) Seen(StationId, Over, Connecting)

        /// <summary>
        /// A charging station was seen now - connecting, or saying something.
        /// </summary>
        private void Seen(String?  StationId,
                          String   Over,
                          Boolean  Connecting)
        {

            if (StationId is null)
                return;

            var now = TimeProvider.GetUtcNow();

            stationsSeen.AddOrUpdate(StationId,
                                     _    => new StationSeen(now, Connecting ? now : null, Over),
                                     (_, seen) => new StationSeen(now, Connecting ? now : seen.ConnectedSince, Over));

        }

        #endregion

        #region (private) WireStationsSeen(Server)

        /// <summary>
        /// Note the charging stations of the station port as they connect and
        /// as they speak: only those this controller let in, by the identity
        /// it let them in as.
        /// </summary>
        private void WireStationsSeen(OCPPWebSockets.OCPPWebSocketServer Server)
        {

            Server.OnNewWebSocketConnection += (timestamp, server, connection, sharedSubprotocols, selectedSubprotocol, eventTrackingId, cancellationToken) => {
                Seen(LetInAs(connection), OverWebSocket, Connecting: true);
                return Task.CompletedTask;
            };

            Server.OnJSONMessageReceived += (timestamp, server, connection, messageTimestamp, eventTrackingId, sourceNodeId, message, cancellationToken) => {
                Seen(LetInAs(connection), OverWebSocket, Connecting: false);
                return Task.CompletedTask;
            };

            Server.OnPingMessageReceived += (timestamp, server, connection, frame, eventTrackingId, pingMessage, cancellationToken) => {
                Seen(LetInAs(connection), OverWebSocket, Connecting: false);
                return Task.CompletedTask;
            };

        }

        /// <summary>
        /// Note the charging stations that come over SSH, as the networking
        /// node their HELLO was welcomed as.
        /// </summary>
        private void WireStationsSeen(OCPPSshServer Server)
        {

            Server.OnNetworkingNodeConnectionAccepted += (timestamp, transport, connection, networkingNodeId, networkingMode, eventTrackingId, cancellationToken) => {
                Seen(networkingNodeId?.ToString(), OverSSH, Connecting: true);
                return Task.CompletedTask;
            };

            Server.OnJSONMessageReceived += (timestamp, transport, connection, messageTimestamp, eventTrackingId, sourceNodeId, message, cancellationToken) => {
                Seen(connection.NetworkingNodeIdOf()?.ToString(), OverSSH, Connecting: false);
                return Task.CompletedTask;
            };

        }

        /// <summary>
        /// The charging station a connection of the station port was let in as,
        /// or null for one that was not.
        /// </summary>
        private static String? LetInAs(WebSocketServerConnection Connection)

            => Connection.TryGetCustomData(StationIdKey) as String;

        #endregion

        #region StateJSON()

        /// <summary>
        /// What is going on, as the web interface reads it with "state:read".
        /// </summary>
        public JObject StateJSON()
        {

            var now        = TimeProvider.GetUtcNow();

            var connected  = new Dictionary<String, String>(StringComparer.Ordinal);

            foreach (var connection in ocppWebSocketServer?.WebSocketConnections ?? [])
                if (LetInAs(connection) is String station)
                    connected[station] = OverWebSocket;

            foreach (var networkingNodeId in ocppSshServer?.ConnectedNetworkingNodeIds ?? [])
                connected[networkingNodeId.ToString()] = OverSSH;

            var stations   = stationsSeen.
                                 OrderBy(seen => seen.Key, StringComparer.Ordinal).
                                 Select (seen => new JObject(
                                                     new JProperty("id",              seen.Key),
                                                     new JProperty("connected",       connected.ContainsKey(seen.Key)),
                                                     new JProperty("over",            connected.TryGetValue(seen.Key, out var over) ? over : null),
                                                     new JProperty("connectedSince",  connected.ContainsKey(seen.Key) ? seen.Value.ConnectedSince?.ToString("o") : null),
                                                     new JProperty("lastSeen",        seen.Value.LastSeen.ToString("o"))
                                                 ));

            return new JObject(

                new JProperty("csms",          new JObject(
                    new JProperty("enabled",          CSMSEnabled),
                    new JProperty("connected",        CSMSConnected),
                    new JProperty("connectedSince",   CSMSConnectedSince?.ToString("o"))
                )),

                new JProperty("stations",      new JObject(
                    new JProperty("connected",        connected.Count),
                    new JProperty("seen",             new JArray(stations))
                )),

                new JProperty("openADR",       new JObject(
                    new JProperty("running",          venLoop is not null),
                    new JProperty("signedIn",         venSignedIn),
                    new JProperty("lastPoll",         lastPollAt?.ToString("o")),
                    new JProperty("lastRead",         lastReadAt?.ToString("o")),
                    new JProperty("importKW",         ImportLimitNow?.KW),
                    new JProperty("exportKW",         ExportLimitNow?.KW),
                    new JProperty("limits",           new JArray(GridLimits.Select(limit => limit.ToJSON(now))))
                )),

                new JProperty("timestamp",     now.ToString("o"))

            );

        }

        #endregion

    }

}
