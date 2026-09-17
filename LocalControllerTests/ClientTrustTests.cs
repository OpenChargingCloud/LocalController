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

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Which chains a charging station's own certificate may lead to - OCPP
    /// security profile 3, from the side that decides.
    /// </summary>
    public class ClientTrustTests
    {

        #region Data

        private String            directory  = default!;
        private TestClock         clock      = default!;
        private ClientTrustStore  store      = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStore()
        {
            directory = TestControllers.TemporaryDirectory("trust");
            clock     = TestClock.At(2026, 6, 1);
            store     = new ClientTrustStore(directory, clock);
        }

        [TearDown]
        public void RemoveTheDirectory()
        {
            store?.Dispose();
            TestControllers.Remove(directory);
        }

        #endregion

        #region (private) Accept(CA, Name)

        private String Accept(TestCA CA, String Name)
        {

            Assert.That(store.TryAdd(CA.ChainPEM(CA.Certificate), Name, out var id, out _, out var error),
                        Is.True, error);

            return id!;

        }

        #endregion


        #region AnEmptyListLetsNobodyIn()

        /// <summary>
        /// The mistake this avoids is the one where an empty configuration
        /// reads as an open door.
        /// </summary>
        [Test]
        public void AnEmptyListLetsNobodyIn()
        {

            using var ca      = TestCA.Create("Some Charging Network");
            using var station = ca.SignFor("cs001", clock.Now.AddDays(-1), clock.Now.AddYears(1));

            var result = store.Validate(station, null, false);

            Assert.Multiple(() => {
                Assert.That(result.Accepted, Is.False);
                Assert.That(result.Reason,   Does.Contain("has not been told"));
            });

        }

        #endregion

        #region AStationWithNoCertificateIsNotJudgedHere()

        /// <summary>
        /// Whether a station may connect without one depends on which security
        /// profiles are allowed, and this store has no opinion on them.
        /// </summary>
        [Test]
        public void AStationWithNoCertificateIsNotJudgedHere()
        {

            var result = store.Validate(null, null, false);

            Assert.Multiple(() => {
                Assert.That(result.Accepted, Is.False);
                Assert.That(result.Reason,   Does.Contain("no certificate"));
            });

        }

        #endregion

        #region AStationFromAnAcceptedAuthorityIsLetIn()

        [Test]
        public void AStationFromAnAcceptedAuthorityIsLetIn()
        {

            using var ca      = TestCA.Create("Some Charging Network");
            var id            = Accept(ca, "Some Charging Network");

            using var station = ca.SignFor("cs001", clock.Now.AddDays(-1), clock.Now.AddYears(1));

            var result = store.Validate(station, null, false);

            Assert.Multiple(() => {
                Assert.That(result.Accepted,  Is.True, result.Reason);
                Assert.That(result.AnchorId,  Is.EqualTo(id));
                Assert.That(result.Subject,   Does.Contain("cs001"));
            });

        }

        #endregion

        #region AStationOfAnyKindOfKeyIsLetIn(Algorithm)

        /// <summary>
        /// A P-256 authority, and stations whose own keys are anything at all.
        /// </summary>
        /// <remarks>
        /// The path a fleet actually takes, and the one nothing here measured:
        /// every certificate in these tests used to be P-256 on both sides,
        /// which is the one case that worked while Hermod chose a signature
        /// from the subject's key rather than the issuer's. So the tests sat
        /// exactly on the diagonal that hid the fault.
        ///
        /// The authority stays P-256 on purpose. Whoever issues certificates
        /// for a fleet does not change their root because one station turned
        /// up with a newer kind of key - and a controller that could only
        /// admit stations shaped like its own authority would be a controller
        /// nobody can migrate.
        /// </remarks>
        [Test]
        [TestCase("ecdsa-p521")]
        [TestCase("rsa-3072")]
        [TestCase("ed25519")]
        [TestCase("ed448")]
        [TestCase("ml-dsa-65")]
        public void AStationOfAnyKindOfKeyIsLetIn(String Algorithm)
        {

            using var ca      = TestCA.Create("Some Charging Network");
            var id            = Accept(ca, "Some Charging Network");

            using var station = ca.SignFor("cs001",
                                           clock.Now.AddDays(-1),
                                           clock.Now.AddYears(1),
                                           SubjectAlgorithm: Algorithm);

            var result = store.Validate(station, null, false);

            Assert.Multiple(() => {
                Assert.That(result.Accepted,  Is.True, $"A station with an {Algorithm} key was turned away: {result.Reason}");
                Assert.That(result.AnchorId,  Is.EqualTo(id));
                Assert.That(result.Subject,   Does.Contain("cs001"));
            });

        }

        #endregion

        #region AnAuthorityOfAnyKindOfKeyCanBeTrusted(Algorithm)

        /// <summary>
        /// And the other way round: a controller told to trust an authority
        /// that is not elliptic curve at all.
        /// </summary>
        /// <remarks>
        /// A different question from the one above, with a different answer,
        /// and the difference is worth keeping straight. A station's own key
        /// can be anything at all, because nothing in the chain has to verify
        /// it - every signature in the chain is the authority's. An
        /// authority's key has to be one this runtime can check a signature
        /// with, because that is precisely what building a chain does.
        ///
        /// So this is not pinned to a list. Measured on .NET 10 today: an RSA
        /// or an ML-DSA authority is accepted, and an Ed25519 one is refused
        /// with "the chain could not be built" - .NET gained ML-DSA in 10 and
        /// has never had Ed25519. That is a sentence with a date in it, and a
        /// test that froze it would start failing the day the runtime grows.
        ///
        /// What is asserted instead is that the answer is always one of the
        /// two: accepted with the right anchor, or refused for a reason that
        /// says what could not be done. Never a crash, never a silent yes.
        /// </remarks>
        [Test]
        [TestCase("rsa-3072")]
        [TestCase("ed25519")]
        [TestCase("ed448")]
        [TestCase("ml-dsa-65")]
        public void AnAuthorityIsUsableWhereThisRuntimeCanCheckItsSignatures(String Algorithm)
        {

            using var ca      = TestCA.Create("Some Charging Network", Algorithm: Algorithm);
            var id            = Accept(ca, "Some Charging Network");

            using var station = ca.SignFor("cs001",
                                           clock.Now.AddDays(-1),
                                           clock.Now.AddYears(1),
                                           SubjectAlgorithm: Algorithm);

            var result = store.Validate(station, null, false);

            if (result.Accepted)
                Assert.That(result.AnchorId, Is.EqualTo(id),
                            $"An {Algorithm} authority was accepted and credited to the wrong anchor.");

            else
                Assert.That(result.Reason, Does.Contain("chain"),
                            $"An {Algorithm} authority was refused, and the reason says nothing about " +
                            $"the chain that could not be built: {result.Reason}");

        }

        #endregion

        #region AStationFromSomewhereElseIsTurnedAway()

        /// <summary>
        /// And it would be turned away even if the operating system trusted
        /// whoever signed it: the list here is the only list.
        /// </summary>
        [Test]
        public void AStationFromSomewhereElseIsTurnedAway()
        {

            using var ours    = TestCA.Create("Our Charging Network");
            using var theirs  = TestCA.Create("Somebody Else Entirely");

            Accept(ours, "ours");

            using var station = theirs.SignFor("cs001", clock.Now.AddDays(-1), clock.Now.AddYears(1));

            Assert.That(store.Validate(station, null, false).Accepted, Is.False);

        }

        #endregion

        #region AChainThroughAnIntermediateIsBuiltFromWhatIsKeptHere()

        /// <summary>
        /// A good many charging stations do not send the certificates between
        /// themselves and their root. One held here is the difference between a
        /// station that connects and one turned away over a certificate that
        /// was fine.
        /// </summary>
        [Test]
        public void AChainThroughAnIntermediateIsBuiltFromWhatIsKeptHere()
        {

            using var ca = TestCA.Create("Some Charging Network", WithIntermediate: true);

            Assert.That(store.TryAdd(TestCA.ToPEM(ca.Certificate, ca.Intermediate!), "network", out var id, out _, out var error),
                        Is.True, error);

            using var station = ca.SignFor("cs001", clock.Now.AddDays(-1), clock.Now.AddYears(1));

            // Nothing sent along: only what this store holds can build the chain.
            var result = store.Validate(station, null, false);

            Assert.Multiple(() => {
                Assert.That(result.Accepted, Is.True, result.Reason);
                Assert.That(result.AnchorId, Is.EqualTo(id));
                Assert.That(store.Entries.Single().Intermediates.Count, Is.EqualTo(1));
            });

        }

        #endregion

        #region ACertificateForSomethingElseIsTurnedAway()

        /// <summary>
        /// A certificate that lists what it may be used for and does not list
        /// authenticating a client is not a client certificate, whoever signed
        /// it.
        /// </summary>
        [Test]
        public void ACertificateForSomethingElseIsTurnedAway()
        {

            using var ca = TestCA.Create("Some Charging Network");

            Accept(ca, "network");

            using var webServer = ca.SignFor("cs001", clock.Now.AddDays(-1), clock.Now.AddYears(1), ClientAuthentication: false);

            var result = store.Validate(webServer, null, false);

            Assert.Multiple(() => {
                Assert.That(result.Accepted, Is.False);
                Assert.That(result.Reason,   Does.Contain("authenticating a client"));
            });

        }

        #endregion

        #region ACertificateThatSaysNothingAboutItsUseIsAccepted()

        /// <summary>
        /// One with no extended key usage at all is unrestricted, which is a
        /// different thing from one that lists what it is for and leaves this
        /// out.
        /// </summary>
        [Test]
        public void ACertificateThatSaysNothingAboutItsUseIsAccepted()
        {

            using var ca = TestCA.Create("Some Charging Network");

            Accept(ca, "network");

            using var station = ca.SignFor("cs001", clock.Now.AddDays(-1), clock.Now.AddYears(1), WithoutAnyKeyUsage: true);

            Assert.That(store.Validate(station, null, false).Accepted, Is.True);

        }

        #endregion

        #region AnExpiredStationCertificateIsTurnedAway()

        [Test]
        public void AnExpiredStationCertificateIsTurnedAway()
        {

            using var ca = TestCA.Create("Some Charging Network");

            Accept(ca, "network");

            using var station = ca.SignFor("cs001", clock.Now.AddYears(-2), clock.Now.AddYears(-1));

            Assert.That(store.Validate(station, null, false).Accepted, Is.False);

        }

        #endregion


        #region AChainSwitchedOffAcceptsNobodyAndIsStillThere()

        /// <summary>
        /// A chain being retired wants to be turned off for a while first, so
        /// that whoever turns it off finds out which stations depended on it
        /// while there is still a way back.
        /// </summary>
        [Test]
        public void AChainSwitchedOffAcceptsNobodyAndIsStillThere()
        {

            using var ca      = TestCA.Create("Some Charging Network");
            var id            = Accept(ca, "network");

            using var station = ca.SignFor("cs001", clock.Now.AddDays(-1), clock.Now.AddYears(1));

            Assert.That(store.Validate(station, null, false).Accepted, Is.True);

            Assert.That(store.TrySetEnabled(id, false, out var error), Is.True, error);

            Assert.Multiple(() => {

                Assert.That(store.Validate(station, null, false).Accepted, Is.False);
                Assert.That(store.EnabledCount,                           Is.EqualTo(0));
                Assert.That(store.Entries.Count,                          Is.EqualTo(1));

                // And back again.
                Assert.That(store.TrySetEnabled(id, true, out _),         Is.True);
                Assert.That(store.Validate(station, null, false).Accepted, Is.True);

            });

        }

        #endregion

        #region BeingSwitchedOffSurvivesARestart()

        [Test]
        public void BeingSwitchedOffSurvivesARestart()
        {

            using var ca = TestCA.Create("Some Charging Network");
            var id       = Accept(ca, "network");

            store.TrySetEnabled(id, false, out _);

            using var restarted = new ClientTrustStore(directory, clock);

            Assert.Multiple(() => {
                Assert.That(restarted.Entries.Single().Enabled, Is.False,
                            "A chain that was switched off came back switched on.");
                Assert.That(restarted.Entries.Single().Name,    Is.EqualTo("network"));
            });

        }

        #endregion

        #region TheSameAuthorityIsNotTrustedTwice()

        [Test]
        public void TheSameAuthorityIsNotTrustedTwice()
        {

            using var ca = TestCA.Create("Some Charging Network");

            Accept(ca, "network");

            Assert.Multiple(() => {
                Assert.That(store.TryAdd(ca.ChainPEM(ca.Certificate), "again", out _, out _, out var error), Is.False);
                Assert.That(error, Does.Contain("already accepted"));
            });

        }

        #endregion

        #region SomethingThatIsNotACertificateIsRefused()

        [Test]
        public void SomethingThatIsNotACertificateIsRefused()
        {

            Assert.That(store.TryAdd("this is our root certificate, trust us", null, out _, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("certificate"));

        }

        #endregion

        #region AnAnchorThatIsNotAnAuthoritySaysSo()

        /// <summary>
        /// Allowed, because pinning one charging station's own certificate is a
        /// thing somebody may mean to do - but it vouches for nothing else, and
        /// whoever added it should be told.
        /// </summary>
        [Test]
        public void AnAnchorThatIsNotAnAuthoritySaysSo()
        {

            using var ca   = TestCA.Create("Some Charging Network");
            using var leaf = ca.SignFor("cs001", clock.Now.AddDays(-1), clock.Now.AddYears(1));

            Assert.That(store.TryAdd(TestCA.ToPEM(leaf), "just this one station", out _, out var warnings, out var error),
                        Is.True, error);

            Assert.That(warnings.Any(warning => warning.Contains("certificate authority")), Is.True,
                        $"Nothing was said about it not being an authority: {String.Join(" | ", warnings)}");

        }

        #endregion

        #region AnExpiredAnchorIsAnnouncedLoudly()

        /// <summary>
        /// Every charging station under it is turned away on the same day, so
        /// this is not a warning.
        /// </summary>
        [Test]
        public void AnExpiredAnchorIsAnnouncedLoudly()
        {

            using var ca = TestCA.Create(
                               "Some Charging Network",
                               NotBefore: clock.Now.AddYears(-5),
                               NotAfter:  clock.Now.AddDays(-1)
                           );

            Accept(ca, "network");

            var said = new List<(Logging.LogLevel Level, String Message)>();
            store.OnNotice += (level, message) => said.Add((level, message));

            store.CheckExpiry();

            Assert.That(said.Any(entry => entry.Level == Logging.LogLevel.Critical &&
                                          entry.Message.Contains("expired")),
                        Is.True,
                        $"An expired trust anchor was not announced as critical: {String.Join(" | ", said.Select(entry => $"{entry.Level}: {entry.Message}"))}");

        }

        #endregion

        #region ARemovedChainTurnsItsStationsAway()

        [Test]
        public void ARemovedChainTurnsItsStationsAway()
        {

            using var ca      = TestCA.Create("Some Charging Network");
            var id            = Accept(ca, "network");

            using var station = ca.SignFor("cs001", clock.Now.AddDays(-1), clock.Now.AddYears(1));

            Assert.That(store.TryRemove(id, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(store.Validate(station, null, false).Accepted, Is.False);
                Assert.That(store.Entries,                                 Is.Empty);
                Assert.That(File.Exists(Path.Combine(directory, $"{id}.pem")),  Is.False);
                Assert.That(File.Exists(Path.Combine(directory, $"{id}.json")), Is.False);
            });

        }

        #endregion

        #region NothingSecretIsShownBecauseThereIsNone()

        /// <summary>
        /// A certificate is a public document; the page that lists them may be
        /// opened by anybody who may read the configuration.
        /// </summary>
        [Test]
        public void NothingSecretIsShownBecauseThereIsNone()
        {

            using var ca = TestCA.Create("Some Charging Network");

            Accept(ca, "network");

            var shown = store.ToJSON().ToString();

            Assert.Multiple(() => {
                Assert.That(shown, Does.Not.Contain("PRIVATE KEY"));
                Assert.That(shown, Does.Contain("Some Charging Network"));
                Assert.That(store.ToJSON()["entries"]?[0]?.Value<String>("state"), Is.EqualTo("valid"));
            });

        }

        #endregion

    }

}
