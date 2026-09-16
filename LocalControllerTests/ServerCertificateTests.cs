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

using NUnit.Framework;

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The keys and certificates the charging station server authenticates
    /// with: making them, taking them in, and which of them is presented on any
    /// given day.
    /// </summary>
    /// <remarks>
    /// Everything here runs against a clock that can be moved, because
    /// everything here is about dates. A certificate that takes over in two
    /// days cannot be tested by waiting two days.
    /// </remarks>
    public class ServerCertificateTests
    {

        #region Data

        private String                  directory  = default!;
        private TestClock               clock      = default!;
        private ServerCertificateStore  store      = default!;

        private static readonly String[] ReachableAs = [ "lc001.example.org", "192.168.1.10" ];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStore()
        {
            directory = TestControllers.TemporaryDirectory("certificates");
            clock     = TestClock.At(2026, 6, 1);
            store     = new ServerCertificateStore(directory, clock);
        }

        [TearDown]
        public void RemoveTheDirectory()
        {
            store?.Dispose();
            TestControllers.Remove(directory);
        }

        #endregion

        #region (private) NewKey(...) / Issue(...)

        /// <summary>
        /// A key and its signing request.
        /// </summary>
        private (String Id, String CSR) NewKey(String? Algorithm = null)
        {

            Assert.That(store.TryCreateKey("lc001.example.org", ReachableAs, Algorithm, out var id, out var csr, out var error),
                        Is.True, error);

            return (id!, csr!);

        }

        /// <summary>
        /// A key, a certificate for it over the given period, and that
        /// certificate taken in.
        /// </summary>
        private String Issue(TestCA          CA,
                             DateTimeOffset  NotBefore,
                             DateTimeOffset  NotAfter)
        {

            var (id, csr)         = NewKey();
            using var certificate = CA.Sign(csr, NotBefore, NotAfter);

            Assert.That(store.TryAddCertificate(CA.ChainPEM(certificate), ReachableAs, out var taken, out _, out var error),
                        Is.True, error);

            Assert.That(taken, Is.EqualTo(id));

            return id;

        }

        #endregion


        #region ARequestNamesEverythingThisControllerIsReachedAs()

        /// <summary>
        /// A certificate that does not carry the name a charging station dialled
        /// is a certificate the station refuses, whatever else is right about
        /// it. So the names go into the request, and both kinds of them.
        /// </summary>
        [Test]
        public void ARequestNamesEverythingThisControllerIsReachedAs()
        {

            var (_, csr) = NewKey();

            var request  = CertificateRequest.LoadSigningRequestPem(
                               csr,
                               HashAlgorithmName.SHA256,
                               CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions
                           );

            var san      = request.CertificateExtensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();

            Assert.Multiple(() => {

                Assert.That(csr, Does.StartWith("-----BEGIN CERTIFICATE REQUEST-----"));
                Assert.That(san, Is.Not.Null, "The signing request named nothing this controller is reachable as.");

                Assert.That(san!.EnumerateDnsNames(),                                   Is.EquivalentTo(new[] { "lc001.example.org" }));
                Assert.That(san. EnumerateIPAddresses().Select(address => address.ToString()), Is.EquivalentTo(new[] { "192.168.1.10" }));

                Assert.That(request.CertificateExtensions.OfType<X509EnhancedKeyUsageExtension>().
                                Single().EnhancedKeyUsages.Cast<Oid>().Select(oid => oid.Value),
                            Does.Contain("1.3.6.1.5.5.7.3.1"),
                            "The request did not ask for a server certificate.");

            });

        }

        #endregion

        #region ARequestWithoutNamesIsRefused()

        /// <summary>
        /// Rather than handing somebody a request that produces a certificate
        /// nothing will accept.
        /// </summary>
        [Test]
        public void ARequestWithoutNamesIsRefused()
        {

            Assert.That(store.TryCreateKey("lc001", [], null, out _, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("reachable as"));

        }

        #endregion

        #region AKeyThisControllerDoesNotGenerateIsRefused()

        [Test]
        public void AKeyThisControllerDoesNotGenerateIsRefused()
        {

            Assert.That(store.TryCreateKey("lc001", ReachableAs, "dsa-512", out _, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("dsa-512"));

        }

        #endregion

        #region ThePrivateKeyIsWrittenDownAndNeverHandedOut()

        /// <summary>
        /// It is generated here and stays here: what leaves is the signing
        /// request, which is a public document.
        /// </summary>
        [Test]
        public void ThePrivateKeyIsWrittenDownAndNeverHandedOut()
        {

            var (id, _) = NewKey();

            var onDisk  = File.ReadAllText(Path.Combine(directory, $"{id}.key.pem"));
            var shown   = store.ToJSON().ToString();

            Assert.Multiple(() => {

                Assert.That(onDisk, Does.Contain("PRIVATE KEY"));

                Assert.That(shown,  Does.Not.Contain("PRIVATE KEY"),
                            "The private key turned up in what the web interface is shown.");

                Assert.That(shown,  Does.Not.Contain(onDisk.Split('\n')[1].Trim()),
                            "A line of the private key turned up in what the web interface is shown.");

                Assert.That(store.ToJSON().Value<Boolean>("canImportPrivateKeys"), Is.False,
                            "The web interface is being offered a way to import a private key.");

            });

        }

        #endregion

        #region ARequestCanBeCollectedAgainLater()

        /// <summary>
        /// Frequently by somebody who is not the person who pressed the button.
        /// </summary>
        [Test]
        public void ARequestCanBeCollectedAgainLater()
        {

            var (id, csr) = NewKey();

            Assert.That(store.TryReadCSR(id, out var again, out var error), Is.True, error);
            Assert.That(again, Is.EqualTo(csr));

        }

        #endregion


        #region ACertificateForAKeyThatIsNotHereIsRefused()

        /// <summary>
        /// A certificate is only usable here if it answers a request made here.
        /// </summary>
        [Test]
        public void ACertificateForAKeyThatIsNotHereIsRefused()
        {

            using var ca      = TestCA.Create("Somebody Else");
            using var foreign = ca.SignFor("lc001.example.org", clock.Now.AddDays(-1), clock.Now.AddYears(1), ClientAuthentication: false);

            NewKey();

            Assert.Multiple(() => {
                Assert.That(store.TryAddCertificate(TestCA.ToPEM(foreign), ReachableAs, out _, out _, out var error), Is.False);
                Assert.That(error, Does.Contain("belongs to a key"));
            });

        }

        #endregion

        #region SomethingThatIsNotACertificateIsRefused()

        [Test]
        public void SomethingThatIsNotACertificateIsRefused()
        {

            NewKey();

            Assert.That(store.TryAddCertificate("Dear sir, please find the certificate attached.", ReachableAs, out _, out _, out var error),
                        Is.False);

            Assert.That(error, Does.Contain("certificate"));

        }

        #endregion

        #region AnAlreadyExpiredCertificateIsRefusedAtTheDoor()

        /// <summary>
        /// Everything that can be checked is checked while somebody is looking
        /// at the screen, rather than at whatever hour the switch would have
        /// happened. A certificate that has already run out can never be used,
        /// so it is refused now.
        /// </summary>
        [Test]
        public void AnAlreadyExpiredCertificateIsRefusedAtTheDoor()
        {

            using var ca  = TestCA.Create("Test CA");
            var (_, csr)  = NewKey();

            using var old = ca.Sign(csr, clock.Now.AddYears(-2), clock.Now.AddDays(-1));

            Assert.Multiple(() => {
                Assert.That(store.TryAddCertificate(ca.ChainPEM(old), ReachableAs, out _, out _, out var error), Is.False);
                Assert.That(error, Does.Contain("expired"));
            });

        }

        #endregion

        #region ACertificateForOtherNamesIsTakenInAndSaysSo()

        /// <summary>
        /// Reported rather than refused: a 'reachable as' list that is out of
        /// date looks exactly like a wrong certificate from in here, and only
        /// one of the two is the certificate's fault.
        /// </summary>
        [Test]
        public void ACertificateForOtherNamesIsTakenInAndSaysSo()
        {

            using var ca = TestCA.Create("Test CA");

            Assert.That(store.TryCreateKey("lc001.example.org", [ "somewhere.else.example" ], null, out var id, out var csr, out var keyError),
                        Is.True, keyError);

            using var certificate = ca.Sign(csr!, clock.Now.AddDays(-1), clock.Now.AddYears(1));

            Assert.That(store.TryAddCertificate(ca.ChainPEM(certificate), ReachableAs, out _, out var warnings, out var error),
                        Is.True, error);

            Assert.That(warnings.Any(warning => warning.Contains("lc001.example.org")), Is.True,
                        $"Nothing was said about the names it is not valid for: {String.Join(" | ", warnings)}");

        }

        #endregion

        #region IntermediatesAreKeptAndTheRootIsNot()

        /// <summary>
        /// A client is expected to know roots and not intermediates, so TLS has
        /// the server send everything in between and nothing above it.
        /// </summary>
        [Test]
        public void IntermediatesAreKeptAndTheRootIsNot()
        {

            using var ca = TestCA.Create("Test CA", WithIntermediate: true);

            var (id, csr)         = NewKey();
            using var certificate = ca.Sign(csr, clock.Now.AddDays(-1), clock.Now.AddYears(1));

            // The way an authority actually sends it back: leaf, issuing CA and
            // root, all in one file.
            Assert.That(store.TryAddCertificate(
                            TestCA.ToPEM(certificate, ca.Intermediate!, ca.Certificate),
                            ReachableAs,
                            out _, out _, out var error
                        ), Is.True, error);

            var entry = store.Entries.Single(entry => entry.Id == id);

            Assert.Multiple(() => {
                Assert.That(entry.Intermediates.Count, Is.EqualTo(1),
                            "The root was sent along, or the issuing certificate was dropped.");
                Assert.That(entry.Intermediates[0].Subject, Does.Contain("Issuing CA"));
            });

        }

        #endregion

        #region ACertificateWithoutItsIntermediatesSaysSo()

        [Test]
        public void ACertificateWithoutItsIntermediatesSaysSo()
        {

            using var ca = TestCA.Create("Test CA", WithIntermediate: true);

            var (_, csr)          = NewKey();
            using var certificate = ca.Sign(csr, clock.Now.AddDays(-1), clock.Now.AddYears(1));

            Assert.That(store.TryAddCertificate(TestCA.ToPEM(certificate), ReachableAs, out _, out var warnings, out var error),
                        Is.True, error);

            Assert.That(warnings.Any(warning => warning.Contains("intermediate")), Is.True,
                        $"Nothing was said about the missing intermediates: {String.Join(" | ", warnings)}");

        }

        #endregion


        #region ACertificateThatIsNotValidYetWaitsItsTurn()

        /// <summary>
        /// The whole reason there is more than one: the replacement is uploaded
        /// the day it arrives and takes over by itself the moment it may.
        /// </summary>
        [Test]
        public void ACertificateThatIsNotValidYetWaitsItsTurn()
        {

            using var ca = TestCA.Create("Test CA");

            var current  = Issue(ca, clock.Now.AddYears(-1), clock.Now.AddDays(5));
            var next     = Issue(ca, clock.Now.AddDays(2),   clock.Now.AddYears(1));

            Assert.Multiple(() => {

                Assert.That(store.Select()?.Certificate.Thumbprint,
                            Is.EqualTo(store.Entries.Single(entry => entry.Id == current).Certificate!.Thumbprint),
                            "A certificate that is not valid yet was put into use.");

                Assert.That(store.ServedId, Is.EqualTo(current));

                var waiting = store.Entries.Single(entry => entry.Id == next);

                Assert.That(waiting.ToJSON(clock.Now, false)["certificate"]?.Value<String>("state"),
                            Is.EqualTo("pending"));

            });

        }

        #endregion

        #region TheReplacementTakesOverOnTheDayItBecomesValid()

        [Test]
        public void TheReplacementTakesOverOnTheDayItBecomesValid()
        {

            using var ca = TestCA.Create("Test CA");

            var current  = Issue(ca, clock.Now.AddYears(-1), clock.Now.AddDays(5));
            var next     = Issue(ca, clock.Now.AddDays(2),   clock.Now.AddYears(1));

            store.Select();
            Assert.That(store.ServedId, Is.EqualTo(current), "The wrong one was in use to begin with.");

            clock.Advance(TimeSpan.FromDays(3));

            store.Select();

            Assert.That(store.ServedId, Is.EqualTo(next),
                        "The replacement did not take over on the day it became valid.");

        }

        #endregion

        #region TheNewerOfTwoValidCertificatesIsTheOnePresented()

        /// <summary>
        /// Of the certificates that are in their window, the one that became
        /// valid last - so that uploading a replacement is what changes which
        /// one is used, and not the order the files happen to be read in.
        /// </summary>
        [Test]
        public void TheNewerOfTwoValidCertificatesIsTheOnePresented()
        {

            using var ca = TestCA.Create("Test CA");

            Issue(ca, clock.Now.AddDays(-30), clock.Now.AddDays(30));
            var newer = Issue(ca, clock.Now.AddDays(-1), clock.Now.AddDays(400));

            store.Select();

            Assert.That(store.ServedId, Is.EqualTo(newer));

        }

        #endregion

        #region WhenNothingIsValidTheLastOneKeepsGoingOut()

        /// <summary>
        /// A station that checks certificates will refuse it and say so; one
        /// that does not will go on charging cars. Refusing to serve anything
        /// would take the site down in both cases.
        /// </summary>
        [Test]
        public void WhenNothingIsValidTheLastOneKeepsGoingOut()
        {

            using var ca = TestCA.Create("Test CA");

            var only     = Issue(ca, clock.Now.AddYears(-1), clock.Now.AddDays(2));

            var before   = store.Select();
            Assert.That(before, Is.Not.Null);

            clock.Advance(TimeSpan.FromDays(10));

            var after    = store.Select();

            Assert.Multiple(() => {
                Assert.That(after,             Is.Not.Null, "Nothing at all was presented once the certificate had run out.");
                Assert.That(after!.CacheKey,   Is.EqualTo(before!.CacheKey));
                Assert.That(store.ServedId,    Is.EqualTo(only));
            });

        }

        #endregion

        #region AFreshStartWithNothingValidPresentsTheLastToExpire()

        /// <summary>
        /// The same decision, made by a process that was not running when the
        /// certificate ran out and so has nothing it was already presenting.
        /// </summary>
        [Test]
        public void AFreshStartWithNothingValidPresentsTheLastToExpire()
        {

            using var ca = TestCA.Create("Test CA");

            Issue(ca, clock.Now.AddYears(-2), clock.Now.AddDays(1));
            var later = Issue(ca, clock.Now.AddYears(-2), clock.Now.AddDays(3));

            clock.Advance(TimeSpan.FromDays(10));

            using var restarted = new ServerCertificateStore(directory, clock);

            var chosen = restarted.Select();

            Assert.Multiple(() => {
                Assert.That(chosen,               Is.Not.Null, "A controller that restarted with only expired certificates presented none.");
                Assert.That(restarted.ServedId,   Is.EqualTo(later));
            });

        }

        #endregion

        #region NothingAtAllIsPresentedWhenThereIsNoCertificate()

        [Test]
        public void NothingAtAllIsPresentedWhenThereIsNoCertificate()
        {

            NewKey();

            Assert.Multiple(() => {
                Assert.That(store.Select(),         Is.Null);
                Assert.That(store.HasCertificate,   Is.False);
            });

        }

        #endregion

        #region TheSameAnswerComesBackUntilSomethingChanges()

        /// <summary>
        /// The selector is asked once per accepted connection, so it must not
        /// build a new chain every time: whoever caches a TLS context keys it by
        /// what the chain holds, and a new object for the same certificates
        /// would throw that cache away on every connection.
        /// </summary>
        [Test]
        public void TheSameAnswerComesBackUntilSomethingChanges()
        {

            using var ca = TestCA.Create("Test CA");

            Issue(ca, clock.Now.AddYears(-1), clock.Now.AddYears(1));

            Assert.That(store.Select(), Is.SameAs(store.Select()));

        }

        #endregion


        #region TheCertificateInUseCannotBeThrownAway()

        /// <summary>
        /// A server without a certificate stops speaking TLS, and doing that by
        /// deleting a file is not a decision anybody means to make.
        /// </summary>
        [Test]
        public void TheCertificateInUseCannotBeThrownAway()
        {

            using var ca = TestCA.Create("Test CA");

            var id = Issue(ca, clock.Now.AddYears(-1), clock.Now.AddYears(1));

            store.Select();

            Assert.Multiple(() => {
                Assert.That(store.TryRemove(id, out var error), Is.False);
                Assert.That(error,                              Does.Contain("presenting right now"));
                Assert.That(File.Exists(Path.Combine(directory, $"{id}.key.pem")), Is.True);
            });

        }

        #endregion

        #region AKeyThatIsNotInUseGoesAwayEntirely()

        [Test]
        public void AKeyThatIsNotInUseGoesAwayEntirely()
        {

            var (id, _) = NewKey();

            Assert.That(store.TryRemove(id, out var error), Is.True, error);

            Assert.Multiple(() => {

                Assert.That(store.Entries.Any(entry => entry.Id == id), Is.False);

                foreach (var extension in new[] { "key.pem", "csr.pem", "json" })
                    Assert.That(File.Exists(Path.Combine(directory, $"{id}.{extension}")), Is.False,
                                $"'{id}.{extension}' was left behind.");

            });

        }

        #endregion

        #region EverythingIsStillThereAfterARestart()

        /// <summary>
        /// Read back the way it will be read at the next start, and not from
        /// what was still in hand when it was written.
        /// </summary>
        [Test]
        public void EverythingIsStillThereAfterARestart()
        {

            using var ca = TestCA.Create("Test CA", WithIntermediate: true);

            var id = Issue(ca, clock.Now.AddYears(-1), clock.Now.AddYears(1));

            using var restarted = new ServerCertificateStore(directory, clock);

            var entry = restarted.Entries.SingleOrDefault(entry => entry.Id == id);

            Assert.Multiple(() => {
                Assert.That(entry,                        Is.Not.Null);
                Assert.That(entry!.Certificate,           Is.Not.Null);
                Assert.That(entry.Certificate!.HasPrivateKey, Is.True,
                            "The certificate came back without the private key, so it cannot be used for TLS.");
                Assert.That(entry.Intermediates.Count,    Is.EqualTo(1));
                Assert.That(entry.CreatedAt,              Is.EqualTo(clock.Now));
            });

        }

        #endregion

        #region AnUnreadableKeyIsSkippedAndTheOthersStillWork()

        /// <summary>
        /// One broken file must not stop a controller that has three others -
        /// and the one keeping a car park running is frequently one of the
        /// three.
        /// </summary>
        [Test]
        public void AnUnreadableKeyIsSkippedAndTheOthersStillWork()
        {

            using var ca = TestCA.Create("Test CA");

            var good = Issue(ca, clock.Now.AddYears(-1), clock.Now.AddYears(1));

            File.WriteAllText(Path.Combine(directory, "broken.key.pem"), "-----BEGIN PRIVATE KEY-----\nnope\n-----END PRIVATE KEY-----\n");

            var said = new List<String>();

            using var restarted = new ServerCertificateStore(directory, clock);
            restarted.OnNotice += (level, message) => said.Add(message);
            restarted.Reload();

            Assert.Multiple(() => {
                Assert.That(restarted.Select()?.Certificate.Thumbprint, Is.Not.Null);
                Assert.That(restarted.ServedId,                         Is.EqualTo(good));
                Assert.That(said.Any(message => message.Contains("broken")), Is.True,
                            "The unreadable key was skipped without a word.");
            });

        }

        #endregion

        #region AnExpiryIsAnnouncedWhileThereIsStillTimeToActOnIt()

        [Test]
        public void AnExpiryIsAnnouncedWhileThereIsStillTimeToActOnIt()
        {

            using var ca = TestCA.Create("Test CA");

            Issue(ca, clock.Now.AddYears(-1), clock.Now.AddDays(10));

            var said = new List<String>();
            store.OnNotice += (level, message) => said.Add(message);

            store.CheckExpiry(ReachableAs);

            Assert.That(said.Any(message => message.Contains("runs out in 9 day(s)") ||
                                            message.Contains("runs out in 10 day(s)")),
                        Is.True,
                        $"Nothing was said about a certificate ten days from running out: {String.Join(" | ", said)}");

        }

        #endregion

        #region NothingValidAtAllIsTheLoudestThingThereIs()

        /// <summary>
        /// The state where every charging station that checks certificates is
        /// being turned away. A check that walked past it in silence would be
        /// quiet in exactly the one case that needs a voice.
        /// </summary>
        [Test]
        public void NothingValidAtAllIsTheLoudestThingThereIs()
        {

            using var ca = TestCA.Create("Test CA");

            Issue(ca, clock.Now.AddYears(-1), clock.Now.AddDays(1));

            clock.Advance(TimeSpan.FromDays(10));

            var said = new List<(Logging.LogLevel Level, String Message)>();
            store.OnNotice += (level, message) => said.Add((level, message));

            store.CheckExpiry(ReachableAs);

            Assert.That(said.Any(entry => entry.Level == Logging.LogLevel.Critical &&
                                          entry.Message.Contains("No server certificate")),
                        Is.True,
                        $"A controller with nothing valid said: {String.Join(" | ", said.Select(entry => $"{entry.Level}: {entry.Message}"))}");

        }

        #endregion

        #region NothingIsAnnouncedWhenTheReplacementIsAlreadyHere()

        /// <summary>
        /// A certificate expiring next week with its replacement already
        /// uploaded is what a planned replacement looks like. Warning about it
        /// is how a warning gets ignored.
        /// </summary>
        [Test]
        public void NothingIsAnnouncedWhenTheReplacementIsAlreadyHere()
        {

            using var ca = TestCA.Create("Test CA");

            Issue(ca, clock.Now.AddYears(-1), clock.Now.AddDays(10));
            Issue(ca, clock.Now.AddDays(5),   clock.Now.AddYears(1));

            var said = new List<String>();
            store.OnNotice += (level, message) => said.Add(message);

            store.CheckExpiry(ReachableAs);

            Assert.That(said.Any(message => message.Contains("runs out")), Is.False,
                        $"A planned replacement was warned about anyway: {String.Join(" | ", said)}");

        }

        #endregion

    }

}
