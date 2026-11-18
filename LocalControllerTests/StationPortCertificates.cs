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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.OCPP.Node;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// What the station port of a local controller presents, put into the
    /// node's certificate store the way a test needs it: a server key of the
    /// listener "ocpp", or a certificate an authority issued for a key made
    /// here - before the controller is built, or into a running one's store.
    /// </summary>
    /// <remarks>
    /// The station port is the listener "ocpp" of the node's store since the
    /// port's own store of keys went: whether the port encrypts is decided
    /// when it starts, so what it is to present is put there first, into
    /// the store a controller in that directory reads - "certificates" beside
    /// its configuration file.
    /// </remarks>
    internal static class StationPortCertificates
    {

        #region Data

        /// <summary>
        /// The listener of the store the station port is.
        /// </summary>
        public const String Listener = "ocpp";

        #endregion


        #region StoreIn(Directory)

        /// <summary>
        /// The certificate store a local controller in the given directory
        /// reads, read now - for a test that fills it before the controller is
        /// built.
        /// </summary>
        public static CertificateStore StoreIn(String Directory)
        {

            var store = new CertificateStore(Path.Combine(Directory, CertificatesConfiguration.DefaultDirectory),
                                             new EventLog(),
                                             NodeName:      "local controller",
                                             Listeners:     [ Listener ],
                                             RequestKinds:  [ CertificateKind.TLSServerIdentity ]);

            store.Reload();

            return store;

        }

        #endregion

        #region AServerKeyIn(Directory, AsNext = false) / AServerKey(Store, AsNext = false)

        /// <summary>
        /// A server key of the station port, made in the store a local
        /// controller in the given directory reads.
        /// </summary>
        public static ServerKey AServerKeyIn(String   Directory,
                                             Boolean  AsNext = false)

            => AServerKey(StoreIn(Directory), AsNext);

        /// <summary>
        /// A server key of the station port, made in the given store.
        /// </summary>
        public static ServerKey AServerKey(CertificateStore  Store,
                                           Boolean           AsNext = false)
        {

            Assert.That(Store.TryCreateServerKey(Listener, null, AsNext, out var key, out var error), Is.True, error);

            return key!;

        }

        #endregion

        #region AnIdentityIn(Directory, CA, ReachableAs, NotBefore, NotAfter) / AnIdentity(Store, ...)

        /// <summary>
        /// A certificate the given authority issued for a key made in the store a
        /// local controller in the given directory reads, for the names given -
        /// a TLS server identity of the station port.
        /// </summary>
        public static CertificateEntry AnIdentityIn(String               Directory,
                                                    TestCA               CA,
                                                    IEnumerable<String>  ReachableAs,
                                                    DateTimeOffset       NotBefore,
                                                    DateTimeOffset       NotAfter)

            => AnIdentity(StoreIn(Directory), CA, ReachableAs, NotBefore, NotAfter);

        /// <summary>
        /// A certificate the given authority issued for a key made in the given
        /// store, for the names given - a TLS server identity of the station
        /// port: a key and its signing request, signed, and brought back.
        /// </summary>
        public static CertificateEntry AnIdentity(CertificateStore     Store,
                                                  TestCA               CA,
                                                  IEnumerable<String>  ReachableAs,
                                                  DateTimeOffset       NotBefore,
                                                  DateTimeOffset       NotAfter)
        {

            var names = ReachableAs.ToArray();

            Assert.That(Store.TryCreateRequest(CertificateKind.TLSServerIdentity,
                                               names[0],
                                               null,
                                               new CertificateRequestAsks(null, null, null, null, names),
                                               [ Listener ],
                                               null,
                                               out var request,
                                               out var error,
                                               out _),
                        Is.True, error);

            using var certificate = CA.Sign(request!.CSR, NotBefore, NotAfter);

            Assert.That(Store.TryAnswerRequest(CA.ChainPEM(certificate), out var entry, out _, out _, out error, out _),
                        Is.True, error);

            return entry!;

        }

        #endregion

        #region CurrentPin(Node) / NextPin(Node) / NextKeyOf(Node)

        /// <summary>
        /// The key pin of the current server key of the station port of a
        /// running node, or null.
        /// </summary>
        public static KeyPin? CurrentPin(OCPPServingNode Node)

            => Node.Certificates.GetCurrentAndNextKey(Listener, out _)?.KeyPin;

        /// <summary>
        /// The key pin of the announced next server key of the station port of
        /// a running node, or null.
        /// </summary>
        public static KeyPin? NextPin(OCPPServingNode Node)

            => NextKeyOf(Node)?.KeyPin;

        /// <summary>
        /// The announced next server key of the station port of a running node,
        /// or null.
        /// </summary>
        public static ServerKey? NextKeyOf(OCPPServingNode Node)
        {
            Node.Certificates.GetCurrentAndNextKey(Listener, out var next);
            return next;
        }

        #endregion

    }

}
