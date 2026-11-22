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

using org.GraphDefined.Vanaheimr.Illias;
using System.Net.WebSockets;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.Logins;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// One station port for every version of OCPP: the server of the node of
    /// OCPP 2.1 listens, and would hand a charging station that asks for OCPP
    /// 1.6 to the server of a node of OCPP 1.6. A local controller has none -
    /// it speaks OCPP 2.1 alone - so a station of OCPP 1.6 is turned away
    /// where the file offers it, and the log says why, rather than being let
    /// in by the node of OCPP 2.1 and read as one of its own.
    /// </summary>
    public class StationPortBySubprotocolTests : ALocalControllerTests
    {

        #region Data

        private const String ThePassword = "a-password-long-enough-for-ocpp";

        private readonly List<ClientWebSocket> clients = [];

        #endregion

        #region (override) Configuration

        /// <summary>
        /// A controller whose station port offers OCPP 2.1 and OCPP 1.6.
        /// </summary>
        protected override JObject Configuration

            => new (

                   new JProperty("nts",  new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),

                   new JProperty("webSocketServer", new JObject(
                       new JProperty("enabled",               true),
                       new JProperty("address",               "127.0.0.1"),
                       new JProperty("port",                  TestPorts.Free().ToUInt16()),
                       new JProperty("clientAuthentication",  new JArray("httpAuthWithoutTLS")),
                       new JProperty("subprotocols",          new JArray("ocpp2.1", "ocpp1.6"))
                   ))

               );

        #endregion

        #region TearDown

        [TearDown]
        public void LetTheClientsGo()
        {

            foreach (var client in clients)
                client.Dispose();

            clients.Clear();

        }

        #endregion


        #region (private) Connect(Id, Password, Subprotocol)

        /// <summary>
        /// One charging station getting in, and staying connected.
        /// </summary>
        private async Task<(Boolean Connected, String? Subprotocol)> Connect(String  Id,
                                                                             String  Password,
                                                                             String  Subprotocol)
        {

            var client = new ClientWebSocket();
            clients.Add(client);

            client.Options.AddSubProtocol(Subprotocol);
            client.Options.SetRequestHeader(
                "Authorization",
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Id}:{Password}"))
            );

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            try
            {
                await client.ConnectAsync(new Uri($"ws://127.0.0.1:{Controller.OCPPServerSettings.TCPPort}/{Id}"), timeout.Token);
            }
            catch (Exception)
            {
                return (false, null);
            }

            return (true, client.SubProtocol);

        }

        #endregion

        #region OCPP16OnOfferWithoutANodeOfIt_IsTurnedAwayAndSaidSo()

        /// <summary>
        /// A station asking for OCPP 1.6 is turned away - not taken by the
        /// node of OCPP 2.1 - and one asking for OCPP 2.1 gets in on the same
        /// port; the log says that OCPP 1.6 is offered and not spoken.
        /// </summary>
        [Test]
        public async Task OCPP16OnOfferWithoutANodeOfIt_IsTurnedAwayAndSaidSo()
        {

            Controller.WebSocketLogins.TrySetPasswordAuthentication(User_Id.Parse("cs016"), ThePassword, null, null, out _);
            Controller.WebSocketLogins.TrySetPasswordAuthentication(User_Id.Parse("cs021"), ThePassword, null, null, out _);

            var (connected16, _)             = await Connect("cs016", ThePassword, "ocpp1.6");
            var (connected21, subprotocol21) = await Connect("cs021", ThePassword, "ocpp2.1");

            Assert.Multiple(() => {

                Assert.That(Controller.StationServers.Count(),  Is.EqualTo(1),  "A local controller has a server of OCPP 1.6.");

                Assert.That(connected16,    Is.False, "A station of OCPP 1.6 was let in by the node of OCPP 2.1.");
                Assert.That(connected21,    Is.True,  "The station of OCPP 2.1 was not let in.");
                Assert.That(subprotocol21,  Is.EqualTo("ocpp2.1"));

                Assert.That(Controller.StationServer!.SecWebSocketProtocols, Does.Not.Contain("ocpp1.6"));

                Assert.That(Controller.Log.Recent(200).Select(entry => entry.Message),
                            Has.Some.Contains("does not speak OCPP 1.6"));

            });

        }

        #endregion

    }

}
