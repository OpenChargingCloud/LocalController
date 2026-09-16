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

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// A certificate authority for the duration of one test: it answers signing
    /// requests, and it can be made to answer them for any day at all.
    /// </summary>
    /// <remarks>
    /// The whole point of the certificate store is what it does with dates -
    /// one certificate taking over from another, one that is not valid yet, one
    /// that has run out. None of that can be tested against a real authority,
    /// which will only ever issue certificates valid from today.
    ///
    /// Everything here is thrown away with the test. Nothing is written
    /// anywhere a real certificate would be looked for, and none of these keys
    /// leaves the process.
    /// </remarks>
    internal sealed class TestCA : IDisposable
    {

        #region Properties

        /// <summary>
        /// The certificate of this authority, which is what a controller would
        /// be told to trust.
        /// </summary>
        public X509Certificate2  Certificate    { get; }

        /// <summary>
        /// The issuing certificate between the root and what it signs, when
        /// this authority was made with one.
        /// </summary>
        public X509Certificate2? Intermediate   { get; }

        /// <summary>
        /// What is actually used for signing: the intermediate where there is
        /// one, the root otherwise.
        /// </summary>
        public X509Certificate2  Issuer
            => Intermediate ?? Certificate;

        #endregion

        #region Constructor(s)

        private TestCA(X509Certificate2   Certificate,
                       X509Certificate2?  Intermediate)
        {
            this.Certificate   = Certificate;
            this.Intermediate  = Intermediate;
        }

        #endregion


        #region (static) Create(Name, NotBefore = null, NotAfter = null, WithIntermediate = false)

        /// <summary>
        /// A new authority, valid over the given period.
        /// </summary>
        public static TestCA Create(String           Name,
                                    DateTimeOffset?  NotBefore          = null,
                                    DateTimeOffset?  NotAfter           = null,
                                    Boolean          WithIntermediate   = false)
        {

            // Wide on purpose, and not a year either side of today: an
            // authority cannot sign a certificate reaching outside its own
            // validity, and these tests deliberately issue certificates for
            // days long past and far ahead. A narrow authority would turn every
            // one of those tests into an error about the authority.
            var notBefore  = NotBefore ?? new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var notAfter   = NotAfter  ?? new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var rootRequest   = new CertificateRequest($"CN={Name}", rootKey, HashAlgorithmName.SHA256);

            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));

            var root = rootRequest.CreateSelfSigned(notBefore, notAfter);

            if (!WithIntermediate)
                return new TestCA(Reload(root), null);

            using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var intermediateRequest   = new CertificateRequest($"CN={Name} Issuing CA", intermediateKey, HashAlgorithmName.SHA256);

            intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            intermediateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));

            var intermediate = intermediateRequest.Create(
                                   root,
                                   notBefore,
                                   notAfter,
                                   RandomNumberGenerator.GetBytes(16)
                               ).CopyWithPrivateKey(intermediateKey);

            return new TestCA(Reload(root), Reload(intermediate));

        }

        #endregion

        #region Sign(CSR, NotBefore, NotAfter, ClientAuthentication = false)

        /// <summary>
        /// Answer a signing request, over whatever period the test needs.
        /// </summary>
        /// <remarks>
        /// The extensions of the request are carried over deliberately: what is
        /// being tested is what the controller asked for and got back, and an
        /// authority that dropped the names would make every test about names
        /// pass for the wrong reason.
        /// </remarks>
        public X509Certificate2 Sign(String          CSR,
                                     DateTimeOffset  NotBefore,
                                     DateTimeOffset  NotAfter,
                                     Boolean         ClientAuthentication = false)
        {

            var request = CertificateRequest.LoadSigningRequestPem(
                              CSR,
                              HashAlgorithmName.SHA256,
                              CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions,
                              RSASignaturePadding.Pkcs1
                          );

            if (ClientAuthentication)
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.2", "TLS Web Client Authentication") ], false)
                );

            return Reload(
                       request.Create(
                           Issuer,
                           NotBefore,
                           NotAfter,
                           RandomNumberGenerator.GetBytes(16)
                       )
                   );

        }

        #endregion

        #region SignFor(Subject, NotBefore, NotAfter, ClientAuthentication)

        /// <summary>
        /// A certificate for a key of this authority's own making - a charging
        /// station, for instance, which does not ask this controller for one.
        /// </summary>
        public X509Certificate2 SignFor(String          Subject,
                                        DateTimeOffset  NotBefore,
                                        DateTimeOffset  NotAfter,
                                        Boolean         ClientAuthentication   = true,
                                        Boolean         WithoutAnyKeyUsage     = false)
        {

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var request   = new CertificateRequest($"CN={Subject}", key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

            if (!WithoutAnyKeyUsage)
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        [ new Oid(ClientAuthentication ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") ],
                        false
                    )
                );

            return Reload(
                       request.Create(
                           Issuer,
                           NotBefore,
                           NotAfter,
                           RandomNumberGenerator.GetBytes(16)
                       )
                   );

        }

        #endregion

        #region ToPEM(Certificates)

        /// <summary>
        /// Certificates the way they arrive from an authority: one PEM block
        /// after another in one file.
        /// </summary>
        public static String ToPEM(params X509Certificate2[] Certificates)

            => String.Join(
                   Environment.NewLine,
                   Certificates.Select(certificate => PemEncoding.WriteString("CERTIFICATE", certificate.RawData))
               ) + Environment.NewLine;

        /// <summary>
        /// A signed certificate together with the intermediate it needs, which
        /// is what an authority that has one actually sends back.
        /// </summary>
        public String ChainPEM(X509Certificate2 Certificate)

            => Intermediate is null
                   ? ToPEM(Certificate)
                   : ToPEM(Certificate, Intermediate);

        #endregion

        #region (private static) Reload(Certificate)

        /// <summary>
        /// A certificate detached from whatever key object made it, so that a
        /// "using" on that key cannot pull the ground out from under it.
        /// </summary>
        private static X509Certificate2 Reload(X509Certificate2 Certificate)

            => Certificate.HasPrivateKey
                   ? X509CertificateLoader.LoadPkcs12(
                         Certificate.Export(X509ContentType.Pkcs12, "test"),
                         "test",
                         X509KeyStorageFlags.Exportable
                     )
                   : X509CertificateLoader.LoadCertificate(Certificate.RawData);

        #endregion

        #region Dispose()

        public void Dispose()
        {
            Certificate .Dispose();
            Intermediate?.Dispose();
        }

        #endregion

    }

}
