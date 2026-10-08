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

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The upload of the server certificates page, over the wire: what a text
    /// holds and which key of this controller each certificate in it is for,
    /// every certificate taken in under its key, and a private key that came
    /// along refused rather than handed back.
    /// </summary>
    public class ServerCertificateUploadTests : ALocalControllerTests
    {

        #region (helpers) Send(...) / NewKey() / AStranger() / AnIdentityWithItsKey()

        private static async Task<(HttpStatusCode Status, JObject JSON)> Send(HttpClient  HTTP,
                                                                             HttpMethod  Method,
                                                                             String      Path,
                                                                             JObject?    JSON = null)
        {

            using var request   = new HttpRequestMessage(Method, Path);

            if (JSON is not null)
                request.Content = new StringContent(JSON.ToString(), Encoding.UTF8, "application/json");

            using var response  = await HTTP.SendAsync(request);
            var text            = await response.Content.ReadAsStringAsync();

            return (response.StatusCode, text.Length > 0 ? JObject.Parse(text) : new JObject());

        }

        /// <summary>A key of this controller, and its signing request.</summary>
        private (String Id, String CSR) NewKey()
        {

            Assert.That(Controller.ServerCertificates.TryCreateKey("lc001.example.org", [ "lc001.example.org" ], null, out var id, out var csr, out var error),
                        Is.True, error);

            return (id!, csr!);

        }

        /// <summary>A certificate of nobody's key here, as PEM.</summary>
        private static String AStranger()
        {

            using var key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var cert = new CertificateRequest("CN=Somebody Else", key, HashAlgorithmName.SHA256).
                                 CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300));

            return cert.ExportCertificatePem() + "\n";

        }

        /// <summary>A certificate with its private key, as a PKCS#12 base64-encoded.</summary>
        private static String AnIdentityWithItsKey()
        {

            using var key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var cert = new CertificateRequest("CN=Brought Its Key", key, HashAlgorithmName.SHA256).
                                 CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300));

            return Convert.ToBase64String(cert.Export(X509ContentType.Pkcs12)!);

        }

        #endregion


        #region TheUploadSaysWhichKeyEachCertificateIsFor()

        [Test]
        public async Task TheUploadSaysWhichKeyEachCertificateIsFor()
        {

            using var ca    = TestCA.Create("Upload CA");
            using var http  = await SignedIn();

            var (one, oneCSR)  = NewKey();
            var (two, twoCSR)  = NewKey();

            using var forOne   = ca.Sign(oneCSR, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300));
            using var forTwo   = ca.Sign(twoCSR, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300));

            var text           = TestCA.ToPEM(forTwo) + AStranger() + TestCA.ToPEM(forOne);

            var (status, said) = await Send(http, HttpMethod.Post, "api/v1/configuration/ocpp-server/certificates/inspect", new JObject(new JProperty("pem", text)));

            var found          = said["certificates"]!.Children<JObject>().ToArray();

            Assert.Multiple(() => {
                Assert.That(status,                                      Is.EqualTo(HttpStatusCode.OK), said.ToString());
                Assert.That(found.Select(one => one.Value<String>("keyId")), Is.EquivalentTo(new[] { one, two, null }));
                Assert.That(found.Single(one => one.Value<String>("keyId") is null).Value<String>("refusal"),
                            Does.StartWith("It belongs to no key of this local controller."));
                Assert.That(Controller.ServerCertificates.ToJSON()["entries"]!.Children<JObject>().All(entry => entry.Value<Boolean>("hasCertificate") == false),
                            Is.True, "something was taken in by looking");
            });

        }

        #endregion

        #region EveryCertificateIsTakenInUnderItsKey()

        [Test]
        public async Task EveryCertificateIsTakenInUnderItsKey()
        {

            using var ca    = TestCA.Create("Upload CA");
            using var http  = await SignedIn();

            var (one, oneCSR)  = NewKey();
            var (two, twoCSR)  = NewKey();

            using var forOne   = ca.Sign(oneCSR, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300));
            using var forTwo   = ca.Sign(twoCSR, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300));

            var (status, said) = await Send(http, HttpMethod.Post, "api/v1/configuration/ocpp-server/certificates/upload", new JObject(
                                                new JProperty("pem", TestCA.ToPEM(forTwo) + AStranger() + TestCA.ToPEM(forOne))
                                            ));

            var withCertificates = Controller.ServerCertificates.ToJSON()["entries"]!.Children<JObject>().
                                       Where (entry => entry.Value<Boolean>("hasCertificate")).
                                       Select(entry => entry.Value<String>("id")).
                                       ToArray();

            Assert.Multiple(() => {
                Assert.That(status,                                               Is.EqualTo(HttpStatusCode.OK), said.ToString());
                Assert.That(said["taken"]!.Select(taken => taken.Value<String>("id")), Is.EquivalentTo(new[] { one, two }));
                Assert.That(said["refused"]!.Select(refused => refused.Value<String>("label")), Is.EqualTo(new[] { "Somebody Else" }));
                Assert.That(withCertificates,                                     Is.EquivalentTo(new[] { one, two }));
            });

        }

        #endregion

        #region AKeyThatCameAlongIsRefusedAndNotHandedBack()

        [Test]
        public async Task AKeyThatCameAlongIsRefusedAndNotHandedBack()
        {

            using var http         = await SignedIn();

            var identity           = AnIdentityWithItsKey();

            var (looked,  found)   = await Send(http, HttpMethod.Post, "api/v1/configuration/ocpp-server/certificates/inspect", new JObject(new JProperty("content", identity)));
            var (taken,   refused) = await Send(http, HttpMethod.Post, "api/v1/configuration/ocpp-server/certificates/upload",  new JObject(new JProperty("content", identity)));

            Assert.Multiple(() => {

                Assert.That(looked,                                              Is.EqualTo(HttpStatusCode.OK), found.ToString());
                Assert.That(found["pem"]!.Value<String>(),                       Does.Contain("BEGIN CERTIFICATE").And.Not.Contain("PRIVATE KEY"),
                            "a key that came along was handed back");
                Assert.That(found["certificates"]![0]!.Value<Boolean>("hasPrivateKey"), Is.True);
                Assert.That(found["certificates"]![0]!.Value<String>("refusal"), Does.StartWith("That certificate came with its private key, and none arrives here"));

                Assert.That(taken,                                               Is.EqualTo(HttpStatusCode.BadRequest), refused.ToString());
                Assert.That(refused.Value<String>("error"),                      Does.StartWith("That certificate came with its private key"));

            });

        }

        #endregion

    }

}
