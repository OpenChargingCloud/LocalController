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

using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// A charging station connecting over TLS, to the certificate this local
    /// controller chose out of its store.
    /// </summary>
    /// <remarks>
    /// The certificates have their own tests and the logins have theirs; what
    /// neither of them can show is that the thing chosen is the thing actually
    /// presented on the wire. Between the store and a charging station sit a
    /// chain selector, an SSL stream and a certificate context, and a store
    /// that picks perfectly into a server that presents something else is
    /// exactly the failure nobody would find until a site went dark.
    ///
    /// Built by hand rather than on the fixture base: the certificate has to be
    /// in the directory before the controller is built, because whether this
    /// port speaks TLS at all is decided when it starts.
    /// </remarks>
    public class ChargingStationTLSTests
    {

        #region Data

        private const String     ThePassword = "a-password-long-enough-for-ocpp";

        private String           directory   = default!;
        private TestCA           ca          = default!;
        private LocalController  controller  = default!;
        private UInt16           port;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void StartAControllerThatAlreadyHasACertificate()
        {

            directory = TestControllers.TemporaryDirectory("tls");
            Directory.CreateDirectory(directory);

            ca        = TestCA.Create("Test CA", WithIntermediate: true);
            port      = TestControllers.FreePort();

            #region A key and a certificate, put there before anything starts

            using (var store = new ServerCertificateStore(Path.Combine(directory, ServerCertificateStore.DefaultDirectoryName)))
            {

                Assert.That(store.TryCreateKey("127.0.0.1", [ "127.0.0.1" ], null, out _, out var csr, out var keyError),
                            Is.True, keyError);

                using var certificate = ca.Sign(csr!, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

                Assert.That(store.TryAddCertificate(ca.ChainPEM(certificate), [ "127.0.0.1" ], out _, out _, out var addError),
                            Is.True, addError);

            }

            #endregion

            controller = TestControllers.New(
                             directory,
                             new JObject(

                                 new JProperty("nts", new JObject(new JProperty("enabled", false))),

                                 new JProperty("ocppServer", new JObject(
                                     new JProperty("enabled",           true),
                                     new JProperty("address",           "127.0.0.1"),
                                     new JProperty("port",              port),
                                     new JProperty("securityProfiles",  new JArray(2)),
                                     new JProperty("reachableAs",       new JArray("127.0.0.1"))
                                 ))

                             )
                         );

            controller.StationLogins.TrySetPassword("cs001", ThePassword, null, out _, out _);

            controller.Start().GetAwaiter().GetResult();

        }

        [TearDown]
        public async Task StopIt()
        {

            if (controller is not null)
                await controller.DisposeAsync();

            ca?.Dispose();

            TestControllers.Remove(directory);

        }

        #endregion

        #region (private) Connect()

        /// <summary>
        /// One charging station, over TLS, remembering what it was shown.
        /// </summary>
        private async Task<(Boolean Connected, String? Why, X509Certificate2? Presented, Int32 ChainLength)> Connect()
        {

            using var client = new ClientWebSocket();

            X509Certificate2?  presented   = null;
            var                chainLength = 0;

            client.Options.AddSubProtocol("ocpp2.1");
            client.Options.SetRequestHeader(
                "Authorization",
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"cs001:{ThePassword}"))
            );

            // What a charging station does: check the certificate against the
            // authority it was told about, and nothing else. Recorded here so
            // that the test can say which certificate arrived, not only that
            // one did.
            client.Options.RemoteCertificateValidationCallback =
                (sender, certificate, chain, errors) => {

                    if (certificate is not null)
                        presented = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

                    chainLength = chain?.ChainElements.Count ?? 0;

                    if (chain is null || certificate is null)
                        return false;

                    using var ours = new X509Chain();

                    ours.ChainPolicy.TrustMode        = X509ChainTrustMode.CustomRootTrust;
                    ours.ChainPolicy.RevocationMode   = X509RevocationMode.NoCheck;
                    ours.ChainPolicy.CustomTrustStore.Add(ca.Certificate);

                    // Only what the server sent: the point is whether the
                    // intermediates travelled, so nothing is added from here.
                    foreach (var element in chain.ChainElements)
                        ours.ChainPolicy.ExtraStore.Add(element.Certificate);

                    return ours.Build(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));

                };

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            try
            {

                await client.ConnectAsync(new Uri($"wss://127.0.0.1:{port}"), timeout.Token);
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);

                return (true, null, presented, chainLength);

            }
            catch (Exception e)
            {
                return (false, e.Message, presented, chainLength);
            }

        }

        #endregion


        #region ThePortIsEncryptedOnceThereIsACertificate()

        [Test]
        public void ThePortIsEncryptedOnceThereIsACertificate()
        {

            Assert.Multiple(() => {
                Assert.That(controller.OCPPServerTLS,                  Is.True,
                            "A controller that found a certificate is still serving its charging stations unencrypted.");
                Assert.That(controller.ServerCertificates.HasCertificate, Is.True);
                Assert.That(controller.OCPPServerURL,                   Does.StartWith("wss://"));
            });

        }

        #endregion

        #region AStationGetsTheCertificateTheStoreChose()

        /// <summary>
        /// The whole point of the store, proved over a socket: what
        /// <see cref="ServerCertificateStore.Select"/> picked is what a charging
        /// station is shown.
        /// </summary>
        [Test]
        public async Task AStationGetsTheCertificateTheStoreChose()
        {

            var (connected, why, presented, chainLength) = await Connect();

            var chosen = controller.ServerCertificates.Entries.
                             Single(entry => entry.Id == controller.ServerCertificates.ServedId);

            Assert.Multiple(() => {

                Assert.That(connected,  Is.True, why);
                Assert.That(presented,  Is.Not.Null, "No certificate arrived at the charging station.");

                Assert.That(presented!.Thumbprint, Is.EqualTo(chosen.Certificate!.Thumbprint),
                            "The charging station was shown a different certificate than the one this controller chose.");

                // Leaf and the issuing authority: the intermediate travelled,
                // which is what lets a station that only knows the root build a
                // chain at all.
                Assert.That(chainLength, Is.GreaterThanOrEqualTo(2),
                            "The intermediate certificate was not sent, so a charging station that only knows the root cannot verify this.");

            });

        }

        #endregion

        #region AReplacementIsPresentedWithoutARestart()

        /// <summary>
        /// The reason more than one certificate lives in the store: the new one
        /// takes over on the next connection, and nothing is restarted.
        /// </summary>
        [Test]
        public async Task AReplacementIsPresentedWithoutARestart()
        {

            var (before, whyBefore, first, _) = await Connect();

            Assert.That(before, Is.True, whyBefore);

            #region A second certificate, valid from a moment ago

            Assert.That(controller.ServerCertificates.TryCreateKey("127.0.0.1", [ "127.0.0.1" ], null, out _, out var csr, out var keyError),
                        Is.True, keyError);

            using var replacement = ca.Sign(csr!, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(2));

            Assert.That(controller.ServerCertificates.TryAddCertificate(ca.ChainPEM(replacement), [ "127.0.0.1" ], out var newId, out _, out var addError),
                        Is.True, addError);

            #endregion

            var (after, whyAfter, second, _) = await Connect();

            Assert.Multiple(() => {

                Assert.That(after,   Is.True, whyAfter);
                Assert.That(second,  Is.Not.Null);

                Assert.That(second!.Thumbprint, Is.Not.EqualTo(first!.Thumbprint),
                            "The replacement did not take over; the old certificate is still being presented.");

                Assert.That(second.Thumbprint,  Is.EqualTo(replacement.Thumbprint));
                Assert.That(controller.ServerCertificates.ServedId, Is.EqualTo(newId));

            });

        }

        #endregion

        #region AStationThatDoesNotTrustTheAuthorityGetsNoFurther()

        /// <summary>
        /// The other side of the same handshake: a charging station checks, and
        /// what it does not accept it does not talk to.
        /// </summary>
        [Test]
        public async Task AStationThatDoesNotTrustTheAuthorityGetsNoFurther()
        {

            using var client = new ClientWebSocket();

            client.Options.AddSubProtocol("ocpp2.1");
            client.Options.SetRequestHeader(
                "Authorization",
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"cs001:{ThePassword}"))
            );

            // Trusts nothing, which is what a station with the wrong authority
            // configured amounts to.
            client.Options.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => false;

            using var timeout   = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var       connected = true;

            try
            {
                await client.ConnectAsync(new Uri($"wss://127.0.0.1:{port}"), timeout.Token);
            }
            catch
            {
                connected = false;
            }

            Assert.That(connected, Is.False);

        }

        #endregion

        #region AnUnencryptedStationCannotSpeakToAnEncryptedPort()

        /// <summary>
        /// A port either encrypts or it does not - so a station configured for
        /// security profile 1 against an encrypted controller does not get a
        /// quiet downgrade, it gets nothing.
        /// </summary>
        [Test]
        public async Task AnUnencryptedStationCannotSpeakToAnEncryptedPort()
        {

            using var client = new ClientWebSocket();

            client.Options.AddSubProtocol("ocpp2.1");
            client.Options.SetRequestHeader(
                "Authorization",
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"cs001:{ThePassword}"))
            );

            using var timeout   = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var       connected = true;

            try
            {
                await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), timeout.Token);
            }
            catch
            {
                connected = false;
            }

            Assert.That(connected, Is.False);

        }

        #endregion

    }

}
