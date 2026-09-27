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
    /// This local controller's own certificate store, over the wire: the kinds
    /// of TLS it keeps and none of a vehicle's, what a TLS root is for, an
    /// identity that comes with its key and one that does not, and a file
    /// copied into the directory by hand.
    /// </summary>
    /// <remarks>
    /// The vehicle's store and the vehicle's routes, over the node's store; what
    /// is asked here is that a local controller has them, and keeps the kinds
    /// it has use for.
    /// </remarks>
    public class CertificateStoreTests : ALocalControllerTests
    {

        #region (helpers) RootPem(Name) / IdentityP12(Name) / Send(HTTP, Method, Path, JSON)

        /// <summary>
        /// A self-signed root, as the text of a PEM file base64-encoded - which
        /// is what an upload from the browser turns into.
        /// </summary>
        private static String RootPem(String Name)
        {

            using var key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request    = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

            using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

            return Convert.ToBase64String(Encoding.ASCII.GetBytes(root.ExportCertificatePem()));

        }

        /// <summary>
        /// A certificate this controller could present, with its private key,
        /// as an unprotected PKCS#12 base64-encoded - or, without the key, as
        /// the certificate alone.
        /// </summary>
        private static String Identity(String Name, Boolean WithKey = true)
        {

            using var key      = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request        = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.2") ], false));

            using var identity = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

            return WithKey
                       ? Convert.ToBase64String(identity.Export(X509ContentType.Pkcs12))
                       : Convert.ToBase64String(Encoding.ASCII.GetBytes(identity.ExportCertificatePem()));

        }

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

        #endregion


        #region TheStoreKeepsTheKindsOfTLSAndNoneOfAVehicle()

        [Test]
        public async Task TheStoreKeepsTheKindsOfTLSAndNoneOfAVehicle()
        {

            using var http  = await SignedIn();

            var (status, store) = await Send(http, HttpMethod.Get, "api/v1/certificates");

            Assert.Multiple(() => {

                Assert.That(status,                                          Is.EqualTo(HttpStatusCode.OK), store.ToString());

                Assert.That(store["trustAnchors"]!.Values<String>(),         Is.EqualTo(new[] { "tlsRoot" }),
                            "the roots a server it connects to may chain to");
                Assert.That(store["credentials"]!.Values<String>(),          Is.EqualTo(new[] { "tlsIdentity" }),
                            "what it presents in TLS itself");
                Assert.That(store["recognised"]!.Values<String>(),           Is.EqualTo(new[] { "tlsServer" }),
                            "a server certificate is recognised, neither believed nor presented");

                Assert.That(((JObject) store["kinds"]!).Properties().Select(kind => kind.Name),
                            Is.EquivalentTo(new[] { "tlsRoot", "tlsServer", "tlsIdentity" }),
                            "none of ISO 15118's, which are a vehicle's");

                Assert.That(store["usages"]!.Values<String>(),               Is.EqualTo(new[] { "dns", "nts" }), "what a page may offer");
                Assert.That(store["keysAreUnencrypted"]!.Value<Boolean>(),   Is.False);

                Assert.That(Path.GetFullPath(store["directory"]!.Value<String>()!),
                            Is.EqualTo(Path.GetFullPath(Path.Combine(Directory, "certificates"))),
                            "beside the configuration file, which is what it is measured from");

            });

        }

        #endregion

        #region ARootIsUploadedForTheUsesItIsFor()

        [Test]
        public async Task ARootIsUploadedForTheUsesItIsFor()
        {

            using var http  = await SignedIn();

            var (created, entry) = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                  new JProperty("kind",     "tlsRoot"),
                                                  new JProperty("content",  RootPem("Our Clocks' Root")),
                                                  new JProperty("usages",   new JArray("nts"))
                                              ));

            var (again, _)       = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                  new JProperty("kind",     "tlsRoot"),
                                                  new JProperty("content",  RootPem("Our Clocks' Other Root")),
                                                  new JProperty("usages",   new JArray("nts"))
                                              ));

            var (_, store)       = await Send(http, HttpMethod.Get, "api/v1/certificates");

            Assert.Multiple(() => {
                Assert.That(created,                                                               Is.EqualTo(HttpStatusCode.Created), entry.ToString());
                Assert.That(again,                                                                 Is.EqualTo(HttpStatusCode.Created));
                Assert.That(entry["usages"]!.Values<String>(),                                     Is.EqualTo(new[] { "nts" }));
                Assert.That(entry["label"]!.Value<String>(),                                       Is.EqualTo("Our Clocks' Root"), "its common name, where no label was given");
                Assert.That(store["certificates"]!["tlsRoot"]!.Children().Count(),                  Is.EqualTo(2));
                Assert.That(store["certificates"]!["tlsRoot"]![0]!["usages"]!.Values<String>(),    Is.EqualTo(new[] { "nts" }));
            });

        }

        #endregion

        #region WhatARootIsForIsChangedAndTakenBackToEveryUse()

        [Test]
        public async Task WhatARootIsForIsChangedAndTakenBackToEveryUse()
        {

            using var http  = await SignedIn();

            var (_, entry)        = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                   new JProperty("kind",     "tlsRoot"),
                                                   new JProperty("content",  RootPem("Our Resolvers' Root")),
                                                   new JProperty("usages",   new JArray("dns"))
                                               ));

            var path              = $"api/v1/certificates/{entry["id"]}";

            var (both,  forBoth)  = await Send(http, HttpMethod.Patch, path, new JObject(new JProperty("usages", new JArray("nts", "dns"))));
            var (label, relabel)  = await Send(http, HttpMethod.Patch, path, new JObject(new JProperty("label",  "Our Root")));
            var (every, forAll)   = await Send(http, HttpMethod.Patch, path, new JObject(new JProperty("usages", JValue.CreateNull())));

            Assert.Multiple(() => {
                Assert.That(both,                                      Is.EqualTo(HttpStatusCode.OK), forBoth.ToString());
                Assert.That(forBoth["usages"]!.Values<String>(),       Is.EqualTo(new[] { "dns", "nts" }));
                Assert.That(label,                                     Is.EqualTo(HttpStatusCode.OK), relabel.ToString());
                Assert.That(relabel["label"]!.Value<String>(),         Is.EqualTo("Our Root"));
                Assert.That(relabel["usages"]!.Values<String>(),       Is.EqualTo(new[] { "dns", "nts" }), "a PATCH without them leaves them alone");
                Assert.That(every,                                     Is.EqualTo(HttpStatusCode.OK), forAll.ToString());
                Assert.That(forAll["usages"]!.Type,                    Is.EqualTo(JTokenType.Null),    "null is every use again");
                Assert.That(Controller.Log.Recent(200, Tag: "security").Any(line => line.Message.Contains("is now for every use")),
                            Is.True,
                            "a change of what a root vouches for is a matter of security, and said as one");
            });

        }

        #endregion

        #region WhatIsNotAUsageOrAKindOfThisStoreIsRefusedWhereItIsTyped()

        [Test]
        public async Task WhatIsNotAUsageOrAKindOfThisStoreIsRefusedWhereItIsTyped()
        {

            using var http  = await SignedIn();

            var (unknown, said)      = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                      new JProperty("kind",     "tlsRoot"),
                                                      new JProperty("content",  RootPem("Some Root")),
                                                      new JProperty("usages",   new JArray("ntp"))
                                                  ));

            var (vehicles, kindSaid) = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                      new JProperty("kind",     "v2gRoot"),
                                                      new JProperty("content",  RootPem("A V2G Root"))
                                                  ));

            var (notAList, listSaid) = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                      new JProperty("kind",     "tlsRoot"),
                                                      new JProperty("content",  RootPem("Another Root")),
                                                      new JProperty("usages",   "dns")
                                                  ));

            var (_, store)           = await Send(http, HttpMethod.Get, "api/v1/certificates");

            Assert.Multiple(() => {
                Assert.That(unknown,                       Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(said.ToString(),               Does.Contain("'ntp' is not a usage").And.Contain("dns, nts"));
                Assert.That(vehicles,                      Is.EqualTo(HttpStatusCode.BadRequest), "a vehicle's kind, which this store does not keep");
                Assert.That(kindSaid.ToString(),           Does.Contain("tlsRoot, tlsServer, tlsIdentity"));
                Assert.That(notAList,                      Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(listSaid.ToString(),           Does.Contain("has to be a list of usages"));
                Assert.That(store["certificates"]!.Values().SelectMany(kind => kind.Children()).Any(),
                            Is.False,
                            "nothing refused was half-imported");
            });

        }

        #endregion

        #region AnIdentityComesWithItsKeyAndIsSwitchedOffRenamedAndDeleted()

        [Test]
        public async Task AnIdentityComesWithItsKeyAndIsSwitchedOffRenamedAndDeleted()
        {

            using var http  = await SignedIn();

            var (created, entry)  = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                   new JProperty("kind",     "tlsIdentity"),
                                                   new JProperty("content",  Identity("lc-001")),
                                                   new JProperty("label",    "Towards the CSMS")
                                               ));

            var path              = $"api/v1/certificates/{entry["id"]}";
            var file              = Path.Combine(Directory, "certificates", entry["fileName"]!.Value<String>()!);

            var (_, withKey)      = await Send(http, HttpMethod.Get, "api/v1/certificates");
            var (off, switched)   = await Send(http, HttpMethod.Patch, path, new JObject(new JProperty("active", false)));
            var (_, one)          = await Send(http, HttpMethod.Get,   path);
            var wasThere          = File.Exists(file);
            var (gone, after)     = await Send(http, HttpMethod.Delete, path);
            var (missing, _)      = await Send(http, HttpMethod.Get,   path);

            Assert.Multiple(() => {

                Assert.That(created,                                         Is.EqualTo(HttpStatusCode.Created), entry.ToString());
                Assert.That(entry["hasPrivateKey"]!.Value<Boolean>(),        Is.True);
                Assert.That(entry["label"]!.Value<String>(),                 Is.EqualTo("Towards the CSMS"));
                Assert.That(withKey["keysAreUnencrypted"]!.Value<Boolean>(), Is.True, "said on the page, where somebody looks at the consequences");
                Assert.That(entry.ToString(),                                Does.Not.Contain("PRIVATE KEY"), "the key is not in the answer");

                Assert.That(off,                                             Is.EqualTo(HttpStatusCode.OK), switched.ToString());
                Assert.That(one["active"]!.Value<Boolean>(),                 Is.False);
                Assert.That(one["usable"]!.Value<Boolean>(),                 Is.False, "switched off is not usable, whatever its dates say");

                Assert.That(wasThere,                                        Is.True, $"the store keeps it as a file of its own: {file}");
                Assert.That(gone,                                            Is.EqualTo(HttpStatusCode.OK), after.ToString());
                Assert.That(File.Exists(file),                               Is.False, "its file goes with it");
                Assert.That(missing,                                         Is.EqualTo(HttpStatusCode.NotFound));

            });

        }

        #endregion

        #region TheIdentityTheCSMSSignsInWithIsNotDeletedUnderIt()

        /// <summary>
        /// Chosen for the CSMS connection, an identity is marked on the store's
        /// page, described on the CSMS page, and not deleted until something
        /// else is chosen - switched off it may be, and the CSMS page says so.
        /// </summary>
        [Test]
        public async Task TheIdentityTheCSMSSignsInWithIsNotDeletedUnderIt()
        {

            using var http  = await SignedIn();

            var (_, entry)        = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                   new JProperty("kind",     "tlsIdentity"),
                                                   new JProperty("content",  Identity("lc-003")),
                                                   new JProperty("label",    "Towards the CSMS")
                                               ));

            var id                = entry["id"]!.Value<String>()!;
            var path              = $"api/v1/certificates/{id}";

            Assert.That(Controller.TryUpdateCSMSConfiguration(new JObject(new JProperty("clientCertificate", id)), out var chooseError),
                        Is.True, chooseError);

            var (_, store)        = await Send(http, HttpMethod.Get,    "api/v1/certificates");
            var (_, csms)         = await Send(http, HttpMethod.Get,    "api/v1/configuration/csms");
            var (refused, said)   = await Send(http, HttpMethod.Delete, path);

            var (_, _)            = await Send(http, HttpMethod.Patch,  path, new JObject(new JProperty("active", false)));
            var (_, switchedOff)  = await Send(http, HttpMethod.Get,    "api/v1/configuration/csms");

            Assert.That(Controller.TryUpdateCSMSConfiguration(new JObject(new JProperty("clientCertificate", JValue.CreateNull())), out var noneError),
                        Is.True, noneError);

            var (deleted, _)      = await Send(http, HttpMethod.Delete, path);
            var (_, missing)      = await Send(http, HttpMethod.Get,    "api/v1/configuration/csms");

            Assert.Multiple(() => {

                Assert.That(store["chosen"]!["csmsClientCertificate"]!.Value<String>(),  Is.EqualTo(id), "the store's page marks it");

                Assert.That(csms["clientCertificateIs"]!["label"]!.Value<String>(),      Is.EqualTo("Towards the CSMS"));
                Assert.That(csms["clientCertificateIs"]!["usable"]!.Value<Boolean>(),    Is.True);

                Assert.That(refused,                                                     Is.EqualTo(HttpStatusCode.Conflict), said.ToString());
                Assert.That(said.ToString(),                                             Does.Contain("'csms.clientCertificate'"));

                Assert.That(switchedOff["clientCertificateIs"]!["usable"]!.Value<Boolean>(), Is.False, "the CSMS page says it is switched off");

                Assert.That(deleted,                                                     Is.EqualTo(HttpStatusCode.OK), "with none chosen, it goes");
                Assert.That(missing["clientCertificateIs"],                              Is.Null, "none is chosen, so none is described");

            });

        }

        #endregion

        #region AnIdentityWithoutItsKeyIsRefused()

        [Test]
        public async Task AnIdentityWithoutItsKeyIsRefused()
        {

            using var http  = await SignedIn();

            var (status, said) = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                new JProperty("kind",     "tlsIdentity"),
                                                new JProperty("content",  Identity("lc-002", WithKey: false))
                                            ));

            var (_, store)     = await Send(http, HttpMethod.Get, "api/v1/certificates");

            Assert.Multiple(() => {
                Assert.That(status,                                                     Is.EqualTo(HttpStatusCode.BadRequest), said.ToString());
                Assert.That(store["certificates"]!["tlsIdentity"]!.Children().Any(),    Is.False,
                            "something this controller cannot present with is not an identity of its own");
            });

        }

        #endregion

        #region ACertificateCopiedIntoTheDirectoryIsAdoptedWhenItIsReadAgain()

        [Test]
        public async Task ACertificateCopiedIntoTheDirectoryIsAdoptedWhenItIsReadAgain()
        {

            using var http  = await SignedIn();

            var (_, before)    = await Send(http, HttpMethod.Get, "api/v1/certificates");

            var roots          = Path.Combine(Directory, "certificates", "roots", "tls");
            System.IO.Directory.CreateDirectory(roots);
            File.WriteAllBytes(Path.Combine(roots, "copied-by-hand.pem"), Convert.FromBase64String(RootPem("Copied By Hand")));

            var (status, after) = await Send(http, HttpMethod.Post, "api/v1/certificates/reload", new JObject());

            Assert.Multiple(() => {
                Assert.That(before["certificates"]!["tlsRoot"]!.Children().Any(),      Is.False);
                Assert.That(status,                                                    Is.EqualTo(HttpStatusCode.OK), after.ToString());
                Assert.That(after["certificates"]!["tlsRoot"]!.Select(entry => entry["label"]!.Value<String>()),
                            Is.EqualTo(new[] { "Copied By Hand" }));
            });

        }

        #endregion

    }

}
