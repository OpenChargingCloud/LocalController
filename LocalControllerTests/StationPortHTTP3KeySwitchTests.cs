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
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// HTTP/3 of the station port shows the server key the port has switched
    /// to: QUIC is handed its certificate when it starts, and the port starts
    /// it again on a switch - seen here by a client that is not Hermod's, the
    /// HTTP/3 of .NET itself, which says what it was shown.
    /// </summary>
    public class StationPortHTTP3KeySwitchTests
    {

        #region Data

        private static readonly TimeSpan  ThroughWithin  = TimeSpan.FromSeconds(15);

        private String           directory   = default!;
        private LocalController  controller  = default!;
        private UInt16           port;
        private KeyPin           currentPin  = default!;
        private KeyPin           nextPin     = default!;

        private readonly ConcurrentQueue<String>  said  = [];

        #endregion

        #region SetUp / TearDown

        /// <summary>
        /// A controller whose station port is encrypted by a server key, with
        /// the next one announced, and takes WebSockets over HTTP/3 as well.
        /// </summary>
        [SetUp]
        public async Task StartAControllerWithTwoKeysOverHTTP3()
        {

            said.Clear();

            controller = await TestPorts.StartedOnFreshPorts(() => {

                             TestControllers.Remove(directory);

                             port       = APortFreeForBoth();

                             directory  = TestControllers.TemporaryDirectory("http3-key-switch");
                             Directory.CreateDirectory(directory);

                             // Before the controller is built: whether the port speaks TLS at
                             // all is decided when it starts.
                             var keys    = StationPortCertificates.StoreIn(directory);

                             currentPin  = StationPortCertificates.AServerKey(keys).KeyPin;
                             nextPin     = StationPortCertificates.AServerKey(keys, AsNext: true).KeyPin;

                             var made    = TestControllers.New(
                                               directory,
                                               new JObject(
                                                   new JProperty("nts",        new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                                   new JProperty("ocpp",       new JObject(new JProperty("nodeId", "lc001"))),
                                                   new JProperty("ocppServer", new JObject(
                                                       new JProperty("enabled",           true),
                                                       new JProperty("address",           "127.0.0.1"),
                                                       new JProperty("port",              port),
                                                       new JProperty("securityProfiles",  new JArray(2, 3)),
                                                       new JProperty("subprotocols",      new JArray("ocpp2.1")),
                                                       new JProperty("http3",             true)
                                                   ))
                                               )
                                           );

                             made.Log.OnLogged += entry => said.Enqueue(entry.Message);

                             return made;

                         });

        }

        [TearDown]
        public async Task StopIt()
        {

            await controller.DisposeAsync();

            TestControllers.Remove(directory);

        }

        #endregion


        #region (private static) APortFreeForBoth()

        /// <summary>
        /// A port free for UDP as for TCP: HTTP/3 is taken on the UDP port of
        /// the number of the TCP one - see CSMSOneTimePasswordOverHTTPVersionsTests.
        /// </summary>
        private static UInt16 APortFreeForBoth()
        {

            for (var attempt = 0; attempt < 200; attempt++)
            {

                var port = Random.Shared.Next(20000, 32768);

                try
                {

                    using var udp4 = new System.Net.Sockets.UdpClient(new IPEndPoint(System.Net.IPAddress.Loopback,     port));
                    using var udp6 = new System.Net.Sockets.UdpClient(new IPEndPoint(System.Net.IPAddress.IPv6Loopback, port));

                    var tcp4 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                    tcp4.Start();
                    tcp4.Stop();

                    return (UInt16) port;

                }
                catch (System.Net.Sockets.SocketException)
                { }

            }

            throw new InvalidOperationException("No port free for TCP and UDP was found.");

        }

        #endregion

        #region (private) ShownOverHTTP3()

        /// <summary>
        /// The key pin of what the station port shows over HTTP/3, as the
        /// HttpClient of .NET is shown it: a plain request, which a WebSocket
        /// server answers with a refusal - the handshake of QUIC is what counts.
        /// </summary>
        private async Task<KeyPin?> ShownOverHTTP3()
        {

            KeyPin? shown = null;

            using var handler  = new SocketsHttpHandler {
                                     SslOptions = new SslClientAuthenticationOptions {
                                         RemoteCertificateValidationCallback = (sender, certificate, chain, policyErrors) => {
                                             if (certificate is X509Certificate2 serverCertificate)
                                                 shown = KeyPin.FromCertificate(serverCertificate);
                                             return true;
                                         }
                                     }
                                 };

            using var client   = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

            using var request  = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/") {
                                     Version        = HttpVersion.Version30,
                                     VersionPolicy  = HttpVersionPolicy.RequestVersionExact
                                 };

            try
            {
                using var response = await client.SendAsync(request);
            }
            catch (HttpRequestException)
            {
                // What the server answers does not matter: what it showed does.
            }

            return shown;

        }

        #endregion

        #region (private static) Until(Condition)

        private static async Task<Boolean> Until(Func<Boolean> Condition)
        {

            var giveUp = DateTimeOffset.UtcNow + ThroughWithin;

            while (DateTimeOffset.UtcNow < giveUp)
            {

                if (Condition())
                    return true;

                await Task.Delay(50);

            }

            return Condition();

        }

        #endregion


        #region ASwitch_IsShownOverHTTP3_ToAClientOfDotNet()

        /// <summary>
        /// A switch to the next server key starts HTTP/3 again, on the same UDP
        /// port, and a new connection over HTTP/3 is shown the new key - which
        /// is what a station that pins it checks.
        /// </summary>
        [Test]
        public async Task ASwitch_IsShownOverHTTP3_ToAClientOfDotNet()
        {

            if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || !QuicConnection.IsSupported)
                Assert.Ignore("QUIC is not supported here: .NET has no client of HTTP/3 on this machine.");

            var server = controller.StationServer!;

            Assert.That(await Until(() => server.HTTP3Port?.ToUInt16() == port), Is.True,
                        "The station port takes no WebSockets over HTTP/3: " + server.WhyNoHTTP3);

            Assert.That(await ShownOverHTTP3(), Is.EqualTo(currentPin), "what HTTP/3 shows before the switch");

            Assert.That(controller.Certificates.TrySwitchToNextKey(StationPortCertificates.Listener, null, out _, out var error), Is.True, error);

            Assert.That(await Until(() => said.Any(message => message.Contains("HTTP/3 of the charging station port was started again"))),
                        Is.True, "HTTP/3 was not started again after the switch: " + server.WhyNoHTTP3 + Environment.NewLine + String.Join(Environment.NewLine, said));

            var shown = await ShownOverHTTP3();

            Assert.Multiple(() => {
                Assert.That(server.HTTP3Port?.ToUInt16(),  Is.EqualTo(port), "HTTP/3 is not taken on its port after the switch: " + server.WhyNoHTTP3);
                Assert.That(shown,                         Is.EqualTo(nextPin), "what HTTP/3 shows after the switch");
            });

        }

        #endregion

    }

}
