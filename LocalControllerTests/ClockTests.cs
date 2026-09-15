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

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// What time this local controller thinks it is, and what that is worth.
    /// </summary>
    /// <remarks>
    /// The word this controller must never guess is "legal", and these are the
    /// tests of it not guessing. Each builds a controller and does not start
    /// it: <c>ClockJSON()</c> is the whole subject, no socket is needed for it,
    /// and an unstarted controller is also one that has not scheduled a check
    /// against a time server - so nothing here goes near the network.
    /// </remarks>
    public class ClockTests
    {

        #region Data

        private String directory = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("clock");
        }

        [TearDown]
        public void RemoveTheDirectory()
        {
            TestControllers.Remove(directory);
        }

        #endregion


        #region TheTimeIsTheControllersOwnClockAndSaysSo()

        /// <summary>
        /// Two different questions, kept apart: what time it is here, and
        /// whether that is any good. The first is always this controller's own
        /// system clock, and the answer says so out loud - because the check
        /// that follows does not set it.
        /// </summary>
        [Test]
        public async Task TheTimeIsTheControllersOwnClockAndSaysSo()
        {

            await using var controller = TestControllers.New(directory);

            var clock = controller.ClockJSON();

            Assert.Multiple(() => {
                Assert.That(clock.Value<String>("source"), Is.EqualTo("system"));
                Assert.That(DateTimeOffset.TryParse(clock.Value<String>("now"), out _), Is.True);
            });

        }

        #endregion

        #region AnUncheckedClockIsNotLegalTime()

        /// <summary>
        /// Nothing can tell from a hostname whether a server disseminates a
        /// country's legal time - that is a fact about an institution. So with
        /// nobody having said, the answer is "unverified" and names the reason.
        /// </summary>
        [Test]
        public async Task AnUncheckedClockIsNotLegalTime()
        {

            await using var controller = TestControllers.New(directory);

            var clock = controller.ClockJSON();

            Assert.Multiple(() => {
                Assert.That(clock.Value<Boolean>("legal"),     Is.False);
                Assert.That(clock.Value<String> ("why"),       Is.EqualTo("notClaimed"));
                Assert.That(clock.Value<String> ("authority"), Is.Null);
            });

        }

        #endregion

        #region AClaimedAuthorityAloneIsStillNotLegalTime()

        /// <summary>
        /// The operator naming an authority is one of four things that have to
        /// hold, and each is a different way of not knowing. With the claim in
        /// place and no check ever made, the answer moves from "notClaimed" to
        /// "neverChecked" and stays unverified.
        /// </summary>
        [Test]
        public async Task AClaimedAuthorityAloneIsStillNotLegalTime()
        {

            await using var controller = TestControllers.New(
                                             directory,
                                             new JObject(
                                                 new JProperty("nts", new JObject(
                                                     new JProperty("legalTimeAuthority", "PTB")
                                                 ))
                                             )
                                         );

            var clock = controller.ClockJSON();

            Assert.Multiple(() => {
                Assert.That(controller.LegalTimeAuthority,     Is.EqualTo("PTB"));
                Assert.That(clock.Value<String> ("authority"), Is.EqualTo("PTB"));
                Assert.That(clock.Value<Boolean>("legal"),     Is.False);
                Assert.That(clock.Value<String> ("why"),       Is.EqualTo("neverChecked"));
            });

        }

        #endregion

        #region ASwitchedOffTimeClientSaysThatIsWhy()

        /// <summary>
        /// A claim that cannot be checked because the time client is off is a
        /// different kind of not knowing from one that was never made, and the
        /// answer distinguishes them - so that somebody reading it knows what
        /// to go and fix.
        /// </summary>
        [Test]
        public async Task ASwitchedOffTimeClientSaysThatIsWhy()
        {

            await using var controller = TestControllers.New(
                                             directory,
                                             new JObject(
                                                 new JProperty("nts", new JObject(
                                                     new JProperty("enabled",            false),
                                                     new JProperty("legalTimeAuthority", "PTB")
                                                 ))
                                             )
                                         );

            var clock = controller.ClockJSON();

            Assert.Multiple(() => {
                Assert.That(controller.NTSEnabled,          Is.False);
                Assert.That(clock.Value<Boolean>("legal"),  Is.False);
                Assert.That(clock.Value<String> ("why"),    Is.EqualTo("ntsOff"));
                Assert.That(clock["nts"]?.Value<String>("server"), Is.Null,
                            "A time client that is switched off should not be named as the server in use.");
            });

        }

        #endregion

        #region TheToleranceAndTheMaxAgeAreReported()

        /// <summary>
        /// Sent as facts rather than left to be interpreted: a page saying the
        /// time is unverified can then say what it would have taken.
        /// </summary>
        [Test]
        public async Task TheToleranceAndTheMaxAgeAreReported()
        {

            await using var controller = TestControllers.New(
                                             directory,
                                             new JObject(
                                                 new JProperty("nts", new JObject(
                                                     new JProperty("legalTimeToleranceSeconds",  0.5),
                                                     new JProperty("legalTimeMaxAgeSeconds",     900)
                                                 ))
                                             )
                                         );

            var clock = controller.ClockJSON();

            Assert.Multiple(() => {
                Assert.That(controller.LegalTimeTolerance,          Is.EqualTo(TimeSpan.FromSeconds(0.5)));
                Assert.That(controller.LegalTimeMaxAge,             Is.EqualTo(TimeSpan.FromMinutes(15)));
                Assert.That(clock.Value<Double>("toleranceSeconds"), Is.EqualTo(0.5));
                Assert.That(clock.Value<Double>("maxAgeSeconds"),    Is.EqualTo(900));
            });

        }

        #endregion

    }

}
