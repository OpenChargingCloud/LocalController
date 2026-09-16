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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// What a charging station needs in order to be let in with a one-time
    /// token, and what this controller refuses to store.
    /// </summary>
    public class TOTPSettingsTests
    {

        #region Data

        private static readonly DateTimeOffset Noon = new (2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        private const String TheSecret = "a-shared-secret-long-enough";

        #endregion

        #region (private) Settings(...)

        private static TOTPSettings Settings(String?             SharedSecret    = TheSecret,
                                             TimeSpan?           ValidityTime    = null,
                                             UInt32?             Length          = null,
                                             String?             Alphabet        = null,
                                             TOTPHashAlgorithm?  HashAlgorithm   = null)
        {

            if (!TOTPSettings.TryCreate(SharedSecret, ValidityTime, Length, Alphabet, HashAlgorithm,
                                        out var settings, out _, out var error))
            {
                throw new InvalidOperationException($"The test's own TOTP settings were refused: {error}");
            }

            return settings;

        }

        /// <summary>
        /// The token a charging station would be showing at that moment, made
        /// the way the station makes it rather than the way the controller
        /// checks it.
        /// </summary>
        private static String TokenAt(TOTPSettings Settings, DateTimeOffset When)

            => TOTPGenerator.GenerateTOTP(
                   Settings.SharedSecret,
                   Settings.ValidityTime,
                   Settings.Length,
                   Settings.Alphabet,
                   When,
                   null,
                   Settings.HashAlgorithm
               ).Current;

        #endregion


        #region TheTokenOfTheMomentIsAccepted()

        [Test]
        public void TheTokenOfTheMomentIsAccepted()
        {

            var settings = Settings();

            Assert.That(settings.Matches(TokenAt(settings, Noon), Noon), Is.True);

        }

        #endregion

        #region TheTokenBeforeAndTheTokenAfterAreAcceptedToo()

        /// <summary>
        /// Two clocks are never quite the same, and a token that was right when
        /// it was sent must still be right when it arrives.
        /// </summary>
        [Test]
        public void TheTokenBeforeAndTheTokenAfterAreAcceptedToo()
        {

            var settings = Settings(ValidityTime: TimeSpan.FromSeconds(30));

            Assert.Multiple(() => {

                Assert.That(settings.Matches(TokenAt(settings, Noon.AddSeconds(-30)), Noon), Is.True,
                            "The token of the slot before was refused, so a station whose clock is a moment behind cannot sign in.");

                Assert.That(settings.Matches(TokenAt(settings, Noon.AddSeconds(30)), Noon), Is.True,
                            "The token of the slot after was refused, so a station whose clock is a moment ahead cannot sign in.");

            });

        }

        #endregion

        #region ATokenFromWellOutsideTheWindowIsRefused()

        /// <summary>
        /// The tolerance is three slots wide and no wider. A token from an hour
        /// ago being accepted would make the whole exercise pointless.
        /// </summary>
        [Test]
        public void ATokenFromWellOutsideTheWindowIsRefused()
        {

            var settings = Settings(ValidityTime: TimeSpan.FromSeconds(30));

            Assert.Multiple(() => {
                Assert.That(settings.Matches(TokenAt(settings, Noon.AddMinutes(-60)), Noon), Is.False);
                Assert.That(settings.Matches(TokenAt(settings, Noon.AddSeconds(-90)), Noon), Is.False);
                Assert.That(settings.Matches(TokenAt(settings, Noon.AddSeconds( 90)), Noon), Is.False);
            });

        }

        #endregion

        #region ATokenOfAnotherSecretIsRefused()

        [Test]
        public void ATokenOfAnotherSecretIsRefused()
        {

            var ours    = Settings();
            var theirs  = Settings(SharedSecret: "a-completely-different-secret");

            Assert.That(ours.Matches(TokenAt(theirs, Noon), Noon), Is.False);

        }

        #endregion

        #region NothingAtAllIsRefused()

        [Test]
        public void NothingAtAllIsRefused()
        {

            var settings = Settings();

            Assert.Multiple(() => {
                Assert.That(settings.Matches(null, Noon), Is.False);
                Assert.That(settings.Matches("",   Noon), Is.False);
            });

        }

        #endregion


        #region ASecretThatIsLeftOutIsMadeUp()

        [Test]
        public void ASecretThatIsLeftOutIsMadeUp()
        {

            Assert.That(TOTPSettings.TryCreate(null, null, null, null, null,
                                               out var settings, out var generated, out var error),
                        Is.True, error);

            Assert.Multiple(() => {
                Assert.That(generated,                Is.Not.Null);
                Assert.That(generated!.Length,        Is.GreaterThanOrEqualTo(TOTPSettings.MinSecretLength));
                Assert.That(settings!.SharedSecret,   Is.EqualTo(generated));
            });

        }

        #endregion

        #region ASecretHermodWouldThrowOnIsRefusedHereFirst()

        /// <summary>
        /// Hermod trims the secret and throws on anything short or spaced. A
        /// controller that stored one anyway would turn that into an exception
        /// at the moment a charging station tries to sign in, which is the
        /// worst possible moment to find out.
        /// </summary>
        [Test]
        public void ASecretHermodWouldThrowOnIsRefusedHereFirst()
        {

            Assert.Multiple(() => {

                Assert.That(TOTPSettings.TryCreate("short", null, null, null, null, out _, out _, out var tooShort),
                            Is.False);
                Assert.That(tooShort, Does.Contain("16"));

                Assert.That(TOTPSettings.TryCreate("a secret with spaces in it", null, null, null, null,
                                                   out _, out _, out var spaced),
                            Is.False);
                Assert.That(spaced, Does.Contain("whitespace"));

                Assert.That(TOTPSettings.TryCreate(new String('x', TOTPSettings.MaxSecretLength + 1), null, null, null, null,
                                                   out _, out _, out var tooLong),
                            Is.False);
                Assert.That(tooLong, Is.Not.Null);

            });

        }

        #endregion

        #region AnAlphabetThatWouldMakeEveryTokenTheSameIsRefused()

        [Test]
        public void AnAlphabetThatWouldMakeEveryTokenTheSameIsRefused()
        {

            Assert.Multiple(() => {

                Assert.That(TOTPSettings.TryCreate(TheSecret, null, null, "x", null, out _, out _, out var oneCharacter),
                            Is.False);
                Assert.That(oneCharacter, Does.Contain("two different"));

                Assert.That(TOTPSettings.TryCreate(TheSecret, null, null, "aabb", null, out _, out _, out var repeated),
                            Is.False);
                Assert.That(repeated, Does.Contain("twice"));

            });

        }

        #endregion

        #region ATokenLongerThanTheHashIsRefused()

        /// <summary>
        /// The token is read out of the HMAC as a ring buffer, so asking for
        /// more characters than the hash has bytes buys nothing and only looks
        /// stronger.
        /// </summary>
        [Test]
        public void ATokenLongerThanTheHashIsRefused()
        {

            Assert.Multiple(() => {

                Assert.That(TOTPSettings.TryCreate(TheSecret, null, 33, null, TOTPHashAlgorithm.SHA256,
                                                   out _, out _, out var tooLong),
                            Is.False);
                Assert.That(tooLong, Does.Contain("32"));

                // The very same length is fine on a longer hash.
                Assert.That(TOTPSettings.TryCreate(TheSecret, null, 33, null, TOTPHashAlgorithm.SHA384,
                                                   out _, out _, out var fine),
                            Is.True, fine);

            });

        }

        #endregion

        #region AValidityNobodyCouldUseIsRefused()

        [Test]
        public void AValidityNobodyCouldUseIsRefused()
        {

            Assert.Multiple(() => {

                Assert.That(TOTPSettings.TryCreate(TheSecret, TimeSpan.FromSeconds(1), null, null, null,
                                                   out _, out _, out var tooShort),
                            Is.False);
                Assert.That(tooShort, Is.Not.Null);

                Assert.That(TOTPSettings.TryCreate(TheSecret, TimeSpan.FromHours(1), null, null, null,
                                                   out _, out _, out var tooLong),
                            Is.False);
                Assert.That(tooLong, Is.Not.Null);

            });

        }

        #endregion


        #region TheSharedSecretIsNotInWhatThePageReads()

        /// <summary>
        /// The one credential here that can be used as it stands, so the one
        /// that must not travel to a page anybody who may read the
        /// configuration can open.
        /// </summary>
        [Test]
        public void TheSharedSecretIsNotInWhatThePageReads()
        {

            var settings = Settings();

            var forThePage = settings.ToJSON();
            var forTheFile = settings.ToJSON(WithSecret: true);

            Assert.Multiple(() => {

                Assert.That(forThePage["sharedSecret"],          Is.Null,
                            "The shared secret was handed to the web interface.");
                Assert.That(forThePage.ToString(),               Does.Not.Contain(TheSecret));

                Assert.That(forTheFile.Value<String>("sharedSecret"), Is.EqualTo(TheSecret),
                            "The file has to carry it: the controller computes tokens from it.");

            });

        }

        #endregion

        #region WhatWasWrittenIsWhatIsReadBack()

        [Test]
        public void WhatWasWrittenIsWhatIsReadBack()
        {

            var settings = Settings(ValidityTime:   TimeSpan.FromSeconds(45),
                                    Length:         20,
                                    Alphabet:       "0123456789",
                                    HashAlgorithm:  TOTPHashAlgorithm.SHA512);

            Assert.That(TOTPSettings.TryParse(settings.ToJSON(WithSecret: true), out var read, out var error),
                        Is.True, error);

            Assert.That(read, Is.EqualTo(settings));

        }

        #endregion

        #region AConfigurationWithoutItsSecretIsRefusedRatherThanMadeUp()

        /// <summary>
        /// A file that lost its secret must say so. Making one up would mean
        /// the controller quietly starts expecting tokens no charging station
        /// can produce.
        /// </summary>
        [Test]
        public void AConfigurationWithoutItsSecretIsRefusedRatherThanMadeUp()
        {

            var json = new JObject(
                           new JProperty("validitySeconds", 30),
                           new JProperty("length",          12)
                       );

            Assert.Multiple(() => {
                Assert.That(TOTPSettings.TryParse(json, out _, out var error), Is.False);
                Assert.That(error, Does.Contain("sharedSecret"));
            });

        }

        #endregion

        #region TheConfigHandedToHermodIsNotTheBoundKind()

        /// <summary>
        /// TLS channel binding needs exporter material, which .NET does not
        /// offer on an SslStream - so everything here is the unbound kind, and
        /// the config says so rather than leaving it to a default.
        /// </summary>
        [Test]
        public void TheConfigHandedToHermodIsNotTheBoundKind()
        {

            var config = Settings().ToTOTPConfig();

            Assert.Multiple(() => {
                Assert.That(config.UseTLSExporterMaterial,  Is.False);
                Assert.That(config.SharedSecret,            Is.EqualTo(TheSecret));
            });

        }

        #endregion

    }

}
