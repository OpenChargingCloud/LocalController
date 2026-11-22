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
using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.Node.Certificates;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// A controller of before kept the keys its lines up to the CSMS hold up
    /// in a directory of their own, ocpp-client-keys/ beside the configuration
    /// file. They are TLS identities of its certificate store now, and what
    /// it kept is moved there at the first start that finds it.
    /// </summary>
    [TestFixture]
    public class ClientKeysOfBeforeTests
    {

        #region Data

        private String             directory   = "";
        private X509Certificate2?  ca;
        private LocalController?   controller;

        private readonly List<X509Certificate2> issued = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void SetUp()
        {

            directory = TestControllers.TemporaryDirectory("clientkeys");
            Directory.CreateDirectory(directory);

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var request = new CertificateRequest("CN=A CA of the tests", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

            ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(30));

        }

        [TearDown]
        public async Task TearDown()
        {

            if (controller is not null)
                await controller.DisposeAsync();

            foreach (var certificate in issued)
                certificate.Dispose();

            issued.Clear();
            ca?.Dispose();

            TestControllers.Remove(directory);

        }

        #endregion

        #region (private) Helpers

        /// <summary>
        /// The certificate that answers a signing request.
        /// </summary>
        private X509Certificate2 Issued(String CSR)
        {

            var request = CertificateRequest.LoadSigningRequestPem(CSR,
                                                                   HashAlgorithmName.SHA256,
                                                                   CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);

            using var caKey = ca!.GetECDsaPrivateKey()!;

            var certificate = request.Create(ca.SubjectName,
                                             X509SignatureGenerator.CreateForECDsa(caKey),
                                             DateTimeOffset.UtcNow.AddDays(-1),
                                             DateTimeOffset.UtcNow.AddDays(20),
                                             Guid.NewGuid().ToByteArray());

            issued.Add(certificate);

            return certificate;

        }

        private static String IdOf(X509Certificate2 Certificate)
            => CertificateEntry.ThumbprintOf(Certificate)[..CertificateEntry.IdLength];

        private IEnumerable<String> Identities
            => controller!.Certificates.ByKind(CertificateKind.TLSIdentity).Select(entry => entry.Id);

        private IEnumerable<String> WaitingKeys
            => controller!.Certificates.Requests.Select(request => request.Id);

        #endregion


        #region TheKeysAControllerOfBeforeKept_AreMovedIntoItsCertificateStore()

        /// <summary>
        /// A key with its certificate is an identity of the store, which a
        /// line up may show and which is not deleted from under it; a key
        /// whose request is still out waits there. Nothing is left where the
        /// keys had been, and nothing moves a second time.
        /// </summary>
        [Test]
        public async Task TheKeysAControllerOfBeforeKept_AreMovedIntoItsCertificateStore()
        {

            var former   = Path.Combine(directory, LocalController.FormerClientKeysDirectoryName);
            var madeAt   = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

            // Where keys of the kind are made, to have some as they were kept.
            var scratch  = new CertificateStore(Path.Combine(directory, "scratch"),
                                                new cloud.charging.open.protocols.WWCP.Node.Logging.EventLog(),
                                                [ CertificateKind.TLSIdentity ],
                                                RequestKinds: [ CertificateKind.TLSIdentity ]);

            String AKeyOfBefore(String Subject, Func<String, X509Certificate2?> With)
            {

                Assert.That(scratch.TryCreateRequest(CertificateKind.TLSIdentity, Subject, null, null, null, null, out var request, out var error, out _),
                            Is.True, error);

                Directory.CreateDirectory(former);

                foreach (var extension in new[] { "key.pem", "csr.pem" })
                    File.Copy(Path.Combine(scratch.Directory, CertificateStore.RequestsDirectoryName, $"{request!.Id}.{extension}"),
                              Path.Combine(former, $"{request.Id}.{extension}"));

                File.WriteAllText(Path.Combine(former, $"{request!.Id}.json"),
                                  new JObject(
                                      new JProperty("id",         request.Id),
                                      new JProperty("algorithm",  "ecdsa-p256"),
                                      new JProperty("createdAt",  madeAt.ToString("o")),
                                      new JProperty("subject",    request.Subject)
                                  ).ToString());

                if (With(request.CSR) is X509Certificate2 certificate)
                    File.WriteAllText(Path.Combine(former, $"{request.Id}.cert.pem"), certificate.ExportCertificatePem());

                return request.Id;

            }

            X509Certificate2? certificate = null;

            var shown    = AKeyOfBefore("lc001", csr => certificate = Issued(csr));
            var waiting  = AKeyOfBefore("lc002", _   => null);

            // The controller of today finds them: building it is what moves
            // them, before anything connects.
            controller = TestControllers.New(directory, TestControllers.Offline);

            var moved = IdOf(certificate!);

            Assert.That(Identities, Is.EqualTo(new[] { moved }), "the key with its certificate is no identity of the store");

            Assert.That(controller.Connections.TryAddConnection(I18NString.Create("CSMS"), URL.Parse("wss://127.0.0.1:1/lc001"), "CSMS", false, null, moved, out var connection, out var problem),
                        Is.True, problem);

            Assert.Multiple(() => {

                Assert.That(controller.Certificates.Get(moved, CertificateKind.TLSIdentity)?.HasPrivateKey,  Is.True);

                Assert.That(WaitingKeys,                                                Is.EqualTo(new[] { waiting }), "the key whose request was out does not wait in the store");
                Assert.That(controller.Certificates.Request(waiting)?.CreatedAt,        Is.EqualTo(madeAt), "when it was made was forgotten");

                Assert.That(Directory.Exists(former),                                   Is.False, "private keys were left where they had been");

                Assert.That(controller.WhatUses(moved, CertificateKind.TLSIdentity),    Does.Contain("CSMS"), "an identity a connection shows could be deleted from under it");

            });

            // And nothing is moved, or lost, the next time.
            await controller.DisposeAsync();

            controller = TestControllers.New(directory, TestControllers.Offline);

            Assert.Multiple(() => {
                Assert.That(Identities,                                                 Is.EqualTo(new[] { moved }));
                Assert.That(WaitingKeys,                                                Is.EqualTo(new[] { waiting }));
                Assert.That(controller.Connections.CopyOf(connection!)?.CertificateId,  Is.EqualTo(moved));
            });

        }

        #endregion

        #region AConnectionThatShowedAKey_ShowsTheIdentityItBecame()

        /// <summary>
        /// A connection of before names the certificate it shows by its key,
        /// which is no handle of the certificate store. The start that moves
        /// the key tells the connection what it is called now.
        /// </summary>
        [Test]
        public async Task AConnectionThatShowedAKey_ShowsTheIdentityItBecame()
        {

            var former = Path.Combine(directory, LocalController.FormerClientKeysDirectoryName);

            // A controller that has a line up, and is stopped.
            controller = TestControllers.New(directory, TestControllers.Offline);

            Assert.That(controller.Connections.TryAddConnection(I18NString.Create("CSMS"), URL.Parse("wss://127.0.0.1:1/lc001"), "CSMS", false, null, null, out var connection, out var problem),
                        Is.True, problem);

            // Its key, made as keys of the kind are, and kept as they were.
            Assert.That(controller.Certificates.TryCreateRequest(CertificateKind.TLSIdentity, "lc001", null, null, null, null, out var request, out var error, out _),
                        Is.True, error);

            var requests     = Path.Combine(controller.Certificates.Directory, CertificateStore.RequestsDirectoryName);
            var certificate  = Issued(request!.CSR);

            Directory.CreateDirectory(former);

            foreach (var extension in new[] { "key.pem", "csr.pem" })
                File.Copy(Path.Combine(requests, $"{request.Id}.{extension}"), Path.Combine(former, $"{request.Id}.{extension}"));

            File.WriteAllText(Path.Combine(former, $"{request.Id}.cert.pem"), certificate.ExportCertificatePem());

            Assert.That(controller.Certificates.TryRemoveRequest(request.Id, out error, out _), Is.True, error);

            // The connection names the key, as a controller of before wrote it.
            controller.Connections.KnownCertificateIds = () => [ request.Id ];

            Assert.That(controller.Connections.TrySetCertificate(connection!, request.Id, out problem), Is.True, problem);

            await controller.DisposeAsync();

            // The controller of today starts on what it left.
            controller = TestControllers.New(directory, TestControllers.Offline);

            Assert.Multiple(() => {
                Assert.That(Identities,                                                 Is.EqualTo(new[] { IdOf(certificate) }));
                Assert.That(WaitingKeys,                                                Is.Empty);
                Assert.That(controller.Connections.CopyOf(connection!)?.CertificateId,  Is.EqualTo(IdOf(certificate)), "the connection does not show the identity its key became");
                Assert.That(Directory.Exists(former),                                   Is.False, "private keys were left where they had been");
            });

        }

        #endregion

    }

}
