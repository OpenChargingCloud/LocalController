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

        /// <summary>
        /// How long the controller below may take to reach a CSMS that has come
        /// up. It dials again a second after a loss at first, and never more
        /// than two apart - see AControllerThatDials(DialsAgainQuickly).
        /// </summary>
        private static readonly TimeSpan BackWithin = TimeSpan.FromSeconds(15);

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
            upstream           = ACSMS(upstreamDirectory, csmsPort);

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

        #region (private static) ACSMS(Directory, Port)

        /// <summary>
        /// A controller standing in for the CSMS, listening on the given port
        /// and ready to let 'lc002' in with a password: built, not started.
        /// </summary>
        private static LocalController ACSMS(String  Directory,
                                             UInt16  Port)
        {

            var csms = TestControllers.New(
                           Directory,
                           new JObject(
                               new JProperty("nts", new JObject(new JProperty("enabled", false))),
                               new JProperty("ocppServer", new JObject(
                                   new JProperty("enabled",           true),
                                   new JProperty("address",           "127.0.0.1"),
                                   new JProperty("port",              Port),
                                   new JProperty("securityProfiles",  new JArray(1)),
                                   new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1"))
                               ))
                           )
                       );

            if (!csms.StationLogins.TrySetPassword("lc002", ThePassword, null, "The controller below", out _, out var error))
                throw new InvalidOperationException($"The test's own upstream login was refused: {error}");

            return csms;

        }

        #endregion

        #region (private) AControllerThatDials(...)

        /// <summary>
        /// The controller below, configured to report to the one above.
        /// </summary>
        /// <param name="DialsAgainQuickly">Whether it dials again a second after a loss, and never more than two apart, for a test of coming back that should not be a test of patience.</param>
        private LocalController AControllerThatDials(Boolean  Enabled            = true,
                                                     Byte     SecurityProfile    = 1,
                                                     String?  URL                = null,
                                                     String?  Username           = "lc002",
                                                     String?  Password           = ThePassword,
                                                     Boolean  DialsAgainQuickly  = false)
        {

            var csms       = new JObject(
                                 new JProperty("enabled",          Enabled),
                                 new JProperty("url",              URL ?? $"ws://127.0.0.1:{csmsPort}"),
                                 new JProperty("securityProfile",  SecurityProfile)
                             );

            if (DialsAgainQuickly)
            {
                csms.Add("reconnectInitialDelay",  1);
                csms.Add("reconnectMaxDelay",      2);
            }

            var controller = TestControllers.New(
                                 downstreamDirectory,
                                 new JObject(
                                     new JProperty("nts",  new JObject(new JProperty("enabled", false))),
                                     new JProperty("ocpp", new JObject(new JProperty("nodeId", "lc002"))),
                                     new JProperty("csms", csms)
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

        #region (private) UntilItIsBack(CSMS)

        /// <summary>
        /// Wait until the CSMS has a connection from the controller below and
        /// the controller says it has one too, or until BackWithin is up.
        /// </summary>
        /// <remarks>
        /// Both, because either can come first: the CSMS has the connection as
        /// soon as it has answered the upgrade, and the controller hears of it
        /// only once that answer has arrived.
        /// </remarks>
        private async Task UntilItIsBack(LocalController CSMS)
        {

            var giveUp = DateTimeOffset.UtcNow + BackWithin;

            while (DateTimeOffset.UtcNow < giveUp &&
                   !(CSMS.StationServer?.WebSocketConnections.Any() == true && downstream?.CSMSConnected == true))
            {
                await Task.Delay(100);
            }

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

                // An answer that means no is not asked again: a wrong password
                // does not get better for being tried every few seconds.
                Assert.That(downstream.CSMSLastProblem,  Does.Contain("refused").And.Contain("not dialled again"),
                            "What the controller says of a refusal does not say that it is final.");

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


        #region ACSMSThatIsDownAtTheStartIsReachedOnceItIsUp()

        /// <summary>
        /// A CSMS that is not there when this controller starts is reached once
        /// it is - without the start waiting for it, on the one client the line
        /// has however often it was dialled, and said to be connected, as one
        /// reached at once is.
        /// </summary>
        /// <remarks>
        /// The client was given its reconnect policy once its first attempt had
        /// come back, and one whose first attempt had failed had ended by then:
        /// a controller started while its CSMS was down stayed away from it
        /// until it was started again.
        /// </remarks>
        [Test]
        public async Task ACSMSThatIsDownAtTheStartIsReachedOnceItIsUp()
        {

            var laterPort       = TestControllers.FreePort();
            var laterDirectory  = TestControllers.TemporaryDirectory("csms-later");

            downstream          = AControllerThatDials(URL:                $"ws://127.0.0.1:{laterPort}",
                                                       DialsAgainQuickly:  true);

            var took            = System.Diagnostics.Stopwatch.StartNew();
            await downstream.Start();
            took.Stop();

            Assert.Multiple(() => {
                Assert.That(downstream.CSMSConnected,    Is.False,
                            "The controller got through to a CSMS that was not there yet, so this test tests nothing.");
                Assert.That(took.Elapsed,                Is.LessThan(TimeSpan.FromSeconds(10)),
                            "The controller waited for a CSMS that was not there before it said it had started.");
                Assert.That(downstream.CSMSLastProblem,  Does.Contain("could not be reached").And.Contain("dialled again by itself"),
                            "What the controller says of a CSMS that is not there does not say that it goes on dialling.");
            });

            // And what it says stays what the first attempt found once the
            // client has tried again, rather than turning into a connection
            // "lost" that never was: twice, so that the first time has been told.
            var client          = downstream.Node.OCPPWebSocketClients.
                                      OfType<org.GraphDefined.Vanaheimr.Hermod.WebSocket.WebSocketClient>().
                                      Single();

            var triedBy         = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

            while (DateTimeOffset.UtcNow < triedBy && client.ReconnectAttempts < 2)
                await Task.Delay(100);

            Assert.Multiple(() => {
                Assert.That(client.ReconnectAttempts,    Is.GreaterThanOrEqualTo(2),
                            "The client did not try again by itself.");
                Assert.That(downstream.CSMSLastProblem,  Does.Contain("could not be reached").And.Not.Contain("lost"),
                            "A line that was never up is said to have been lost.");
            });

            var later           = ACSMS(laterDirectory, laterPort);

            try
            {

                await later.Start();
                await UntilItIsBack(later);

                Assert.That(later.StationServer?.WebSocketConnections.Count(),  Is.GreaterThan(0),
                            $"The CSMS came up after the controller had started, and the controller did not reach it within {BackWithin.TotalSeconds:F0} s.");

                Assert.Multiple(() => {
                    Assert.That(downstream.CSMSConnected,                      Is.True,        "The CSMS has the controller's connection, and the controller says it is not connected.");
                    Assert.That(downstream.CSMSConnectedSince,                 Is.Not.Null,    "The controller does not say since when it is connected.");
                    Assert.That(downstream.CSMSLastProblem,                    Is.Null,        "The controller still names a problem with a line that is up.");
                    Assert.That(downstream.Node.OCPPWebSocketClients.Count(),  Is.EqualTo(1),  "Dialling the CSMS again made a client each time.");
                });

            }
            finally
            {
                await later.DisposeAsync();
                TestControllers.Remove(laterDirectory);
            }

        }

        #endregion

        #region ACSMSThatRestartsIsReachedAgain()

        /// <summary>
        /// A CSMS that goes away and comes back - stopped and started again, as
        /// for an update - is reached again, on the client the line had, and
        /// the controller says since when.
        /// </summary>
        /// <remarks>
        /// "Dialled once, kept up by the client", the line has always said of
        /// itself. The client stopped for good, though, when it was the other
        /// side that ended its connection, whatever its policy said.
        /// </remarks>
        [Test]
        public async Task ACSMSThatRestartsIsReachedAgain()
        {

            downstream    = AControllerThatDials(DialsAgainQuickly: true);

            await downstream.Start();

            Assert.That(downstream.CSMSConnected, Is.True, downstream.CSMSLastProblem);

            var wentAway  = DateTimeOffset.UtcNow;

            await upstream.DisposeAsync();

            // The same CSMS again, from the same directory and on the same port:
            // what a restart is.
            upstream      = ACSMS(upstreamDirectory, csmsPort);

            await upstream.Start();
            await UntilItIsBack(upstream);

            Assert.That(upstream.StationServer?.WebSocketConnections.Count(),  Is.GreaterThan(0),
                        $"The CSMS was restarted, and the controller did not come back to it within {BackWithin.TotalSeconds:F0} s.");

            Assert.Multiple(() => {
                Assert.That(downstream.CSMSConnected,                      Is.True,                  "The CSMS has the controller's connection again, and the controller says it is not connected.");
                Assert.That(downstream.CSMSConnectedSince ?? DateTimeOffset.MinValue,
                                                                           Is.GreaterThan(wentAway), "The controller does not say since when it is connected again.");
                Assert.That(downstream.CSMSLastProblem,                    Is.Null,                  "The controller still names a problem with a line that is up again.");
                Assert.That(downstream.Node.OCPPWebSocketClients.Count(),  Is.EqualTo(1),            "Coming back made a client of its own.");
            });

        }

        #endregion

        #region AStoppedControllerDialsNoMore()

        /// <summary>
        /// A controller stopped while it was still dialling a CSMS that was not
        /// there does not reach it once it is: stopping ends the dialling, and
        /// nothing of a controller that is gone turns up at its CSMS later.
        /// </summary>
        /// <remarks>
        /// Since its client has its policy before the first attempt, a
        /// controller whose CSMS is down has something going on in the
        /// background, which its stopping has to end.
        /// </remarks>
        [Test]
        public async Task AStoppedControllerDialsNoMore()
        {

            var laterPort       = TestControllers.FreePort();
            var laterDirectory  = TestControllers.TemporaryDirectory("csms-later");

            downstream          = AControllerThatDials(URL:                $"ws://127.0.0.1:{laterPort}",
                                                       DialsAgainQuickly:  true);

            await downstream.Start();

            Assert.That(downstream.CSMSLastProblem, Does.Contain("dialled again by itself"),
                        "The controller does not go on dialling, so this test tests nothing.");

            await downstream.Stop();

            var later           = ACSMS(laterDirectory, laterPort);

            try
            {

                await later.Start();

                // More than twice the longest wait between two attempts.
                await Task.Delay(TimeSpan.FromSeconds(5));

                Assert.That(later.StationServer?.WebSocketConnections.Count(), Is.EqualTo(0),
                            "A controller that had been stopped reached its CSMS once it was up.");

            }
            finally
            {
                await later.DisposeAsync();
                TestControllers.Remove(laterDirectory);
            }

        }

        #endregion

    }

}
