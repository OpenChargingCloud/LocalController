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

using cloud.charging.open.LocalController.Configuration;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// What a local controller does with its two files when it is built, and
    /// what it refuses to do.
    /// </summary>
    /// <remarks>
    /// Built, not started: everything here is decided in the constructor, and
    /// a controller that was only built has not scheduled anything or bound a
    /// socket.
    /// </remarks>
    public class StartupTests
    {

        #region Data

        private String directory = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("startup");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void RemoveTheDirectory()
            => TestControllers.Remove(directory);

        #endregion


        #region AFirstStartMakesUpAPasswordAndWritesItDown()

        /// <summary>
        /// Nobody can sign in to a web interface whose login is not set yet,
        /// and an unauthenticated setup page would be a door of its own. So the
        /// password is made up, handed back once, and kept only as a hash.
        /// </summary>
        [Test]
        public async Task AFirstStartMakesUpAPasswordAndWritesItDown()
        {

            await using var controller = TestControllers.New(directory, TestControllers.Offline);

            var loginFile = Path.Combine(directory, "web-login.json");

            Assert.Multiple(() => {

                Assert.That(controller.GeneratedPassword, Is.Not.Null.And.Not.Empty);
                Assert.That(controller.Sessions.Username, Is.EqualTo("root"));
                Assert.That(File.Exists(loginFile),       Is.True);

                var written = File.ReadAllText(loginFile);

                Assert.That(written, Does.Not.Contain(controller.GeneratedPassword!),
                            "The password this controller made up was written to its file in the clear.");
                Assert.That(written, Does.Contain("$pbkdf2"));

            });

        }

        #endregion

        #region ASecondStartUsesTheLoginItFindsAndMakesUpNothing()

        [Test]
        public async Task ASecondStartUsesTheLoginItFindsAndMakesUpNothing()
        {

            String firstPassword;

            await using (var first = TestControllers.New(directory, TestControllers.Offline))
            {
                firstPassword = first.GeneratedPassword!;
            }

            await using var second = TestControllers.New(directory, TestControllers.Offline);

            Assert.Multiple(() => {
                Assert.That(second.GeneratedPassword, Is.Null,
                            "A controller that found a login file made up another password anyway.");
                Assert.That(second.Sessions.Login.Verify("root", firstPassword), Is.True,
                            "The login from the file is not the one the first start wrote.");
            });

        }

        #endregion

        #region AnUnreadableLoginFileStopsTheController()

        /// <summary>
        /// Papering over it with a new password would lock out whoever owns the
        /// old one without saying why.
        /// </summary>
        [Test]
        public void AnUnreadableLoginFileStopsTheController()
        {

            File.WriteAllText(Path.Combine(directory, "web-login.json"), "{ not json at all");

            var problem = Assert.Throws<InvalidOperationException>(
                              () => TestControllers.New(directory, TestControllers.Offline)
                          );

            Assert.That(problem!.Message, Does.Contain("web-login.json"));

        }

        #endregion

        #region AnUnreadableConfigurationStopsTheController()

        /// <summary>
        /// Somebody wrote down what their controller is and got it wrong.
        /// Quietly running as something else would be worse than stopping.
        /// </summary>
        [Test]
        public void AnUnreadableConfigurationStopsTheController()
        {

            File.WriteAllText(Path.Combine(directory, "configuration.json"), "{ dns: [ unquoted");

            // Configuration: null, so that the broken file written above is
            // left exactly as it is.
            var problem = Assert.Throws<InvalidOperationException>(
                              () => TestControllers.New(directory)
                          );

            Assert.That(problem!.Message, Does.Contain("configuration.json"));

        }

        #endregion

        #region AControllerWithNoFilesRunsOnItsDefaults()

        [Test]
        public async Task AControllerWithNoFilesRunsOnItsDefaults()
        {

            await using var controller = TestControllers.New(directory);

            Assert.Multiple(() => {
                Assert.That(controller.DNSEnabled,           Is.True);
                Assert.That(controller.NTSEnabled,           Is.True);
                Assert.That(controller.Node.Id.ToString(),   Is.EqualTo(OCPPConfiguration.DefaultNodeId));
                Assert.That(controller.Node.VendorName,      Is.EqualTo(OCPPConfiguration.DefaultVendorName));
                Assert.That(controller.Version,              Is.Not.Empty);
                Assert.That(controller.CreatedAt,            Is.Not.EqualTo(default(DateTimeOffset)));
            });

        }

        #endregion

        #region TheFileDecidesWhoThisControllerIsInOCPP()

        /// <summary>
        /// Read once, at the start. What the file says beats what the
        /// constructor was handed, and what it does not mention is left alone.
        /// </summary>
        [Test]
        public async Task TheFileDecidesWhoThisControllerIsInOCPP()
        {

            var configuration = new JObject(
                                    new JProperty("nts",  new JObject(new JProperty("enabled", false))),
                                    new JProperty("ocpp", new JObject(
                                        new JProperty("nodeId",      "lc-in-the-file"),
                                        new JProperty("vendorName",  "Somebody Else")
                                    ))
                                );

            await using var controller = TestControllers.New(directory, configuration);

            Assert.Multiple(() => {
                Assert.That(controller.Node.Id.ToString(),  Is.EqualTo("lc-in-the-file"));
                Assert.That(controller.Node.VendorName,     Is.EqualTo("Somebody Else"));
                // Not mentioned, so the default stands.
                Assert.That(controller.Node.Model,          Is.EqualTo(OCPPConfiguration.DefaultModel));
            });

        }

        #endregion

        #region TheClockIsSetBeforeAnythingAsksTheTime()

        /// <summary>
        /// The event log stamps its entries with the controller's clock, and it
        /// is built inside the constructor - so a controller handed a clock has
        /// to be using it from its very first line, or the log reads the system
        /// one and cannot be held against anything.
        /// </summary>
        [Test]
        public async Task TheClockIsSetBeforeAnythingAsksTheTime()
        {

            var clock = TestClock.At(2000, 1, 1);

            await using var controller = TestControllers.New(directory, TestControllers.Offline, clock);

            Assert.Multiple(() => {

                Assert.That(controller.CreatedAt,   Is.EqualTo(clock.Now));
                Assert.That(controller.TimeProvider, Is.SameAs(clock));

                // Everything the controller said while it was being built.
                Assert.That(controller.Log.Count, Is.GreaterThan(0),
                            "A controller that said nothing while starting up cannot show this.");

                Assert.That(controller.Log.Recent(100).Select(entry => entry.Timestamp),
                            Is.All.EqualTo(clock.Now),
                            "Something was logged against a clock other than the controller's own.");

            });

        }

        #endregion

        #region ASwitchedOffTimeClientScheduleNothing()

        /// <summary>
        /// The whole reason the fixtures write that section: switched off, no
        /// timer is put on the network at all.
        /// </summary>
        [Test]
        public async Task ASwitchedOffTimeClientSchedulesNothing()
        {

            await using var controller = TestControllers.New(directory, TestControllers.Offline);

            await controller.Start();

            Assert.Multiple(() => {

                Assert.That(controller.NTSEnabled, Is.False);

                Assert.That(controller.Log.Recent(200).Any(entry => entry.Message.Contains("not being checked")),
                            Is.True,
                            "A controller with its time client switched off did not say that it is not checking its clock.");

                Assert.That(controller.Log.Recent(200).Any(entry => entry.Message.Contains("will be checked against")),
                            Is.False,
                            "A controller with its time client switched off scheduled a check anyway.");

            });

            await controller.Stop();

        }

        #endregion

    }

}
