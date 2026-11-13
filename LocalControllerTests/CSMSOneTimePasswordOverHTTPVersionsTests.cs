/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of LocalController <https://github.com/OpenChargingCloud/LocalController>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The line up to the CSMS with a one-time token, over the versions of
    /// HTTP a connection of a charging station may name as well: a local
    /// controller that dials a second one standing in for the CSMS, over TLS,
    /// security profile 2.
    /// </summary>
    /// <remarks>
    /// What a charging station's connection can do, the controller's line to
    /// its CSMS can: name the version of HTTP its WebSocket is opened over,
    /// and bind its one-time token to the TLS session where that is HTTP/3 -
    /// and where it is not, not dial rather than send less than was asked for.
    /// </remarks>
    public class CSMSOneTimePasswordOverHTTPVersionsTests
    {

        #region Data

        private const String TheSharedSecret = "a-shared-secret-long-enough-for-ocpp";

        private static readonly TimeSpan ThroughWithin = TimeSpan.FromSeconds(15);

        private TestCA           ca                   = default!;
        private String           upstreamDirectory    = default!;
        private String           downstreamDirectory  = default!;
        private LocalController  upstream             = default!;
        private LocalController? downstream;
        private UInt16           csmsPort;

        #endregion

        #region SetUp / TearDown

        /// <summary>
        /// A controller standing in for the CSMS: TLS with a certificate of
        /// the test's authority, security profile 2, and 'lc002' known by a
        /// one-time token.
        /// </summary>
        [SetUp]
        public async Task StartACSMSThatKnowsATokenOfLC002()
        {

            ca       = TestCA.Create($"Site CA {Guid.NewGuid()}", WithIntermediate: true);

            upstream = await TestPorts.StartedOnFreshPorts(() => {

                           TestControllers.Remove(upstreamDirectory);

                           csmsPort           = APortFreeForBoth();

                           upstreamDirectory  = TestControllers.TemporaryDirectory("csms-totp-upstream");
                           Directory.CreateDirectory(upstreamDirectory);

                           using (var store = new ServerCertificateStore(Path.Combine(upstreamDirectory, ServerCertificateStore.DefaultDirectoryName)))
                           {

                               Assert.That(store.TryCreateKey("127.0.0.1", [ "127.0.0.1" ], null, out _, out var csr, out var keyError),
                                           Is.True, keyError);

                               using var certificate = ca.Sign(csr!, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

                               Assert.That(store.TryAddCertificate(ca.ChainPEM(certificate), [ "127.0.0.1" ], out _, out _, out var addError),
                                           Is.True, addError);

                           }

                           var made = TestControllers.New(
                                          upstreamDirectory,
                                          new JObject(
                                              new JProperty("nts", new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                              new JProperty("ocppServer", new JObject(
                                                  new JProperty("enabled",           true),
                                                  new JProperty("address",           "127.0.0.1"),
                                                  new JProperty("port",              csmsPort),
                                                  new JProperty("securityProfiles",  new JArray(2)),
                                                  new JProperty("reachableAs",       new JArray("127.0.0.1")),
                                                  new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1"))
                                              ))
                                          )
                                      );

                           if (!made.StationLogins.TrySetTOTP("lc002", TheSharedSecret, null, null, null, null, null, "The controller below", out _, out var error))
                               throw new InvalidOperationException($"The test's own upstream TOTP login was refused: {error}");

                           return made;

                       });

            downstreamDirectory = TestControllers.TemporaryDirectory("csms-totp-downstream");
            Directory.CreateDirectory(downstreamDirectory);

        }

        [TearDown]
        public async Task StopThem()
        {

            if (downstream is not null)
                await downstream.DisposeAsync();

            await upstream.DisposeAsync();

            ca.Dispose();

            TestControllers.Remove(downstreamDirectory);
            TestControllers.Remove(upstreamDirectory);

        }

        #endregion


        #region (private) AControllerThatDials(HTTPVersion, Bound, SharedSecret)

        /// <summary>
        /// The controller below, believing the CSMS by the site's authority,
        /// with a one-time token and no password, dialling over the version
        /// of HTTP it is told - or over none it was told.
        /// </summary>
        private async Task<LocalController> AControllerThatDials(String?  HTTPVersion,
                                                                 Boolean  Bound         = false,
                                                                 String   SharedSecret  = TheSharedSecret)
        {

            var attempt = 0;

            LocalController Made()
            {

                if (attempt++ > 0)
                {
                    TestControllers.Remove(downstreamDirectory);
                    downstreamDirectory = TestControllers.TemporaryDirectory("csms-totp-downstream");
                    Directory.CreateDirectory(downstreamDirectory);
                }

                var controller = TestControllers.New(
                                     downstreamDirectory,
                                     new JObject(
                                         new JProperty("nts",  new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                         new JProperty("ocpp", new JObject(new JProperty("nodeId", "lc002")))
                                     )
                                 );

                Assert.That(controller.Certificates.Import(Encoding.ASCII.GetBytes(ca.Certificate.ExportCertificatePem()),
                                                           CertificateKind.TLSRoot, null, "The site's authority", null,
                                                           out _, out var rootError),
                            Is.True, rootError);

                Assert.That(controller.CSMSLogin.TrySetTOTP("lc002", SharedSecret, null, null, null, null, out var tokenError, out _, Bound),
                            Is.True, tokenError);

                var csms = new JObject(
                               new JProperty("enabled",                true),
                               new JProperty("url",                    $"wss://127.0.0.1:{csmsPort}/lc002"),
                               new JProperty("securityProfile",        2),
                               new JProperty("reconnectInitialDelay",  1),
                               new JProperty("reconnectMaxDelay",      2)
                           );

                if (HTTPVersion is not null)
                    csms.Add("httpVersion", HTTPVersion);

                Assert.That(controller.TryUpdateCSMSConfiguration(csms, out var csmsError), Is.True, csmsError);

                return controller;

            }

            var started = await TestPorts.StartedOnFreshPorts(Made);

            downstream  = started;

            return started;

        }

        #endregion

        #region (private static) APortFreeForBoth() / (private) TheCSMSTakesHTTP3()

        /// <summary>
        /// A port that is free for UDP as for TCP, on both loopback
        /// addresses: Windows keeps ranges of ports for itself, for UDP and
        /// TCP apart, so the UDP port of the number of a free TCP port may
        /// not be there to have - and a WebSocket over HTTP/3 comes to the
        /// UDP port of the number of the TCP one.
        /// </summary>
        private static UInt16 APortFreeForBoth()
        {

            for (var attempt = 0; attempt < 200; attempt++)
            {

                var port = Random.Shared.Next(20000, 32768);

                try
                {

                    using var udp4 = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,     port));
                    using var udp6 = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Loopback, port));

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

        /// <summary>
        /// The CSMS takes WebSockets over HTTP/3, on the UDP port of the
        /// number of its TCP port - or the test says why it does not.
        /// </summary>
        private void TheCSMSTakesHTTP3()
        {

            var server = upstream.StationServer;

            SpinWait.SpinUntil(() => server?.HTTP3Port is not null || server?.WhyNoHTTP3 is not null, TimeSpan.FromSeconds(10));

            Assert.That(server?.HTTP3Port?.ToString(), Is.EqualTo(csmsPort.ToString()),
                        "The CSMS takes no WebSockets over HTTP/3: " + server?.WhyNoHTTP3);

        }

        #endregion

        #region (private) UntilItIsThrough(Controller) / UntilItSaysWhy(Controller)

        private async Task UntilItIsThrough(LocalController Controller)
        {

            var giveUp = DateTimeOffset.UtcNow + ThroughWithin;

            while (DateTimeOffset.UtcNow < giveUp &&
                   !(upstream.StationServer?.WebSocketConnections.Any() == true && Controller.CSMSConnected))
            {
                await Task.Delay(100);
            }

        }

        private static async Task UntilItSaysWhy(LocalController Controller)
        {

            var giveUp = DateTimeOffset.UtcNow + ThroughWithin;

            while (DateTimeOffset.UtcNow < giveUp && !Controller.CSMSConnected && Controller.CSMSLastProblem is null)
                await Task.Delay(100);

        }

        #endregion


        #region ARawTokenSignsItInOverEveryVersion(Named)

        /// <summary>
        /// A one-time token that is not bound is sent under the controller's
        /// name over whatever the line names, and over what the CSMS chooses
        /// where it names nothing.
        /// </summary>
        [TestCase(null)]
        [TestCase("auto")]
        [TestCase("1.1")]
        [TestCase("2")]
        [TestCase("3")]
        public async Task ARawTokenSignsItInOverEveryVersion(String? Named)
        {

            if (Named == "3")
                TheCSMSTakesHTTP3();

            var controller = await AControllerThatDials(Named);

            await UntilItIsThrough(controller);

            Assert.Multiple(() => {
                Assert.That(controller.CSMSConnected,    Is.True, controller.CSMSLastProblem);
                Assert.That(controller.CSMSLastProblem,  Is.Null);
                Assert.That(upstream.StationServer?.WebSocketConnections.Count(), Is.GreaterThan(0),
                            "The CSMS end has no connection.");
                Assert.That(controller.CSMSConfigurationJSON().Value<String>("httpVersion"),
                            Is.EqualTo(Named ?? "auto"),
                            "What the page reads does not say what the line is opened over.");
            });

        }

        #endregion

        #region ATokenBoundToTheLineSignsItInOverHTTP3()

        /// <summary>
        /// Over HTTP/3 the TLS of the line exports what a token is bound with:
        /// the controller binds it, and a CSMS that takes nothing but a bound
        /// token of 'lc002' lets it in.
        /// </summary>
        [Test]
        public async Task ATokenBoundToTheLineSignsItInOverHTTP3()
        {

            Assert.That(upstream.StationLogins.TrySetTOTPBinding("lc002", true, out var bindingError), Is.True, bindingError);

            TheCSMSTakesHTTP3();

            var controller = await AControllerThatDials("3", Bound: true);

            await UntilItIsThrough(controller);

            Assert.Multiple(() => {
                Assert.That(controller.CSMSConnected,    Is.True, controller.CSMSLastProblem);
                Assert.That(controller.CSMSLastProblem,  Is.Null);
                Assert.That(upstream.StationServer?.WebSocketConnections.Count(), Is.GreaterThan(0),
                            "The CSMS end has no connection.");
                Assert.That(controller.CSMSConfigurationJSON()["credentials"]?["totp"]?.Value<Boolean>("tlsChannelBinding"),
                            Is.True,
                            "What the page reads does not say that the token is bound.");
            });

        }

        #endregion

        #region ARawTokenIsRefusedByACSMSThatAsksForTheBinding()

        [Test]
        public async Task ARawTokenIsRefusedByACSMSThatAsksForTheBinding()
        {

            Assert.That(upstream.StationLogins.TrySetTOTPBinding("lc002", true, out var bindingError), Is.True, bindingError);

            TheCSMSTakesHTTP3();

            var controller = await AControllerThatDials("3");

            await UntilItSaysWhy(controller);

            Assert.Multiple(() => {
                Assert.That(controller.CSMSConnected,    Is.False);
                Assert.That(controller.CSMSLastProblem,  Does.Contain("refused this local controller"));
            });

        }

        #endregion

        #region ATokenToBeBoundIsNotDialledWhereTheLineDoesNotNameHTTP3(Named)

        /// <summary>
        /// Left the choice of the version, or told HTTP/1.1 or HTTP/2, the
        /// line may be, or is, one whose TLS exports nothing: it is not
        /// dialled, and says that naming HTTP/3 would do - rather than
        /// sending the token unbound.
        /// </summary>
        [TestCase(null)]
        [TestCase("auto")]
        [TestCase("1.1")]
        [TestCase("2")]
        public async Task ATokenToBeBoundIsNotDialledWhereTheLineDoesNotNameHTTP3(String? Named)
        {

            var controller = await AControllerThatDials(Named, Bound: true);

            await UntilItSaysWhy(controller);

            Assert.Multiple(() => {
                Assert.That(controller.CSMSConnected,    Is.False);
                Assert.That(controller.CSMSLastProblem,  Does.Contain("is not dialled").And.Contain("Name HTTP/3"));
                Assert.That(upstream.StationServer?.WebSocketConnections.Any(), Is.Not.True,
                            "The CSMS was dialled all the same.");
            });

        }

        #endregion

        #region AVersionThatIsNoneIsRefused_AndHTTP3WithoutTLS()

        [Test]
        public async Task AVersionThatIsNoneIsRefused_AndHTTP3WithoutTLS()
        {

            var controller = await AControllerThatDials(null);

            var noVersion  = controller.TryUpdateCSMSConfiguration(new JObject(new JProperty("httpVersion", "4")), out var noVersionError);

            var withoutTLS = controller.TryUpdateCSMSConfiguration(new JObject(
                                                                       new JProperty("url",              $"ws://127.0.0.1:{csmsPort}/lc002"),
                                                                       new JProperty("securityProfile",  1),
                                                                       new JProperty("httpVersion",      "3")
                                                                   ),
                                                                   out var withoutTLSError);

            Assert.Multiple(() => {
                Assert.That(noVersion,        Is.False);
                Assert.That(noVersionError,   Does.Contain("\"auto\", \"1.1\", \"2\" or \"3\""));
                Assert.That(withoutTLS,       Is.False);
                Assert.That(withoutTLSError,  Does.Contain("wss://"));
            });

        }

        #endregion

        #region TheBindingOfTheTokenIsSetWithoutItsSecret_AndOutlivesANewOne()

        [Test]
        public async Task TheBindingOfTheTokenIsSetWithoutItsSecret_AndOutlivesANewOne()
        {

            var controller = await AControllerThatDials("3");
            var login      = controller.CSMSLogin;

            var bound      = login.TrySetTOTPBinding(true, out var boundError);
            var asBound    = login.TOTPConfig()?.UseTLSExporterMaterial;
            var secret     = login.TOTPConfig()?.SharedSecret;

            var anew       = login.TrySetTOTP("lc002", TheSharedSecret + "-anew", null, null, null, null, out var anewError);
            var stillBound = login.TOTPConfig()?.UseTLSExporterMaterial;

            var unbound    = login.TrySetTOTPBinding(false, out _);
            var atLast     = login.TOTPConfig()?.UseTLSExporterMaterial;

            var cleared    = login.TryClear(out _);
            var nothing    = login.TrySetTOTPBinding(true, out var nothingError);

            Assert.Multiple(() => {
                Assert.That(bound,         Is.True,  boundError);
                Assert.That(asBound,       Is.True);
                Assert.That(secret,        Is.EqualTo(TheSharedSecret),  "the secret was changed");
                Assert.That(anew,          Is.True,  anewError);
                Assert.That(stillBound,    Is.True,  "a new secret undid the binding");
                Assert.That(unbound,       Is.True);
                Assert.That(atLast,        Is.False);
                Assert.That(cleared,       Is.True);
                Assert.That(nothing,       Is.False, "bound what is not there");
                Assert.That(nothingError,  Is.Not.Null);
            });

        }

        #endregion

    }

}
