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
    /// Which charging stations may sign in, and with what - OCPP security
    /// profiles 1 and 2.
    /// </summary>
    public class StationLoginTests
    {

        #region Data

        private String                 directory  = default!;
        private TestClock              clock      = default!;
        private ChargingStationLogins  logins     = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAFile()
        {

            directory = TestControllers.TemporaryDirectory("stations");
            Directory.CreateDirectory(directory);

            clock     = TestClock.At(2026, 6, 1);
            logins    = new ChargingStationLogins(Path.Combine(directory, "ocpp-stations.json"), clock);

            Assert.That(logins.TryLoad(out var error), Is.True, error);

        }

        [TearDown]
        public void RemoveTheDirectory()
            => TestControllers.Remove(directory);

        #endregion


        #region NoFileIsAControllerNoStationHasBeenAddedTo()

        [Test]
        public void NoFileIsAControllerNoStationHasBeenAddedTo()
        {

            Assert.Multiple(() => {
                Assert.That(logins.Exists,        Is.False);
                Assert.That(logins.Logins,        Is.Empty);
                Assert.That(logins.EnabledCount,  Is.EqualTo(0));
            });

        }

        #endregion

        #region AnUnreadableFileIsAFailureAndNotAnEmptyList()

        /// <summary>
        /// Carrying on with an empty list would turn every charging station on
        /// the site away while looking like nothing was wrong.
        /// </summary>
        [Test]
        public void AnUnreadableFileIsAFailureAndNotAnEmptyList()
        {

            var path = Path.Combine(directory, "ocpp-stations.json");

            File.WriteAllText(path, "{ stations: [ unquoted");

            var broken = new ChargingStationLogins(path, clock);

            Assert.Multiple(() => {
                Assert.That(broken.TryLoad(out var error), Is.False);
                Assert.That(error,                         Does.Contain("ocpp-stations.json"));
            });

        }

        #endregion

        #region APasswordIsMadeUpAndComesBackExactlyOnce()

        /// <summary>
        /// To be shown to whoever is standing in front of the charging station
        /// about to type it in. It is kept only as a hash, so a second look is
        /// a second password.
        /// </summary>
        [Test]
        public void APasswordIsMadeUpAndComesBackExactlyOnce()
        {

            Assert.That(logins.TrySetPassword("cs001", null, "Ladepunkt 1", out var generated, out var error), Is.True, error);

            Assert.Multiple(() => {

                Assert.That(generated,          Is.Not.Null.And.Not.Empty);
                Assert.That(generated!.Length,  Is.GreaterThanOrEqualTo(ChargingStationLogins.MinPasswordLength));
                Assert.That(logins.Verify("cs001", generated), Is.True);

                // And nowhere else: not in what the page is shown.
                Assert.That(logins.ToJSON().ToString(), Does.Not.Contain(generated));

            });

        }

        #endregion

        #region TheFileNeverHoldsThePasswordInTheClear()

        [Test]
        public void TheFileNeverHoldsThePasswordInTheClear()
        {

            logins.TrySetPassword("cs001", null, null, out var generated, out _);

            var written = File.ReadAllText(Path.Combine(directory, "ocpp-stations.json"));

            Assert.Multiple(() => {
                Assert.That(written, Does.Not.Contain(generated!),
                            "The password of a charging station was written to its file in the clear.");
                Assert.That(written, Does.Contain("$pbkdf2"));
            });

        }

        #endregion

        #region AChosenPasswordHasToBeWorthChoosing()

        /// <summary>
        /// OCPP asks for at least 16 characters of entropy in the password of a
        /// charging station; a controller that accepted "1234" would be the
        /// weakest part of a system built around that rule.
        /// </summary>
        [Test]
        public void AChosenPasswordHasToBeWorthChoosing()
        {

            Assert.Multiple(() => {

                Assert.That(logins.TrySetPassword("cs001", "1234", null, out _, out var tooShort), Is.False);
                Assert.That(tooShort, Does.Contain("16"));

                Assert.That(logins.TrySetPassword("cs001", new String('x', 65), null, out _, out var tooLong), Is.False);
                Assert.That(tooLong,  Does.Contain("64"));

                Assert.That(logins.TrySetPassword("cs001", new String('x', 20), null, out _, out var fine), Is.True, fine);

            });

        }

        #endregion

        #region AStationNeedsAnIdentificationToSignInWith()

        [Test]
        public void AStationNeedsAnIdentificationToSignInWith()
        {

            Assert.Multiple(() => {
                Assert.That(logins.TrySetPassword("",  null, null, out _, out var empty), Is.False);
                Assert.That(empty, Does.Contain("identification"));

                Assert.That(logins.TrySetPassword(new String('c', 49), null, null, out _, out var tooLong), Is.False);
                Assert.That(tooLong, Does.Contain("48"));
            });

        }

        #endregion

        #region AWrongPasswordOrAnUnknownStationIsRefused()

        [Test]
        public void AWrongPasswordOrAnUnknownStationIsRefused()
        {

            logins.TrySetPassword("cs001", null, null, out var generated, out _);

            Assert.Multiple(() => {
                Assert.That(logins.Verify("cs001", generated!),        Is.True);
                Assert.That(logins.Verify("cs001", "something else"),  Is.False);
                Assert.That(logins.Verify("cs002", generated!),        Is.False);
                Assert.That(logins.Verify("",      ""),                Is.False);
            });

        }

        #endregion

        #region AStationSwitchedOffCannotSignInAndKeepsItsPassword()

        [Test]
        public void AStationSwitchedOffCannotSignInAndKeepsItsPassword()
        {

            logins.TrySetPassword("cs001", null, null, out var generated, out _);

            Assert.That(logins.TrySetEnabled("cs001", false, out var error), Is.True, error);

            Assert.Multiple(() => {

                Assert.That(logins.Verify("cs001", generated!),  Is.False);
                Assert.That(logins.EnabledCount,                 Is.EqualTo(0));
                Assert.That(logins.Logins.Count,                 Is.EqualTo(1));

                // A switched-off station is not handed to the server at all.
                Assert.That(logins.SecurePasswords(),            Is.Empty);

                Assert.That(logins.TrySetEnabled("cs001", true, out _), Is.True);
                Assert.That(logins.Verify("cs001", generated!),  Is.True);

            });

        }

        #endregion

        #region ChangingAPasswordKeepsWhatWasKnownAboutTheStation()

        [Test]
        public void ChangingAPasswordKeepsWhatWasKnownAboutTheStation()
        {

            logins.TrySetPassword("cs001", null, "Ladepunkt 1", out var first, out _);

            logins.TrySetEnabled ("cs001", false, out _);
            logins.TrySetPassword("cs001", null, null, out var second, out var error);

            var station = logins.Logins.Single();

            Assert.Multiple(() => {
                Assert.That(error,                            Is.Null);
                Assert.That(station.Note,                     Is.EqualTo("Ladepunkt 1"), "The note was lost when the password changed.");
                Assert.That(station.Enabled,                  Is.False,                  "A switched-off station was switched on by a new password.");
                Assert.That(logins.Verify("cs001", first!),   Is.False);
                Assert.That(second,                           Is.Not.EqualTo(first));
            });

        }

        #endregion

        #region ARemovedStationIsGoneFromTheFileToo()

        [Test]
        public void ARemovedStationIsGoneFromTheFileToo()
        {

            logins.TrySetPassword("cs001", null, null, out var generated, out _);

            Assert.That(logins.TryRemove("cs001", out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(logins.Verify("cs001", generated!), Is.False);
                Assert.That(File.ReadAllText(Path.Combine(directory, "ocpp-stations.json")), Does.Not.Contain("cs001"));
                Assert.That(logins.TryRemove("cs001", out var again), Is.False);
                Assert.That(again, Does.Contain("never heard of"));
            });

        }

        #endregion

        #region EverythingIsStillThereAfterARestart()

        [Test]
        public void EverythingIsStillThereAfterARestart()
        {

            logins.TrySetPassword("cs001", null, "Ladepunkt 1", out var generated, out _);
            logins.TrySetPassword("cs002", null, null,          out _,             out _);
            logins.TrySetEnabled ("cs002", false, out _);

            var restarted = new ChargingStationLogins(Path.Combine(directory, "ocpp-stations.json"), clock);

            Assert.That(restarted.TryLoad(out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(restarted.Verify("cs001", generated!), Is.True);
                Assert.That(restarted.Logins.Count,                Is.EqualTo(2));
                Assert.That(restarted.EnabledCount,                Is.EqualTo(1));
                Assert.That(restarted.Logins.First().Note,         Is.EqualTo("Ladepunkt 1"));
                Assert.That(restarted.Logins.First().AddedAt,      Is.EqualTo(clock.Now));
            });

        }

        #endregion

        #region AChangeIsAnnouncedSoTheServerCanBeTold()

        /// <summary>
        /// A station whose password was taken away must stop being able to use
        /// it now, and not at the next start.
        /// </summary>
        [Test]
        public void AChangeIsAnnouncedSoTheServerCanBeTold()
        {

            var changes = 0;

            logins.OnChanged += () => changes++;

            logins.TrySetPassword("cs001", null, null, out _, out _);
            logins.TrySetEnabled ("cs001", false, out _);
            logins.TryRemove     ("cs001", out _);

            // And not for something that changed nothing.
            logins.TrySetEnabled("cs001", false, out _);

            Assert.That(changes, Is.EqualTo(3));

        }

        #endregion

        #region WhatThePageIsShownNamesNoHashes()

        /// <summary>
        /// A PBKDF2 hash on a screen is a PBKDF2 hash somebody can take away
        /// and attack offline.
        /// </summary>
        [Test]
        public void WhatThePageIsShownNamesNoHashes()
        {

            logins.TrySetPassword("cs001", null, "Ladepunkt 1", out _, out _);

            var shown = logins.ToJSON();

            Assert.Multiple(() => {
                Assert.That(shown.ToString(),                             Does.Not.Contain("$pbkdf2"));
                Assert.That(shown["stations"]?[0]?.Value<String>("id"),    Is.EqualTo("cs001"));
                Assert.That(shown["stations"]?[0]?.Value<String>("note"),  Is.EqualTo("Ladepunkt 1"));
                Assert.That(shown["stations"]?[0]?["password"],            Is.Null);
            });

        }

        #endregion

    }

}
