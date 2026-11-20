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
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.LocalController.OCPP;
using cloud.charging.open.protocols.OCPP.Node.Serving;

using OCPPConnections = cloud.charging.open.protocols.WWCP.Node.Connections;
using WWCPNode = cloud.charging.open.protocols.WWCP.Node.WWCPNode;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// A CSMS that dials the controller signs in with what is more than a
    /// password: a client certificate - known by the key pin of its WebSocket
    /// login, or by a chain the station port accepts, issued to the name it
    /// comes under - or a one-time password of its login, bound to the TLS
    /// session of its line, over HTTP/3.
    /// </summary>
    /// <remarks>
    /// The CSMS is a second controller here, whose line up dials the first:
    /// what it sends - a certificate with its chain, a bound one-time
    /// password over HTTP/3 - is what WWCPNode sends, which the CSMS
    /// that dials uses unchanged. The controller under test lets it in on the
    /// port of its charging stations, by its WebSocket login, as it lets in a
    /// charging station.
    /// </remarks>
    public class CSMSDiallingInSignInTests
    {

        #region Data

        private const           String    CSMSName          = "csms-01";
        private const           String    TheSharedSecret   = "a-shared-secret-long-enough-for-ocpp";
        private const           String    RootGroup         = "operator";

        private static readonly TimeSpan  ThroughWithin     = TimeSpan.FromSeconds(15);

        private TestCA           ca                 = default!;
        private String           controllerDirectory = default!;
        private String           csmsDirectory       = default!;
        private LocalController  controller          = default!;
        private LocalController? csms;
        private UInt16           port;

        private readonly ConcurrentQueue<String>  said  = [];
        private readonly List<TestCA>             more  = [];

        #endregion

        #region SetUp / TearDown

        /// <summary>
        /// The controller under test: its station port encrypted with a
        /// certificate of the site's authority, taking security profiles 2 and 3,
        /// over HTTP/3 as well, and reporting to a CSMS it does not dial.
        /// </summary>
        [SetUp]
        public async Task StartTheControllerTheCSMSDials()
        {

            said.Clear();

            ca          = TestCA.Create($"Site CA {Guid.NewGuid()}", WithIntermediate: true);

            controller  = await TestPorts.StartedOnFreshPorts(() => {

                              TestControllers.Remove(controllerDirectory);

                              port                 = APortFreeForBoth();

                              controllerDirectory  = TestControllers.TemporaryDirectory("csms-dials-in");
                              Directory.CreateDirectory(controllerDirectory);

                              // What the station port presents, issued by the authority for a key made in
                              // the node's store: a TLS server identity told "ocpp".
                              StationPortCertificates.AnIdentityIn(controllerDirectory, ca, [ "127.0.0.1" ], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

                              var made = TestControllers.New(
                                             controllerDirectory,
                                             new JObject(
                                                 new JProperty("nts",        new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                                 new JProperty("ocpp",       new JObject(new JProperty("nodeId", "lc001"))),
                                                 new JProperty("webSocketServer", new JObject(
                                                     new JProperty("enabled",           true),
                                                     new JProperty("address",           "127.0.0.1"),
                                                     new JProperty("port",              port),
                                                     new JProperty("clientAuthentication",  new JArray("httpAuthOverTLS", "tlsClientCertificate")),
                                                     new JProperty("reachableAs",       new JArray("127.0.0.1")),
                                                     new JProperty("subprotocols",      new JArray("ocpp2.1")),
                                                     new JProperty("http3",             true)
                                                 )),
                                                 new JProperty("csms",       new JObject(new JProperty("enabled", true)))
                                             )
                                         );

                              made.Log.OnLogged += entry => said.Enqueue(entry.Message);

                              return made;

                          });

            csmsDirectory = TestControllers.TemporaryDirectory("csms-dials-in-csms");
            Directory.CreateDirectory(csmsDirectory);

        }

        [TearDown]
        public async Task StopThem()
        {

            if (csms is not null)
                await csms.DisposeAsync();

            await controller.DisposeAsync();

            ca.Dispose();

            foreach (var other in more)
                other.Dispose();

            more.Clear();

            TestControllers.Remove(csmsDirectory);
            TestControllers.Remove(controllerDirectory);

        }

        #endregion


        #region (private static) APortFreeForBoth()

        /// <summary>
        /// A port free for UDP as for TCP: a WebSocket over HTTP/3 comes to the
        /// UDP port of the number of the TCP one - see
        /// CSMSOneTimePasswordOverHTTPVersionsTests.
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

        #endregion

        #region (private) TheControllerTakesHTTP3()

        private void TheControllerTakesHTTP3()
        {

            var server = controller.StationServer;

            SpinWait.SpinUntil(() => server?.HTTP3Port is not null || server?.WhyNoHTTP3 is not null, TimeSpan.FromSeconds(10));

            Assert.That(server?.HTTP3Port?.ToString(), Is.EqualTo(port.ToString()),
                        "The controller takes no WebSockets over HTTP/3: " + server?.WhyNoHTTP3);

        }

        #endregion

        #region (private) AnIdentity(Subject, Authority = null) / KeyPinOf(Identity)

        /// <summary>
        /// A TLS identity the given authority issued - the site's, where none
        /// is given -, with its private key and the sub-CA it was issued by,
        /// as the PKCS#12 an import takes: or with its key alone, where it is
        /// not brought with its sub-CA.
        /// </summary>
        private Byte[] AnIdentity(String Subject, TestCA? Authority = null, Boolean WithItsSubCA = true)
        {

            var authority     = Authority ?? ca;

            using var key     = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request       = new CertificateRequest($"CN={Subject}", key, HashAlgorithmName.SHA256);

            using var issued  = authority.Sign(request.CreateSigningRequestPem(),
                                               DateTimeOffset.UtcNow.AddDays(-1),
                                               DateTimeOffset.UtcNow.AddYears(1),
                                               ClientAuthentication: true);

            using var withKey = issued.CopyWithPrivateKey(key);

            var collection    = new X509Certificate2Collection { withKey };

            if (WithItsSubCA && authority.Intermediate is not null)
                collection.Add(authority.Intermediate);

            return collection.Export(X509ContentType.Pkcs12)!;

        }

        /// <summary>
        /// The key pin of the key of the first certificate of a PKCS#12.
        /// </summary>
        private static String KeyPinOf(Byte[] Identity)
        {
            var certificates = X509CertificateLoader.LoadPkcs12Collection(Identity, null);
            try
            {
                return KeyPin.FromCertificate(certificates.First(one => one.HasPrivateKey)).ToHex();
            }
            finally
            {
                foreach (var one in certificates)
                    one.Dispose();
            }
        }

        #endregion

        #region (private) TheCSMSIsWrittenDown(...)

        /// <summary>
        /// Write down, on the controller under test, the CSMS that dials it:
        /// its WebSocket login, with the key pin of its client certificate or
        /// a one-time password bound to its TLS session - or, by its CA, the
        /// chain of the given authority, the site's where none is given, which
        /// the station port accepts -, and the connection it makes.
        /// </summary>
        private String TheCSMSIsWrittenDown(String?  KeyPins           = null,
                                            Boolean  ByCA              = false,
                                            Boolean  BoundOneTimePass  = false,
                                            TestCA?  Operator          = null)
        {

            if (BoundOneTimePass)
            {

                Assert.That(controller.WebSocketLogins.TrySetTOTPAuthentication(CSMSName, TheSharedSecret, null, null, null, null, null, I18NString.Create("What the CSMS signs in with"),
                                                                  out var loginError),
                            Is.True, loginError);

                Assert.That(controller.WebSocketLogins.TrySetTOTPBinding(CSMSName, true, out loginError), Is.True, loginError);

            }

            if (KeyPins is not null)
                Assert.That(controller.WebSocketLogins.TryAddKeyPin(CSMSName, KeyPins, "its client certificate", null, I18NString.Create("What the CSMS signs in with"), "tests",
                                                                    out _, out var pinError),
                            Is.True, pinError);

            if (ByCA)
                Assert.That(controller.ClientTrust.TryAdd((Operator ?? ca).Certificate.ExportCertificatePem(), "The operator's authority",
                                                          out _, out _, out var trustError),
                            Is.True, trustError);

            Assert.That(controller.Connections.TryAddConnection(I18NString.Create("The CSMS, dialling in"), URL.Parse($"wss://127.0.0.1:{port}/{CSMSName}"), "CSMS", true, null, null,
                                                                out var id, out var error,
                                                                Inbound: true),
                        Is.True, error);

            return id!;

        }

        #endregion

        #region (private) ACSMSThatDials(...)

        /// <summary>
        /// The CSMS: a second controller whose line up dials the controller
        /// under test on the given path, believing it by the site's authority -
        /// with a client certificate, or with a one-time password over the
        /// given version of HTTP.
        /// </summary>
        /// <param name="NamedByItsConnection">
        /// Whether the client certificate is the identity its connection names,
        /// as the Connections page sets it - shown as WWCPNode shows
        /// one -, rather than the one the section "csms" chose before there
        /// were connections.
        /// </param>
        private async Task<LocalController> ACSMSThatDials(String   Path,
                                                           Byte[]?  Identity              = null,
                                                           Boolean  OneTimePass           = false,
                                                           Boolean  Bound                 = false,
                                                           String?  HTTPVersion           = null,
                                                           Boolean  NamedByItsConnection  = false)
        {

            var attempt = 0;

            LocalController Made()
            {

                if (attempt++ > 0)
                {
                    TestControllers.Remove(csmsDirectory);
                    csmsDirectory = TestControllers.TemporaryDirectory("csms-dials-in-csms");
                    Directory.CreateDirectory(csmsDirectory);
                }

                var made = TestControllers.New(
                               csmsDirectory,
                               new JObject(
                                   new JProperty("nts",  new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                   new JProperty("ocpp", new JObject(new JProperty("nodeId", "csms")))
                               )
                           );

                Assert.That(made.Certificates.Import(Encoding.ASCII.GetBytes(ca.Certificate.ExportCertificatePem()),
                                                     CertificateKind.TLSRoot, null, "The site's authority", null,
                                                     out _, out var rootError),
                            Is.True, rootError);

                String? handle = null;

                if (Identity is not null)
                {
                    Assert.That(made.Certificates.Import(Identity, CertificateKind.TLSIdentity, null, "Towards the controller", null,
                                                         out var entry, out var identityError),
                                Is.True, identityError);
                    handle = entry!.Id;
                }

                if (OneTimePass)
                    Assert.That(made.TrySetUplinkTOTP(CSMSName, TheSharedSecret, null, null, null, null, out var tokenError, out _, Bound),
                                Is.True, tokenError);

                var uplink = new JObject(
                                 new JProperty("enabled",                true),
                                 new JProperty("url",                    $"wss://127.0.0.1:{port}/{Path}"),
                                 new JProperty("securityProfile",        Identity is not null ? 3 : 2),
                                 new JProperty("reconnectInitialDelay",  1),
                                 new JProperty("reconnectMaxDelay",      2)
                             );

                if (handle is not null && !NamedByItsConnection)
                    uplink.Add("clientCertificate", handle);

                if (HTTPVersion is not null)
                    uplink.Add("httpVersion", HTTPVersion);

                Assert.That(made.TryConfigureUplink(uplink, out var uplinkError), Is.True, uplinkError);

                if (handle is not null && NamedByItsConnection)
                {
                    var line = made.Uplink()!;
                    Assert.That(made.Connections.TryUpdateConnection(line.Id, line.Description, line.URL, line.ConnectionType.ToString(), line.AutoConnect,
                                                                     line.AuthenticationId, handle,
                                                                     out var lineError),
                                Is.True, lineError);
                }

                return made;

            }

            csms = await TestPorts.StartedOnFreshPorts(Made);

            return csms;

        }

        #endregion

        #region (private) TheStationsAreOfTheSiteToo()

        /// <summary>
        /// The charging stations of the controller hold certificates of the
        /// site's authority as well: a certificate of the CSMS gets through the
        /// handshake as a station's would, and what keeps it out of the name
        /// of the CSMS is the WebSocket login of the CSMS alone.
        /// </summary>
        private void TheStationsAreOfTheSiteToo()

            => Assert.That(controller.ClientTrust.TryAdd(ca.ChainPEM(ca.Certificate), "The site's authority", out _, out _, out var trustError),
                           Is.True, trustError);

        #endregion

        #region (private) StatusOf(Id) / Log / UntilConnected(Id) / NeverConnected(Id)

        private String StatusOf(String Id)
            => controller.ConnectionStates.TryGetValue(Id, out var state)
                   ? OCPPConnections.ConnectionState.AsText(state.Status)
                   : "nothing yet";

        private String Log
            => String.Join(Environment.NewLine, said);

        private async Task<Boolean> UntilConnected(String Id)
        {

            var giveUp = DateTimeOffset.UtcNow + ThroughWithin;

            while (DateTimeOffset.UtcNow < giveUp && !(StatusOf(Id) == "connected" && csms?.CSMSConnected == true))
                await Task.Delay(100);

            return StatusOf(Id) == "connected" && csms?.CSMSConnected == true;

        }

        /// <summary>
        /// Whether the CSMS keeps trying for a few seconds and is never let in:
        /// a refusal is not one moment, the line comes back every second.
        /// </summary>
        private async Task<Boolean> NeverConnected(String? Id)
        {

            var giveUp = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6);

            while (DateTimeOffset.UtcNow < giveUp)
            {

                if (csms?.CSMSConnected == true || (Id is not null && StatusOf(Id) == "connected"))
                    return false;

                await Task.Delay(100);

            }

            return true;

        }

        #endregion


        #region ByTheKeyPinOfItsCertificate_ItIsLetIn_WhateverTheCertificateSays()

        /// <summary>
        /// Its WebSocket login pins the key of its client certificate: the key
        /// is the whole check, and the name in the certificate is not the one it
        /// comes under. Let in, as security profile 3, with no password at all.
        /// </summary>
        [Test]
        public async Task ByTheKeyPinOfItsCertificate_ItIsLetIn_WhateverTheCertificateSays()
        {

            var identity  = AnIdentity("not-the-name-it-comes-under");
            var id        = TheCSMSIsWrittenDown(KeyPins: KeyPinOf(identity));

            await ACSMSThatDials(CSMSName, Identity: identity);

            Assert.That(await UntilConnected(id), Is.True, $"{csms?.CSMSLastProblem}{Environment.NewLine}{Log}");

            Assert.Multiple(() => {
                Assert.That(Log, Does.Contain($"The back end '{CSMSName}' dialled this local controller"));
                Assert.That(Log, Does.Contain("signed in with its key pin"));
                Assert.That(Log, Does.Contain("security profile 3"));
                Assert.That(controller.StateJSON()["stations"]?["connected"]?.Value<Int32>(), Is.Zero, "the CSMS is counted among the charging stations");
            });

        }

        #endregion

        #region AnotherKey_IsNotLetIn()

        /// <summary>
        /// A certificate of the site's authority, which the stations are of as
        /// well, with another key than the one its login pins: through the
        /// handshake as a station's, and not let in under the name of the CSMS.
        /// </summary>
        [Test]
        public async Task AnotherKey_IsNotLetIn()
        {

            TheStationsAreOfTheSiteToo();

            var id = TheCSMSIsWrittenDown(KeyPins: KeyPinOf(AnIdentity(CSMSName)));

            await ACSMSThatDials(CSMSName, Identity: AnIdentity(CSMSName));

            Assert.That(await NeverConnected(id), Is.True, "a key its login does not pin was let in");

        }

        #endregion

        #region ByItsCA_IssuedToItsName_ItIsLetIn()

        /// <summary>
        /// The station port accepts the chain of its authority: issued by that
        /// authority, through a sub-CA it sends beside it, to the name the CSMS
        /// comes under. Let in, without a login, as a charging station is.
        /// </summary>
        [Test]
        public async Task ByItsCA_IssuedToItsName_ItIsLetIn()
        {

            var id = TheCSMSIsWrittenDown(ByCA: true);

            await ACSMSThatDials(CSMSName, Identity: AnIdentity(CSMSName));

            Assert.That(await UntilConnected(id), Is.True, $"{csms?.CSMSLastProblem}{Environment.NewLine}{Log}");

            Assert.That(Log, Does.Contain($"The back end '{CSMSName}' dialled this local controller").And.Contain("signed in with a certificate (security profile 3)"));

        }

        #endregion

        #region ByItsCA_IssuedToAnotherName_OrByAnotherCA_ItIsNotLetIn(IssuedTo)

        /// <summary>
        /// Issued by the right authority to another name, or by another
        /// authority to its name: not let in.
        /// </summary>
        [TestCase("csms-02", false)]
        [TestCase(CSMSName,  true)]
        public async Task ByItsCA_IssuedToAnotherName_OrByAnotherCA_ItIsNotLetIn(String IssuedTo, Boolean ByAnotherCA)
        {

            var id = TheCSMSIsWrittenDown(ByCA: true);

            TestCA? another = null;

            if (ByAnotherCA)
            {
                another = TestCA.Create($"Another CA {Guid.NewGuid()}", WithIntermediate: true);
                more.Add(another);
            }

            // Issued to another name by the site's authority, which the stations
            // are of as well: through the handshake as a station's, and kept out
            // of the name of the CSMS by the name it is issued to - at the door of
            // the station server, which takes no certificate for a path that is
            // not its common name (403), before the connection is asked.

            await ACSMSThatDials(CSMSName, Identity: AnIdentity(IssuedTo, another));

            Assert.That(await NeverConnected(id), Is.True, ByAnotherCA ? "another authority's certificate was let in" : "a certificate issued to another name was let in");

        }

        #endregion

        #region ItsCertificate_OpensNoStationsPath()

        /// <summary>
        /// The certificate the CSMS signs in with gets through the handshake by
        /// the key pin of its login. On the path of a charging station it is no
        /// station's: the key pin lets in its login, on its own path, and
        /// nobody else.
        /// </summary>
        [Test]
        public async Task ItsCertificate_OpensNoStationsPath()
        {

            var identity = AnIdentity("cs009");

            TheCSMSIsWrittenDown(KeyPins: KeyPinOf(identity));

            await ACSMSThatDials("cs009", Identity: identity);

            Assert.Multiple(async () => {
                Assert.That(await NeverConnected(null),  Is.True, "the certificate of the CSMS let a charging station in");
                Assert.That(Log,                         Does.Contain($"is the WebSocket login '{CSMSName}''s, and it asked to connect as 'cs009'"));
            });

        }

        #endregion

        #region AOneTimePasswordBoundToItsLine_LetsItInOverHTTP3()

        /// <summary>
        /// Its WebSocket login binds the one-time password to the TLS session:
        /// over HTTP/3, whose TLS exports what it is bound with, it is let in.
        /// </summary>
        [Test]
        public async Task AOneTimePasswordBoundToItsLine_LetsItInOverHTTP3()
        {

            TheControllerTakesHTTP3();

            var id = TheCSMSIsWrittenDown(BoundOneTimePass: true);

            await ACSMSThatDials(CSMSName, OneTimePass: true, Bound: true, HTTPVersion: "3");

            Assert.That(await UntilConnected(id), Is.True, $"{csms?.CSMSLastProblem}{Environment.NewLine}{Log}");

            Assert.That(Log, Does.Contain($"The back end '{CSMSName}' dialled this local controller").And.Contain("bound to its").And.Contain("(security profile 2"));

        }

        #endregion

        #region AOneTimePasswordThatIsNotBound_IsNotLetIn_WhereTheBindingIsAsked()

        /// <summary>
        /// The same CSMS, its one-time password not bound: not let in, and the
        /// log says what it has to do.
        /// </summary>
        [Test]
        public async Task AOneTimePasswordThatIsNotBound_IsNotLetIn_WhereTheBindingIsAsked()
        {

            TheControllerTakesHTTP3();

            var id = TheCSMSIsWrittenDown(BoundOneTimePass: true);

            await ACSMSThatDials(CSMSName, OneTimePass: true, Bound: false, HTTPVersion: "3");

            var never = await NeverConnected(id);

            Assert.Multiple(() => {
                Assert.That(never,  Is.True, "a one-time password that is not bound was let in");
                Assert.That(Log,    Does.Contain("has to open its WebSocket over HTTP/3 and bind its one-time password"));
            });

        }

        #endregion

        #region ABoundOneTimePassword_WhereNoHTTP3IsTaken_IsSaidToBeUnableToSignIn()

        /// <summary>
        /// Its one-time password bound to the TLS session, and the server it
        /// dials taking no WebSockets over HTTP/3: what the connection says is
        /// why it can not sign in, not that it is waited for.
        /// </summary>
        [Test]
        public void ABoundOneTimePassword_WhereNoHTTP3IsTaken_IsSaidToBeUnableToSignIn()
        {

            controller.StationServer!.AcceptHTTP3 = false;

            var id    = TheCSMSIsWrittenDown(BoundOneTimePass: true);

            SpinWait.SpinUntil(() => controller.ConnectionStates.ContainsKey(id), ThroughWithin);

            var said  = controller.ConnectionStates.TryGetValue(id, out var state) ? state.Said : "nothing";

            Assert.That(said, Does.Contain($"'{CSMSName}' can not sign in").And.Contain("takes no WebSockets over HTTP/3"));

        }

        #endregion

        #region ThroughASubCA_WhichItSendsAlong_ItIsLetIn(NamedByItsConnection)

        /// <summary>
        /// Its certificate comes from a sub-CA of its operator's authority, and
        /// the station port accepts the root of that authority alone: the CSMS
        /// sends the sub-CA beside its certificate, and the controller builds
        /// the chain with it. Whether its identity is the one its connection
        /// names, or the one the section "csms" chose.
        /// </summary>
        /// <remarks>
        /// Another authority than the site's, which issued the certificate of
        /// the station port: a sub-CA the controller's own TLS context put into
        /// the Windows store of intermediates would let a chain through that
        /// nobody sent. What a client sends along is put there on Windows as
        /// well, by .NET, and taken out again with the authority - see
        /// <see cref="TestCA"/>; there the test does not tell what was sent
        /// from what is kept, as it does on Linux.
        /// </remarks>
        [TestCase(true)]
        [TestCase(false)]
        public async Task ThroughASubCA_WhichItSendsAlong_ItIsLetIn(Boolean NamedByItsConnection)
        {

            var operatorCA = TestCA.Create($"Operator CA {Guid.NewGuid()}", WithIntermediate: true);
            more.Add(operatorCA);

            var id = TheCSMSIsWrittenDown(ByCA: true, Operator: operatorCA);

            await ACSMSThatDials(CSMSName, Identity: AnIdentity(CSMSName, operatorCA), NamedByItsConnection: NamedByItsConnection);

            Assert.That(await UntilConnected(id), Is.True, $"{csms?.CSMSLastProblem}{Environment.NewLine}{Log}");

            Assert.That(Log, Does.Contain($"The back end '{CSMSName}' dialled this local controller").And.Contain("signed in with a certificate"));

        }

        #endregion

        #region ThroughASubCA_WhichTheControllerKeeps_ItsCertificateAloneIsLetIn()

        /// <summary>
        /// The other way: the CSMS shows its certificate alone, and the station
        /// port keeps the sub-CA between it and the root with the chain it
        /// accepts. Not let in while it does not, and let in once it does -
        /// read at every handshake, without a restart.
        /// </summary>
        [Test]
        public async Task ThroughASubCA_WhichTheControllerKeeps_ItsCertificateAloneIsLetIn()
        {

            var operatorCA = TestCA.Create($"Operator CA {Guid.NewGuid()}", WithIntermediate: true);
            more.Add(operatorCA);

            var id = TheCSMSIsWrittenDown();

            await ACSMSThatDials(CSMSName, Identity: AnIdentity(CSMSName, operatorCA, WithItsSubCA: false), NamedByItsConnection: true);

            Assert.That(await NeverConnected(id), Is.True, "its certificate alone was let in, though nobody told the controller the sub-CA above it");

            Assert.That(controller.ClientTrust.TryAdd(operatorCA.Intermediate!.ExportCertificatePem() + "\n" + operatorCA.Certificate.ExportCertificatePem(),
                                                      "The operator's authority, with its issuing CA", out _, out _, out var keptError),
                        Is.True, keptError);

            Assert.That(await UntilConnected(id), Is.True, $"{csms?.CSMSLastProblem}{Environment.NewLine}{Log}");

            Assert.That(Log, Does.Contain($"The back end '{CSMSName}' dialled this local controller").And.Contain("signed in with a certificate"));

        }

        #endregion

        #region AnIssuingCAOfAnotherAuthority_OpensNothing()

        /// <summary>
        /// A sub-CA the controller keeps in its store is believed nothing by
        /// the station port: a certificate of it, of an authority the station
        /// port does not accept, is not let in, whether it is shown alone or
        /// with the sub-CA.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public async Task AnIssuingCAOfAnotherAuthority_OpensNothing(Boolean SentAlong)
        {

            var operatorCA  = TestCA.Create($"Operator CA {Guid.NewGuid()}", WithIntermediate: true);
            var another     = TestCA.Create($"Another CA {Guid.NewGuid()}",  WithIntermediate: true);
            more.Add(operatorCA);
            more.Add(another);

            var id = TheCSMSIsWrittenDown(ByCA: true, Operator: operatorCA);

            Assert.That(controller.Certificates.Import(Encoding.ASCII.GetBytes(another.Intermediate!.ExportCertificatePem()),
                                                       CertificateKind.TLSIntermediate, null, "Somebody else's issuing CA", null,
                                                       out _, out var keptError),
                        Is.True, keptError);

            await ACSMSThatDials(CSMSName, Identity: AnIdentity(CSMSName, another, WithItsSubCA: SentAlong), NamedByItsConnection: true);

            Assert.That(await NeverConnected(id), Is.True, "a certificate of an issuing CA of another authority was let in");

        }

        #endregion

    }

}
