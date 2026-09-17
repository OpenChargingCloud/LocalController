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
    /// This local controller dialling the charging station management system
    /// above it.
    /// </summary>
    /// <remarks>
    /// <b>The CSMS here is another local controller.</b> Its charging station
    /// port speaks OCPP over a WebSocket and checks HTTP Basic Authentication
    /// against a list, which is exactly what the controller below has to get
    /// past - so the two ends of this test are the two ends of the real thing,
    /// and nothing about the connection is simulated.
    /// </remarks>
    public class CSMSConnectionTests
    {

        #region Data

        private const String ThePassword = "a-password-long-enough-for-ocpp";

        private String           upstreamDirectory    = default!;
        private String           downstreamDirectory  = default!;
        private LocalController  upstream             = default!;
        private LocalController? downstream;
        private UInt16           csmsPort;

        #endregion

        #region SetUp / TearDown

        /// <summary>
        /// One controller standing in for the CSMS, listening and ready to let
        /// 'lc002' in with a password.
        /// </summary>
        [SetUp]
        public async Task StartSomethingToDial()
        {

            upstreamDirectory  = TestControllers.TemporaryDirectory("csms-upstream");
            Directory.CreateDirectory(upstreamDirectory);

            csmsPort           = TestControllers.FreePort();

            upstream           = TestControllers.New(
                                     upstreamDirectory,
                                     new JObject(
                                         new JProperty("nts", new JObject(new JProperty("enabled", false))),
                                         new JProperty("ocppServer", new JObject(
                                             new JProperty("enabled",           true),
                                             new JProperty("address",           "127.0.0.1"),
                                             new JProperty("port",              csmsPort),
                                             new JProperty("securityProfiles",  new JArray(1)),
                                             new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1"))
                                         ))
                                     )
                                 );

            if (!upstream.StationLogins.TrySetPassword("lc002", ThePassword, null, "The controller below", out _, out var error))
                throw new InvalidOperationException($"The test's own upstream login was refused: {error}");

            await upstream.Start();

            downstreamDirectory = TestControllers.TemporaryDirectory("csms-downstream");
            Directory.CreateDirectory(downstreamDirectory);

        }

        [TearDown]
        public async Task StopThem()
        {

            if (downstream is not null)
                await downstream.DisposeAsync();

            await upstream.DisposeAsync();

            TestControllers.Remove(downstreamDirectory);
            TestControllers.Remove(upstreamDirectory);

        }

        #endregion

        #region (private) AControllerThatDials(...)

        /// <summary>
        /// The controller below, configured to report to the one above.
        /// </summary>
        private LocalController AControllerThatDials(Boolean  Enabled          = true,
                                                     Byte     SecurityProfile  = 1,
                                                     String?  URL              = null,
                                                     String?  Username         = "lc002",
                                                     String?  Password         = ThePassword)
        {

            var controller = TestControllers.New(
                                 downstreamDirectory,
                                 new JObject(
                                     new JProperty("nts",  new JObject(new JProperty("enabled", false))),
                                     new JProperty("ocpp", new JObject(new JProperty("nodeId", "lc002"))),
                                     new JProperty("csms", new JObject(
                                         new JProperty("enabled",          Enabled),
                                         new JProperty("url",              URL ?? $"ws://127.0.0.1:{csmsPort}"),
                                         new JProperty("securityProfile",  SecurityProfile)
                                     ))
                                 )
                             );

            if (Username is not null && Password is not null &&
                !controller.CSMSLogin.TrySetPassword(Username, Password, out var error))
            {
                throw new InvalidOperationException($"The test's own credentials were refused: {error}");
            }

            return controller;

        }

        #endregion


        #region TheControllerSignsInToTheCSMS()

        [Test]
        public async Task TheControllerSignsInToTheCSMS()
        {

            downstream = AControllerThatDials();

            await downstream.Start();

            Assert.Multiple(() => {
                Assert.That(downstream.CSMSEnabled,         Is.True);
                Assert.That(downstream.CSMSConnected,       Is.True, downstream.CSMSLastProblem);
                Assert.That(downstream.CSMSConnectedSince,  Is.Not.Null);
                Assert.That(downstream.CSMSLastProblem,     Is.Null);
            });

        }

        #endregion

        #region TheCSMSSeesItArrive()

        /// <summary>
        /// Not only that this end thinks it got in: the other end has a
        /// connection from it.
        /// </summary>
        [Test]
        public async Task TheCSMSSeesItArrive()
        {

            downstream = AControllerThatDials();

            await downstream.Start();

            Assert.That(downstream.CSMSConnected, Is.True, downstream.CSMSLastProblem);

            Assert.That(upstream.StationServer?.WebSocketConnections.Count(), Is.GreaterThan(0),
                        "The CSMS end has no connection, so the controller below is talking to nobody.");

        }

        #endregion

        #region AWrongPasswordIsRefusedAndSaidSo()

        [Test]
        public async Task AWrongPasswordIsRefusedAndSaidSo()
        {

            downstream = AControllerThatDials(Password: "not-the-password-at-all");

            await downstream.Start();

            Assert.Multiple(() => {

                Assert.That(downstream.CSMSConnected,    Is.False,
                            "The CSMS let this controller in with the wrong password.");

                Assert.That(downstream.CSMSLastProblem,  Is.Not.Null,
                            "The connection failed and nothing says why.");

                // And a controller whose backend refused it is still a
                // controller: the port below has to be open regardless.
                Assert.That(downstream.OCPPServerRunning || downstream.OCPPServerEnabled == false, Is.True);

            });

        }

        #endregion

        #region WithoutCredentialsItDoesNotEvenDial()

        [Test]
        public async Task WithoutCredentialsItDoesNotEvenDial()
        {

            downstream = AControllerThatDials(Username: null, Password: null);

            await downstream.Start();

            Assert.Multiple(() => {
                Assert.That(downstream.CSMSConnected,    Is.False);
                Assert.That(downstream.CSMSLastProblem,  Does.Contain("credentials"));
            });

        }

        #endregion

        #region SwitchedOffItDialsNothing()

        [Test]
        public async Task SwitchedOffItDialsNothing()
        {

            downstream = AControllerThatDials(Enabled: false);

            await downstream.Start();

            Assert.Multiple(() => {
                Assert.That(downstream.CSMSEnabled,      Is.False);
                Assert.That(downstream.CSMSConnected,    Is.False);
                Assert.That(downstream.CSMSLastProblem,  Is.Null,
                            "A connection nobody asked for was attempted and then reported as a problem.");
            });

        }

        #endregion

        #region ProfileThreeSaysWhatIsMissingRatherThanFailingAtTheOtherEnd()

        /// <summary>
        /// The client certificate store of this controller's own does not exist
        /// yet. That is said here, where somebody can read it, rather than left
        /// to the CSMS to refuse for a reason nobody at this end would see.
        /// </summary>
        [Test]
        public async Task ProfileThreeSaysWhatIsMissingRatherThanFailingAtTheOtherEnd()
        {

            downstream = AControllerThatDials(SecurityProfile:  3,
                                              URL:              $"wss://127.0.0.1:{csmsPort}");

            await downstream.Start();

            Assert.Multiple(() => {
                Assert.That(downstream.CSMSConnected,    Is.False);
                Assert.That(downstream.CSMSLastProblem,  Does.Contain("client certificate"));
            });

        }

        #endregion

        #region WhatThePageIsShownCarriesNoPassword()

        /// <summary>
        /// This is the one credential in the whole controller that is stored so
        /// that it can be said out loud. All the more reason for it never to
        /// reach a page.
        /// </summary>
        [Test]
        public async Task WhatThePageIsShownCarriesNoPassword()
        {

            downstream = AControllerThatDials();

            await downstream.Start();

            var shown = downstream.CSMSConfigurationJSON();

            Assert.Multiple(() => {

                Assert.That(shown.ToString(),                                        Does.Not.Contain(ThePassword));
                Assert.That(shown["credentials"]?.Value<String>("username"),         Is.EqualTo("lc002"));
                Assert.That(shown["credentials"]?.Value<Boolean>("hasPassword"),     Is.True);
                Assert.That(shown["state"]?.Value<Boolean>("connected"),             Is.True);

                // And the file does carry it, or this controller could never
                // sign in at all.
                Assert.That(File.ReadAllText(downstream.CSMSLogin.Path),             Does.Contain(ThePassword));

            });

        }

        #endregion

    }

}
