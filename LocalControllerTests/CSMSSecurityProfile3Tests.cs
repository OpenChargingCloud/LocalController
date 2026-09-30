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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    /// Security profile 3 up to the CSMS: a local controller that signs in with
    /// a TLS identity of its own certificate store, and believes the CSMS by a
    /// TLS root of that store - against a second local controller standing in
    /// for the CSMS, over TLS, letting in only what its accepted chains vouch
    /// for.
    /// </summary>
    /// <remarks>
    /// One authority for all of it, as a site's own PKI would be: it issued the
    /// CSMS's server certificate and the controller's identity, and neither
    /// end's machine trusts it. So the CSMS is believed only through the root
    /// in the store, and the controller let in only by the chain the CSMS was
    /// told to accept.
    /// </remarks>
    public class CSMSSecurityProfile3Tests
    {

        #region Data

        /// <summary>
        /// How long the controller below may take to get through.
        /// </summary>
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
        /// A controller standing in for the CSMS: TLS with a certificate of the
        /// test's authority, security profile 3 only, and that authority as the
        /// one chain it accepts.
        /// </summary>
        [SetUp]
        public async Task StartACSMSThatWantsACertificate()
        {

            // A name of its own per run - see ChargingStationTLSTests for the
            // machine-wide cache that makes that matter.
            ca                 = TestCA.Create($"Site CA {Guid.NewGuid()}", WithIntermediate: true);
            csmsPort           = TestPorts.Free();

            upstreamDirectory  = TestControllers.TemporaryDirectory("csms-p3-upstream");
            Directory.CreateDirectory(upstreamDirectory);

            using (var store = new ServerCertificateStore(Path.Combine(upstreamDirectory, ServerCertificateStore.DefaultDirectoryName)))
            {

                Assert.That(store.TryCreateKey("127.0.0.1", [ "127.0.0.1" ], null, out _, out var csr, out var keyError),
                            Is.True, keyError);

                using var certificate = ca.Sign(csr!, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

                Assert.That(store.TryAddCertificate(ca.ChainPEM(certificate), [ "127.0.0.1" ], out _, out _, out var addError),
                            Is.True, addError);

            }

            upstream = TestControllers.New(
                           upstreamDirectory,
                           new JObject(
                               new JProperty("nts", new JObject(new JProperty("enabled", false))),
                               new JProperty("ocppServer", new JObject(
                                   new JProperty("enabled",           true),
                                   new JProperty("address",           "127.0.0.1"),
                                   new JProperty("port",              csmsPort),
                                   new JProperty("securityProfiles",  new JArray(3)),
                                   new JProperty("reachableAs",       new JArray("127.0.0.1")),
                                   new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1"))
                               ))
                           )
                       );

            Assert.That(upstream.ClientTrust.TryAdd(ca.ChainPEM(ca.Certificate), "The site's authority", out _, out _, out var trustError),
                        Is.True, trustError);

            await upstream.Start();

            downstreamDirectory = TestControllers.TemporaryDirectory("csms-p3-downstream");
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


        #region (private) AnIdentity(Subject)

        /// <summary>
        /// A TLS identity the site's authority issued, with its private key and
        /// the sub-CA it was issued by, as the PKCS#12 an import takes.
        /// </summary>
        private Byte[] AnIdentity(String Subject)
        {

            using var key       = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request         = new CertificateRequest($"CN={Subject}", key, HashAlgorithmName.SHA256);

            using var issued    = ca.Sign(request.CreateSigningRequestPem(),
                                          DateTimeOffset.UtcNow.AddDays(-1),
                                          DateTimeOffset.UtcNow.AddYears(1),
                                          ClientAuthentication: true);

            using var withKey   = issued.CopyWithPrivateKey(key);

            var collection      = new X509Certificate2Collection { withKey };

            if (ca.Intermediate is not null)
                collection.Add(ca.Intermediate);

            return collection.Export(X509ContentType.Pkcs12)!;

        }

        #endregion

        #region (private) AControllerThatDials(Identity, Root, Configure)

        /// <summary>
        /// The controller below, set to dial the one above with security profile
        /// 3: built, its store given what the test says, then started.
        /// </summary>
        /// <param name="Identity">Whether its store holds an identity the site's authority issued, chosen for the CSMS.</param>
        /// <param name="Root">Whether its store holds the site's authority as a TLS root for every use.</param>
        /// <param name="Chosen">What it names as its identity, where that is not the one it holds.</param>
        private async Task<LocalController> AControllerThatDials(Boolean                     Identity   = true,
                                                                 Boolean                     Root       = true,
                                                                 String?                     Chosen     = null,
                                                                 Action<LocalController>?    Also       = null)
        {

            var controller = TestControllers.New(
                                 downstreamDirectory,
                                 new JObject(
                                     new JProperty("nts",  new JObject(new JProperty("enabled", false))),
                                     new JProperty("ocpp", new JObject(new JProperty("nodeId", "lc002")))
                                 )
                             );

            String? handle = null;

            if (Identity)
            {
                Assert.That(controller.Certificates.Import(AnIdentity("lc002"), CertificateKind.TLSIdentity, null, "Towards the CSMS", null,
                                                           out var entry, out var identityError),
                            Is.True, identityError);
                handle = entry!.Id;
            }

            if (Root)
                Assert.That(controller.Certificates.Import(Encoding.ASCII.GetBytes(ca.Certificate.ExportCertificatePem()),
                                                           CertificateKind.TLSRoot, null, "The site's authority", null,
                                                           out _, out var rootError),
                            Is.True, rootError);

            Also?.Invoke(controller);

            // The controller's node id as the last segment of the path, as
            // OCPP has a charging station dial its CSMS. With a password it
            // says who it is by its user name; with a certificate alone,
            // Hermod does not hand the certificate on to the request the CSMS
            // reads the node id from, and the path is what is left.
            var csms = new JObject(
                           new JProperty("enabled",                true),
                           new JProperty("url",                    $"wss://127.0.0.1:{csmsPort}/lc002"),
                           new JProperty("securityProfile",        3),
                           new JProperty("reconnectInitialDelay",  1),
                           new JProperty("reconnectMaxDelay",      2)
                       );

            if ((Chosen ?? handle) is String chosen)
                csms.Add("clientCertificate", chosen);

            Assert.That(controller.TryUpdateCSMSConfiguration(csms, out var csmsError), Is.True, csmsError);

            downstream = controller;

            await controller.Start();

            return controller;

        }

        #endregion

        #region (private) UntilItIsThrough(Controller)

        /// <summary>
        /// Wait until the CSMS has the controller's connection and the
        /// controller says so too, or until ThroughWithin is up.
        /// </summary>
        private async Task UntilItIsThrough(LocalController Controller)
        {

            var giveUp = DateTimeOffset.UtcNow + ThroughWithin;

            while (DateTimeOffset.UtcNow < giveUp &&
                   !(upstream.StationServer?.WebSocketConnections.Any() == true && Controller.CSMSConnected))
            {
                await Task.Delay(100);
            }

        }

        #endregion

        #region (private) NoConnectionStays()

        /// <summary>
        /// Whether the CSMS is left with no connection, within a few seconds.
        /// </summary>
        /// <remarks>
        /// Not asked at one moment: Hermod's WebSocket server lists a connection
        /// as soon as its TLS handshake is through, before the HTTP upgrade, and
        /// a client that judges the server's certificate once the handshake is
        /// through, as SslStream does, leaves a connection in that list for the
        /// moment it takes to close it. Asked at that moment on a busy Windows
        /// CI machine, the list was not empty, and the test red. A connection
        /// the controller had let through would stay, and is what this is about.
        /// </remarks>
        private async Task<Boolean> NoConnectionStays()
        {

            var giveUp = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);

            while (upstream.StationServer?.WebSocketConnections.Any() == true)
            {

                if (DateTimeOffset.UtcNow > giveUp)
                    return false;

                await Task.Delay(50);

            }

            return true;

        }

        #endregion


        #region AControllerSignsInWithItsOwnIdentityAndBelievesTheCSMSByItsStore()

        /// <summary>
        /// Neither machine trusts the site's authority, so the controller gets
        /// through only by what its store holds: the root to believe the CSMS
        /// by, and the identity it is let in with.
        /// </summary>
        [Test]
        public async Task AControllerSignsInWithItsOwnIdentityAndBelievesTheCSMSByItsStore()
        {

            var controller = await AControllerThatDials();

            await UntilItIsThrough(controller);

            // And stays through: a CSMS that could not tell which node it
            // was let in closed the line again at once, which came back and
            // was closed again, every second.
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.Multiple(() => {

                Assert.That(controller.CSMSConnected,                                   Is.True, controller.CSMSLastProblem);
                Assert.That(upstream.StationServer?.WebSocketConnections.Count(),       Is.EqualTo(1));

                Assert.That(controller.Log.Recent(200).Any(line => line.Message.Contains("was lost")),
                            Is.False,
                            "The line went down again after it was up.");

                Assert.That(controller.Log.Recent(200).Any(line => line.Message.Contains("signed in to the CSMS") &&
                                                                   line.Message.Contains("with its certificate, security profile 3")),
                            Is.True,
                            "It does not say how it signed in.");

                Assert.That(upstream.Log.Recent(200).Any(line => line.Message.Contains("'lc002' signed in") &&
                                                                 line.Message.Contains("with a certificate (security profile 3)")),
                            Is.True,
                            "The CSMS did not let it in by its certificate.");

                Assert.That(controller.KnownServers.Get(LocalController.CSMSService, "127.0.0.1"), Is.Not.Null,
                            "The CSMS is not remembered by what it was believed with, so another certificate would go unnoticed.");

            });

        }

        #endregion

        #region AControllerThatDoesNotBelieveTheCSMSSaysWhy()

        /// <summary>
        /// Without the site's authority in its store, the CSMS's certificate
        /// chains to nothing this controller trusts - which is what it says,
        /// rather than that the CSMS could not be reached.
        /// </summary>
        [Test]
        public async Task AControllerThatDoesNotBelieveTheCSMSSaysWhy()
        {

            var controller = await AControllerThatDials(Root: false);
            var noneStays  = await NoConnectionStays();

            Assert.Multiple(() => {
                Assert.That(controller.CSMSConnected,           Is.False);
                Assert.That(controller.CSMSLastProblem,         Does.Contain("showed a certificate this local controller does not believe").
                                                                And.Contain("chains to no root that is trusted"));
                Assert.That(noneStays,                          Is.True, "the CSMS kept a connection of a controller that does not believe it");
            });

        }

        #endregion

        #region ProfileThreeWithoutAnIdentityIsSaidAndNotDialled()

        [Test]
        public async Task ProfileThreeWithoutAnIdentityIsSaidAndNotDialled()
        {

            var controller = await AControllerThatDials(Identity: false);

            Assert.Multiple(() => {
                Assert.That(controller.CSMSConnected,      Is.False);
                Assert.That(controller.CSMSLastProblem,    Does.Contain("none is chosen"));
                Assert.That(controller.Node.OCPPWebSocketClients.Any(), Is.False, "It was dialled without an identity.");
            });

        }

        #endregion

        #region AnIdentityThatIsSwitchedOffIsSaidAndNotDialled()

        [Test]
        public async Task AnIdentityThatIsSwitchedOffIsSaidAndNotDialled()
        {

            var controller = await AControllerThatDials(
                                 Also: dialling => {
                                     var identity = dialling.Certificates.ByKind(CertificateKind.TLSIdentity).Single();
                                     Assert.That(dialling.Certificates.SetActive(identity.Id, false, out _, out var error), Is.True, error);
                                 }
                             );

            Assert.Multiple(() => {
                Assert.That(controller.CSMSConnected,      Is.False);
                Assert.That(controller.CSMSLastProblem,    Does.Contain("'Towards the CSMS'").And.Contain("is switched off"));
                Assert.That(controller.Node.OCPPWebSocketClients.Any(), Is.False, "It was dialled with an identity nobody may use.");
            });

        }

        #endregion

        #region WhatIsNotAnIdentityIsSaidAndNotDialled()

        [Test]
        public async Task WhatIsNotAnIdentityIsSaidAndNotDialled()
        {

            var root       = CertificateEntry.ThumbprintOf(ca.Certificate)[..16];

            var controller = await AControllerThatDials(Identity: false, Chosen: root);

            // What it is said to be instead is its kind with its article: it
            // was the whole of Describe() after an "a" - "but a TLS root -
            // what a server this node connects to may chain to: a time
            // server, a backend."
            Assert.Multiple(() => {
                Assert.That(controller.CSMSConnected,      Is.False);
                Assert.That(controller.CSMSLastProblem,    Does.Contain("is not a TLS identity").And.
                                                           EndWith("is not a TLS identity this local controller could sign in with, but a TLS root."));
            });

        }

        #endregion

    }

}
