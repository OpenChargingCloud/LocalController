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

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.X509;

using org.GraphDefined.Vanaheimr.Hermod.PKI;

using BCx509 = Org.BouncyCastle.X509;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// A certificate authority for the duration of one test: it answers signing
    /// requests, and it can be made to answer them for any day at all.
    /// </summary>
    /// <remarks>
    /// <b>Bouncy Castle throughout, like the store it is testing.</b> .NET can
    /// only sign a request whose key is the same kind as the issuer's, and has
    /// never heard of an Ed448 or an ML-DSA one at all - so a test authority
    /// built on it could only ever answer requests for the two algorithms .NET
    /// knows, and every test about the others would fail for a reason that has
    /// nothing to do with what is being tested.
    ///
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

        #region Data

        /// <summary>
        /// The kind of key an authority and the stations it issues to get when
        /// nothing else is said.
        /// </summary>
        /// <remarks>
        /// What a fleet's authority actually is, and what every test that is
        /// not about key algorithms should keep using: a test that quietly
        /// changed the shape of its certificates would be testing something
        /// else.
        /// </remarks>
        public const String DefaultAlgorithm = "ecdsa-p256";

        /// <summary>
        /// The key of whichever certificate signs: the intermediate where there
        /// is one, the root otherwise.
        /// </summary>
        private readonly AsymmetricKeyParameter  issuerKey;

        private readonly BCx509.X509Certificate  issuer;

        /// <summary>
        /// The kind of key this authority signs with, which is what decides
        /// the signature it makes.
        /// </summary>
        /// <remarks>
        /// Kept rather than worked out at each signature, and kept rather than
        /// written into the call: "SHA256withECDSA" used to be a literal in
        /// three places, so an authority made with any other kind of key would
        /// have handed an elliptic curve signer a key it could not use. Which
        /// is the same mistake this library's own factory had, one layer down.
        /// </remarks>
        private readonly KeyAlgorithm            algorithm;

        #endregion

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

        #endregion

        #region Constructor(s)

        private TestCA(X509Certificate2        Certificate,
                       X509Certificate2?       Intermediate,
                       BCx509.X509Certificate  Issuer,
                       AsymmetricKeyParameter  IssuerKey,
                       KeyAlgorithm            Algorithm)
        {
            this.Certificate   = Certificate;
            this.Intermediate  = Intermediate;
            this.issuer        = Issuer;
            this.issuerKey     = IssuerKey;
            this.algorithm     = Algorithm;
        }

        #endregion


        #region (static) Create(Name, NotBefore = null, NotAfter = null, WithIntermediate = false)

        /// <summary>
        /// A new authority, valid over the given period.
        /// </summary>
        /// <param name="Algorithm">
        /// The kind of key this authority is built on; an elliptic curve P-256
        /// one when nothing is said, which is what a fleet's authority
        /// normally is.
        /// </param>
        public static TestCA Create(String           Name,
                                    DateTimeOffset?  NotBefore          = null,
                                    DateTimeOffset?  NotAfter           = null,
                                    Boolean          WithIntermediate   = false,
                                    String?          Algorithm          = null)
        {

            var algorithm = KeyAlgorithm.Find(Algorithm ?? DefaultAlgorithm)
                                ?? throw new ArgumentException($"'{Algorithm}' is not a kind of key this library makes.");

            // Wide on purpose, and not a year either side of today: an
            // authority cannot sign a certificate reaching outside its own
            // validity, and these tests deliberately issue certificates for
            // days long past and far ahead. A narrow authority would turn every
            // one of those tests into an error about the authority.
            var notBefore  = (NotBefore ?? new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)).UtcDateTime;
            var notAfter   = (NotAfter  ?? new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero)).UtcDateTime;

            #region A root, signing itself

            var rootPair = algorithm.Generate();

            var root     = Authority($"CN={Name}", rootPair.Public, rootPair, notBefore, notAfter, null, algorithm);

            #endregion

            if (!WithIntermediate)
                return new TestCA(ToDotNet(root), null, root, rootPair.Private, algorithm);

            #region An intermediate, signed by the root

            var intermediatePair = algorithm.Generate();

            var intermediate     = Authority($"CN={Name} Issuing CA",
                                             intermediatePair.Public,
                                             rootPair,
                                             notBefore,
                                             notAfter,
                                             root.SubjectDN,
                                             algorithm);

            #endregion

            return new TestCA(ToDotNet(root), ToDotNet(intermediate), intermediate, intermediatePair.Private, algorithm);

        }

        /// <summary>
        /// One certificate authority certificate, self-signed or not.
        /// </summary>
        private static BCx509.X509Certificate Authority(String                   Subject,
                                                        AsymmetricKeyParameter   PublicKey,
                                                        AsymmetricCipherKeyPair  Signer,
                                                        DateTime                 NotBefore,
                                                        DateTime                 NotAfter,
                                                        X509Name?                Issuer,
                                                        KeyAlgorithm             Algorithm)
        {

            var generator = new X509V3CertificateGenerator();

            generator.SetSerialNumber(Serial());
            generator.SetIssuerDN    (Issuer ?? new X509Name(Subject));
            generator.SetSubjectDN   (new X509Name(Subject));
            generator.SetNotBefore   (NotBefore);
            generator.SetNotAfter    (NotAfter);
            generator.SetPublicKey   (PublicKey);

            generator.AddExtension(X509Extensions.BasicConstraints, true,  new BasicConstraints(true));
            generator.AddExtension(X509Extensions.KeyUsage,         true,  new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign));

            return generator.Generate(new Asn1SignatureFactory(Algorithm.SignatureAlgorithm, Signer.Private));

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

            var request   = (Pkcs10CertificationRequest) new PemReader(new StringReader(CSR)).ReadObject();
            var info      = request.GetCertificationRequestInfo();

            var generator = new X509V3CertificateGenerator();

            generator.SetSerialNumber(Serial());
            generator.SetIssuerDN    (issuer.SubjectDN);
            generator.SetSubjectDN   (info.Subject);
            generator.SetNotBefore   (NotBefore.UtcDateTime);
            generator.SetNotAfter    (NotAfter. UtcDateTime);
            generator.SetPublicKey   (request.GetPublicKey());

            #region Whatever the request asked for

            var requested = request.GetRequestedExtensions();

            if (requested is not null)
                foreach (DerObjectIdentifier oid in requested.ExtensionOids)
                {

                    // Replaced below where the test wants something else.
                    if (ClientAuthentication && oid.Equals(X509Extensions.ExtendedKeyUsage))
                        continue;

                    var extension = requested.GetExtension(oid);

                    generator.AddExtension(oid, extension.IsCritical, extension.GetParsedValue());

                }

            if (ClientAuthentication)
                generator.AddExtension(X509Extensions.ExtendedKeyUsage, false,
                                       new ExtendedKeyUsage(KeyPurposeID.id_kp_clientAuth));

            #endregion

            // The signature is this authority's to make, so its own key
            // decides which one it is - not the key in the request. That is
            // exactly the mistake Hermod's factory had, and the reason an
            // authority here can answer a request for any kind of key at all.
            return ToDotNet(generator.Generate(new Asn1SignatureFactory(algorithm.SignatureAlgorithm, issuerKey)));

        }

        #endregion

        #region SignFor(Subject, NotBefore, NotAfter, ClientAuthentication)

        /// <summary>
        /// A certificate for a key of this authority's own making - a charging
        /// station, for instance, which does not ask this controller for one.
        /// </summary>
        /// <param name="SubjectAlgorithm">
        /// The kind of key the station gets, which need not be this
        /// authority's: a fleet does not change its root because one station
        /// asked for something newer.
        /// </param>
        public X509Certificate2 SignFor(String          Subject,
                                        DateTimeOffset  NotBefore,
                                        DateTimeOffset  NotAfter,
                                        Boolean         ClientAuthentication   = true,
                                        Boolean         WithoutAnyKeyUsage     = false,
                                        String?         SubjectAlgorithm       = null)
        {

            var subjectKind = KeyAlgorithm.Find(SubjectAlgorithm ?? DefaultAlgorithm)
                                  ?? throw new ArgumentException($"'{SubjectAlgorithm}' is not a kind of key this library makes.");

            var pair      = subjectKind.Generate();

            var generator = new X509V3CertificateGenerator();

            generator.SetSerialNumber(Serial());
            generator.SetIssuerDN    (issuer.SubjectDN);
            generator.SetSubjectDN   (new X509Name($"CN={Subject}"));
            generator.SetNotBefore   (NotBefore.UtcDateTime);
            generator.SetNotAfter    (NotAfter. UtcDateTime);
            generator.SetPublicKey   (pair.Public);

            generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(false));

            if (!WithoutAnyKeyUsage)
                generator.AddExtension(
                    X509Extensions.ExtendedKeyUsage,
                    false,
                    new ExtendedKeyUsage(ClientAuthentication
                                             ? KeyPurposeID.id_kp_clientAuth
                                             : KeyPurposeID.id_kp_serverAuth)
                );

            return ToDotNet(generator.Generate(new Asn1SignatureFactory(algorithm.SignatureAlgorithm, issuerKey)));

        }

        #endregion

        #region ToPEM(Certificates) / ChainPEM(Certificate)

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

        #region (private static) ToDotNet(Certificate) / Serial()

        /// <summary>
        /// The certificate alone, without a private key: nothing here is ever
        /// presented over TLS, it is only held up to be checked.
        /// </summary>
        private static X509Certificate2 ToDotNet(BCx509.X509Certificate Certificate)
            => X509CertificateLoader.LoadCertificate(Certificate.GetEncoded());

        private static BigInteger Serial()
            => new (1, RandomNumberGenerator.GetBytes(16));

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
