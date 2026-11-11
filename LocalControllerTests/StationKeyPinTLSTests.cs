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
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Charging stations on the station port by key pin, over real handshakes:
    /// the controller presents its server key in a carrier certificate, the
    /// station checks the controller by its key pin alone, and the controller
    /// lets the station in by the key pin of its client certificate - as the
    /// station the pin is listed for, on that station's path, and as nobody
    /// else, whatever the certificate says.
    /// </summary>
    /// <remarks>
    /// Every certificate here signs itself and lives in memory: no
    /// certificate authority is made, and none reaches a certificate store.
    /// </remarks>
    public class StationKeyPinTLSTests
    {

        #region Data

        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

        private String           directory   = default!;
        private LocalController  controller  = default!;
        private UInt16           port;
        private KeyPin           serverPin   = default!;

        private ECDsa            cs001Key    = default!;
        private ECDsa            cs002Key    = default!;

        private readonly ConcurrentQueue<String>           said  = [];
        private readonly ConcurrentQueue<KeyPinValidation>  told  = [];
        private readonly List<IDisposable>                  made  = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void StartAControllerWithAServerKey()
        {

            directory = TestControllers.TemporaryDirectory("keypins");
            Directory.CreateDirectory(directory);

            said.Clear();
            told.Clear();

            cs001Key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            cs002Key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            #region The server key, made before anything starts

            // Before the controller is built, because whether the port speaks
            // TLS at all is decided when it starts.
            using (var store = new ServerCertificateStore(Path.Combine(directory, ServerCertificateStore.DefaultDirectoryName)))
            {
                Assert.That(store.TryCreateServerKey(null, AsNext: false, out _, out var pin, out var error), Is.True, error);
                serverPin = pin!;
            }

            #endregion

            controller = TestPorts.StartedOnFreshPorts(() => {

                             port      = TestPorts.Free();

                             var lc    = TestControllers.New(
                                             directory,
                                             new JObject(

                                                 new JProperty("nts", new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),

                                                 new JProperty("ocppServer", new JObject(
                                                     new JProperty("enabled",           true),
                                                     new JProperty("address",           "127.0.0.1"),
                                                     new JProperty("port",              port),
                                                     new JProperty("securityProfiles",  new JArray(2, 3))
                                                 ))

                                             )
                                         );

                             lc.Log.OnLogged += entry => said.Enqueue(entry.Message);

                             Assert.That(lc.StationLogins.TryAddKeyPin("cs001", KeyPin.FromKey(cs001Key).ToHex(), "bay 1", null, null, null, out _, out var error1), Is.True, error1);
                             Assert.That(lc.StationLogins.TryAddKeyPin("cs002", KeyPin.FromKey(cs002Key).ToHex(), "bay 2", null, null, null, out _, out var error2), Is.True, error2);

                             return lc;

                         }).GetAwaiter().GetResult();

        }

        [TearDown]
        public async Task StopIt()
        {

            if (controller is not null)
                await controller.DisposeAsync();

            foreach (var disposable in made)
                disposable.Dispose();

            made.Clear();

            cs001Key?.Dispose();
            cs002Key?.Dispose();

            TestControllers.Remove(directory);

        }

        #endregion

        #region (private) Certificate(Key, CommonName)

        /// <summary>
        /// A self-signed client certificate for the given key: what a station
        /// without a certificate authority wraps its key in. Its name is
        /// whatever the station - or whoever holds its key - writes into it.
        /// </summary>
        private X509Certificate2 Certificate(ECDsa   Key,
                                             String  CommonName)
        {

            var request = new CertificateRequest($"CN={CommonName}", Key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.2") ], false));

            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            // SChannel wants a key it can find again: out to PKCS#12 and back.
            var usable = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet);

            made.Add(usable);

            return usable;

        }

        #endregion

        #region (private) Connect(Station, ClientCertificate)

        /// <summary>
        /// A charging station connecting to its path with the given client
        /// certificate, checking the controller by its key pin alone - and what
        /// it was answered, while it is still connected.
        /// </summary>
        private async Task<(WebSocketClient Client, HTTPStatusCode? Status)> Connect(String            Station,
                                                                                    X509Certificate2  ClientCertificate)
        {

            var client = new WebSocketClient(
                             URL.Parse($"wss://127.0.0.1:{port}/{Station}"),
                             SecWebSocketProtocols:       [ "ocpp2.1" ],
                             RemoteCertificateValidator:  KeyPinValidator.ForServerCertificates<IWebSocketClient>(
                                                              KeyPinPolicy.PinnedTo(serverPin),
                                                              told.Enqueue
                                                          ),
                             LocalCertificateSelector:    (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) => ClientCertificate,
                             ClientCertificates:          [ ClientCertificate ],
                             ConnectTimeout:              TimeSpan.FromSeconds(10)
                         );

            try
            {

                var connect = client.Connect();

                if (await Task.WhenAny(connect, Task.Delay(Guard)) != connect)
                    Assert.Fail("Connect() did not return.");

                var (_, response) = await connect;

                return (client, response?.HTTPStatusCode);

            }
            catch (Exception)
            {
                // Refused during the handshake: no answer at all, which is not
                // a 101 either.
                return (client, null);
            }

        }

        #endregion

        #region (private) Registered / Log

        /// <summary>
        /// The charging stations the station port has a connection of now.
        /// </summary>
        private String[] Registered
            => [ .. controller.StationServer!.ConnectedNetworkingNodeIds.Select(id => id.ToString()).Order() ];

        /// <summary>
        /// What the controller said, for the message of an assertion.
        /// </summary>
        private String Log
            => String.Join(Environment.NewLine, said.Where(line => line.Contains("station", StringComparison.OrdinalIgnoreCase)));

        #endregion


        #region TheControllerShowsItsKeyPin()

        /// <summary>
        /// What a technician reads off at commissioning: the port is encrypted,
        /// and its key pin is the one of the key the store made.
        /// </summary>
        [Test]
        public void TheControllerShowsItsKeyPin()
        {

            Assert.Multiple(() => {
                Assert.That(controller.OCPPServerTLS,                                          Is.True, "a controller with a server key is not encrypted");
                Assert.That(controller.OCPPServerKeyPin,                                       Is.EqualTo(serverPin));
                Assert.That(controller.OCPPServerConfigurationJSON()["state"]?.Value<String>("keyPin"), Is.EqualTo(serverPin.ToHex()));
                Assert.That(said,                                                              Has.Some.Contains($"key pin {serverPin}"),
                            "the start does not say the key pin");
            });

        }

        #endregion

        #region AStationIsAdmittedByItsKeyPin()

        /// <summary>
        /// The whole point: a station with a self-signed certificate that no
        /// authority vouches for connects to its own path, is let in by the key
        /// pin listed for it, and is registered under its identity - having
        /// checked the controller by the controller's key pin alone.
        /// </summary>
        [Test]
        public async Task AStationIsAdmittedByItsKeyPin()
        {

            var (client, status) = await Connect("cs001", Certificate(cs001Key, "station.invalid"));

            await using (client)
            {

                Assert.Multiple(() => {
                    Assert.That(status,                                Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);
                    Assert.That(Registered,                            Is.EqualTo(new[] { "cs001" }));
                    Assert.That(told,                                  Is.Not.Empty, "the station did not check the controller by its key pin");
                    Assert.That(told.Select(result => result.Outcome), Has.All.EqualTo(KeyPinOutcome.Accepted));
                    Assert.That(told.Select(result => result.Pin),     Has.All.EqualTo(serverPin));
                    Assert.That(said,                                  Has.Some.Contains($"'cs001' signed in").And.Some.Contains("key pin"));
                });

            }

        }

        #endregion

        #region AnotherKeyIsRefused()

        /// <summary>
        /// The same name, another key: refused, and nothing is registered.
        /// </summary>
        [Test]
        public async Task AnotherKeyIsRefused()
        {

            using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var (client, status) = await Connect("cs001", Certificate(stranger, "station.invalid"));

            await using (client)
            {
                Assert.Multiple(() => {
                    Assert.That(status,      Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "a key no station has was let in");
                    Assert.That(Registered,  Is.Empty);
                });
            }

        }

        #endregion

        #region APinOnAnotherStationsPathIsRefused()

        /// <summary>
        /// cs001's key asks to be cs002: the pin is cs001's, and cs001 alone it
        /// lets in.
        /// </summary>
        [Test]
        public async Task APinOnAnotherStationsPathIsRefused()
        {

            var (client, status) = await Connect("cs002", Certificate(cs001Key, "station.invalid"));

            await using (client)
            {
                Assert.Multiple(() => {
                    Assert.That(status,      Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "a station's key opened another station's path");
                    Assert.That(Registered,  Is.Empty);
                    Assert.That(said,        Has.Some.Contains("is the charging station 'cs001''s, and it asked to connect as 'cs002'"));
                });
            }

        }

        #endregion

        #region ACertificateNamingAnotherStationIsThePinsStation()

        /// <summary>
        /// A certificate accepted by its key pin contributes its pin and
        /// nothing else: cs001's key in a certificate that says "cs002" is let in
        /// as cs001 on cs001's path - neither refused for the name, nor taken for
        /// cs002 - and does not open cs002's path.
        /// </summary>
        [Test]
        public async Task ACertificateNamingAnotherStationIsThePinsStation()
        {

            var certificate = Certificate(cs001Key, "cs002");

            var (asItself, itselfStatus) = await Connect("cs001", certificate);

            await using (asItself)
            {
                Assert.Multiple(() => {
                    Assert.That(itselfStatus,  Is.EqualTo(HTTPStatusCode.SwitchingProtocols), $"its own path was refused for the name in its certificate{Environment.NewLine}{Log}");
                    Assert.That(Registered,    Is.EqualTo(new[] { "cs001" }), "the connection is registered as somebody else");
                });
            }

            var (asNamed, namedStatus) = await Connect("cs002", certificate);

            await using (asNamed)
            {
                Assert.Multiple(() => {
                    Assert.That(namedStatus,   Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "the name in a pinned certificate opened that station's path");
                    Assert.That(Registered,    Has.No.Member("cs002"));
                });
            }

        }

        #endregion

        #region AGroupWithoutKeyPinsIsRefused() / AGroupWithoutProfile3IsRefused()

        /// <summary>
        /// The group decides, as for every other way in: one that does not take
        /// key pins lets nobody in by one.
        /// </summary>
        [Test]
        public async Task AGroupWithoutKeyPinsIsRefused()
        {

            Assert.That(controller.StationLogins.TryAddOrUpdateGroup("no-pins", "No key pins", true, [ AuthMethod.Certificate, AuthMethod.Basic ], [ 1, 2, 3 ], null, out var error),
                        Is.True, error);

            Assert.That(controller.StationLogins.TrySetGroup("cs001", "no-pins", out var moveError), Is.True, moveError);

            var (client, status) = await Connect("cs001", Certificate(cs001Key, "station.invalid"));

            await using (client)
            {
                Assert.Multiple(() => {
                    Assert.That(status,      Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "a group that takes no key pins let a station in by one");
                    Assert.That(Registered,  Is.Empty);
                    Assert.That(said,        Has.Some.Contains("which does not accept keypin"));
                });
            }

        }

        /// <summary>
        /// A client key is security profile 3, and a group that does not take
        /// that profile lets nobody in by one either.
        /// </summary>
        [Test]
        public async Task AGroupWithoutProfile3IsRefused()
        {

            Assert.That(controller.StationLogins.TryAddOrUpdateGroup("profile-2", "Profile 2 only", true, [ AuthMethod.KeyPin, AuthMethod.Basic ], [ 2 ], null, out var error),
                        Is.True, error);

            Assert.That(controller.StationLogins.TrySetGroup("cs001", "profile-2", out var moveError), Is.True, moveError);

            var (client, status) = await Connect("cs001", Certificate(cs001Key, "station.invalid"));

            await using (client)
            {
                Assert.Multiple(() => {
                    Assert.That(status,      Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "a group without profile 3 let a station in by a client key");
                    Assert.That(Registered,  Is.Empty);
                    Assert.That(said,        Has.Some.Contains("which does not accept security profile 3"));
                });
            }

        }

        #endregion

        #region ASwitchedOffStationIsRefused()

        /// <summary>
        /// A station that is switched off is not let in by its key pin - and
        /// its pin does not even get it through the handshake.
        /// </summary>
        [Test]
        public async Task ASwitchedOffStationIsRefused()
        {

            Assert.That(controller.StationLogins.TrySetEnabled("cs001", false, out var error), Is.True, error);

            var (client, status) = await Connect("cs001", Certificate(cs001Key, "station.invalid"));

            await using (client)
            {
                Assert.Multiple(() => {
                    Assert.That(status,      Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "a station that is switched off was let in by its key pin");
                    Assert.That(Registered,  Is.Empty);
                    Assert.That(said,        Has.None.Contains("was accepted by its key pin"), "a switched-off station's pin got it through the handshake");
                });
            }

        }

        /// <summary>
        /// The same where the station's certificate also leads to an accepted
        /// chain, and so gets through the handshake by it: its key pin is still
        /// the station's, and the station is still switched off.
        /// </summary>
        [Test]
        public async Task ASwitchedOffStationIsRefused_EvenWithAnAcceptedChain()
        {

            var certificate = Certificate(cs001Key, "cs001");

            Assert.That(controller.ClientTrust.TryAdd(TestCA.ToPEM(certificate), "cs001 itself", out _, out _, out var trustError), Is.True, trustError);
            Assert.That(controller.StationLogins.TrySetEnabled("cs001", false, out var error), Is.True, error);

            var (client, status) = await Connect("cs001", certificate);

            await using (client)
            {
                Assert.Multiple(() => {
                    Assert.That(status,      Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "a station that is switched off was let in");
                    Assert.That(Registered,  Is.Empty);
                    Assert.That(said,        Has.Some.Contains("the charging station 'cs001' is switched off here"));
                });
            }

        }

        #endregion

        #region ACertificateStationIsStillLetInByItsChain() / AStationWithKeyPinsIsNotLetInByACertificateOfItsName()

        /// <summary>
        /// Beside the key pins, a station whose certificate leads to an accepted
        /// chain is let in by it and its name, as before - here a certificate
        /// accepted as its own anchor, so that no authority is made.
        /// </summary>
        [Test]
        public async Task ACertificateStationIsStillLetInByItsChain()
        {

            using var cs003Key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var certificate     = Certificate(cs003Key, "cs003");

            Assert.That(controller.ClientTrust.TryAdd(TestCA.ToPEM(certificate), "cs003 itself", out _, out _, out var error), Is.True, error);

            var (client, status) = await Connect("cs003", certificate);

            await using (client)
            {
                Assert.Multiple(() => {
                    Assert.That(status,      Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);
                    Assert.That(Registered,  Is.EqualTo(new[] { "cs003" }));
                });
            }

        }

        /// <summary>
        /// A station is let in by its key pins or by a certificate authority's
        /// word, never both: cs001 has key pins, so a certificate in its name
        /// with another key is refused, however accepted its chain.
        /// </summary>
        [Test]
        public async Task AStationWithKeyPinsIsNotLetInByACertificateOfItsName()
        {

            using var otherKey  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var certificate     = Certificate(otherKey, "cs001");

            Assert.That(controller.ClientTrust.TryAdd(TestCA.ToPEM(certificate), "a certificate in cs001's name", out _, out _, out var error), Is.True, error);

            var (client, status) = await Connect("cs001", certificate);

            await using (client)
            {
                Assert.Multiple(() => {
                    Assert.That(status,      Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "a station with key pins was let in by a certificate of its name");
                    Assert.That(Registered,  Is.Empty);
                    Assert.That(said,        Has.Some.Contains("is let in by its key pins"));
                });
            }

        }

        #endregion

        #region APinTakenAwayBetweenHandshakeAndUpgradeOpensNothing()

        /// <summary>
        /// The client decides how long it waits between the TLS handshake and
        /// the upgrade. A certificate let through the handshake by cs001's pin,
        /// with "cs003" written in it, waits until the pin is taken away and
        /// then asks to be cs003 - which nobody lists, so its chain would be the
        /// whole decision. The chain was never looked at, and is looked at now.
        /// </summary>
        [Test]
        public async Task APinTakenAwayBetweenHandshakeAndUpgradeOpensNothing()
        {

            var certificate = Certificate(cs001Key, "cs003");

            using var tcp = new TcpClient();

            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port);

            await using var tls = new SslStream(tcp.GetStream(), false);

            using var timeout = new CancellationTokenSource(Guard);

            await tls.AuthenticateAsClientAsync(
                      new SslClientAuthenticationOptions {
                          TargetHost                           = "127.0.0.1",
                          EnabledSslProtocols                  = SslProtocols.Tls12 | SslProtocols.Tls13,
                          ClientCertificates                   = [ certificate ],
                          LocalCertificateSelectionCallback    = (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) => certificate,
                          RemoteCertificateValidationCallback  = KeyPinValidator.ForSslStream(KeyPinPolicy.PinnedTo(serverPin))
                      },
                      timeout.Token
                  );

            // The handshake is through on the controller's side, by the pin.
            while (!said.Any(line => line.Contains("was accepted by its key pin")) && !timeout.IsCancellationRequested)
                await Task.Delay(20);

            Assert.That(said, Has.Some.Contains("was accepted by its key pin"), "the handshake did not get through by the pin");

            Assert.That(controller.StationLogins.TryRemoveKeyPin("cs001", KeyPin.FromKey(cs001Key).ToHex(), out var error), Is.True, error);

            var request = "GET /cs003 HTTP/1.1\r\n" +
                          $"Host: 127.0.0.1:{port}\r\n" +
                          "Upgrade: websocket\r\n" +
                          "Connection: Upgrade\r\n" +
                          $"Sec-WebSocket-Key: {Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))}\r\n" +
                          "Sec-WebSocket-Version: 13\r\n" +
                          "Sec-WebSocket-Protocol: ocpp2.1\r\n" +
                          "\r\n";

            await tls.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            await tls.FlushAsync(timeout.Token);

            var answer = new StringBuilder();
            var buffer = new Byte[4096];

            try
            {
                while (!answer.ToString().Contains("\r\n\r\n"))
                {

                    var read = await tls.ReadAsync(buffer, timeout.Token);

                    if (read == 0)
                        break;

                    answer.Append(Encoding.ASCII.GetString(buffer, 0, read));

                }
            }
            catch (IOException)
            {
                // Dropped without an answer: no 101 either.
            }

            var statusLine = answer.ToString().Split("\r\n")[0];

            Assert.Multiple(() => {
                Assert.That(statusLine,  Does.Not.Contain(" 101 "), "a pin taken away still opened a path it was never listed for");
                Assert.That(Registered,  Is.Empty);
                Assert.That(said,        Has.Some.Contains("is no charging station's any more"));
            });

        }

        #endregion

    }

}
