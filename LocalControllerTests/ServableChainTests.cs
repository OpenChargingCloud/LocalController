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

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Whether a certificate can actually be served, asked before a charging
    /// station finds out the hard way.
    /// </summary>
    /// <remarks>
    /// .NET does not merely hold the bytes of a server certificate: while
    /// making the context it walks the certificates that were handed to it, and
    /// refuses the lot when it cannot make a chain of them. What a charging
    /// station sees then is a TLS handshake reset with nothing said - the port
    /// reports itself as running and encrypted and turns everybody away.
    ///
    /// The point of these tests is that the question has an answer <em>before</em>
    /// a station knocks, and that the answer is a sentence somebody can act on
    /// rather than "An unknown chain building error occurred", which names
    /// neither the certificate nor what is wrong with it.
    /// </remarks>
    public class ServableChainTests
    {

        #region Data

        private String  directory  = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("servable");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void RemoveIt()
            => TestControllers.Remove(directory);

        #endregion

        #region (private) AStoreWith(CA, WithIntermediate)

        /// <summary>
        /// A key made here and a certificate for it, the way a controller comes
        /// by one.
        /// </summary>
        private ServerCertificateStore AStoreWith(TestCA CA, Boolean WithTheIntermediate)
        {

            var store = new ServerCertificateStore(Path.Combine(directory, ServerCertificateStore.DefaultDirectoryName));

            if (!store.TryCreateKey("127.0.0.1", [ "127.0.0.1" ], null, out _, out var csr, out var keyError))
                throw new InvalidOperationException($"The test's own key was refused: {keyError}");

            using var certificate = CA.Sign(csr!, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

            var pem = WithTheIntermediate
                          ? CA.ChainPEM(certificate)
                          : TestCA.ToPEM(certificate);

            if (!store.TryAddCertificate(pem, [ "127.0.0.1" ], out _, out _, out var addError))
                throw new InvalidOperationException($"The test's own certificate was refused: {addError}");

            return store;

        }

        #endregion


        #region ALeafOnItsOwnCanBeServed()

        /// <summary>
        /// Nothing was handed over but the certificate itself, so there is
        /// nothing that could fail to fit together.
        /// </summary>
        [Test]
        public void ALeafOnItsOwnCanBeServed()
        {

            using var ca     = TestCA.Create($"On its own {Guid.NewGuid()}", WithIntermediate: true);
            using var store  = AStoreWith(ca, WithTheIntermediate: false);

            var chain = store.Select();

            Assert.That(chain, Is.Not.Null);

            Assert.Multiple(() => {
                Assert.That(chain!.HasIntermediates,                             Is.False);
                Assert.That(chain.TryCreateContext(out var context, out var why), Is.True, why);
                Assert.That(context,                                              Is.Not.Null);
            });

        }

        #endregion

        // There is deliberately no test here for the case that fails. The
        // refusal this whole file is about - "An unknown chain building error
        // occurred" out of the context creation - could not be provoked on
        // demand: it depends on what this machine's certificate chain engine
        // happens to have cached, which is why the charging station TLS tests
        // beside this one come and go. A test asserting a guess at the trigger
        // would pin down the guess rather than the behaviour.

    }

}
