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
        /// What a CSMS knows 'lc002' by once its password has been changed at
        /// its end, and not at this one.
        /// </summary>
        private const String AnotherPassword = "another-password-long-enough-for-ocpp";

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
        /// <param name="Password">The password it lets 'lc002' in with.</param>
        private static LocalController ACSMS(String  Directory,
                                             UInt16  Port,
                                             String  Password  = ThePassword)
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

            if (!csms.StationLogins.TrySetPassword("lc002", Password, null, "The controller below", out _, out var error))
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

        #region (private static) TheClientOf(Controller)

        /// <summary>
        /// The client a controller's node made for its line up to the CSMS.
        /// </summary>
        private static org.GraphDefined.Vanaheimr.Hermod.WebSocket.WebSocketClient TheClientOf(LocalController Controller)

            => Controller.Node.OCPPWebSocketClients.
                   OfType<org.GraphDefined.Vanaheimr.Hermod.WebSocket.WebSocketClient>().
                   Single();

        #endregion

        #region (private) UntilItHasGivenUp(Client)

        /// <summary>
        /// Wait until the client has stopped dialling, or until BackWithin is
        /// up - and then, for a moment, until the controller says it is not
        /// dialled again.
        /// </summary>
        /// <remarks>
        /// In that order, because the client stops before anything is told of
        /// the answer that stopped it.
        /// </remarks>
        private async Task UntilItHasGivenUp(org.GraphDefined.Vanaheimr.Hermod.WebSocket.WebSocketClient Client)
        {

            var giveUp = DateTimeOffset.UtcNow + BackWithin;

            while (DateTimeOffset.UtcNow < giveUp && Client.KeepsTrying)
                await Task.Delay(100);

            var saidBy = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);

            while (DateTimeOffset.UtcNow < saidBy && downstream?.CSMSLastProblem?.Contains("not dialled again") != true)
                await Task.Delay(50);

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
                Assert.That(downstream.CSMSLastProblem,  Does.Contain("refused this local controller").And.Contain("not dialled again"),
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
            var client          = TheClientOf(downstream);

            var triedBy         = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

            while (DateTimeOffset.UtcNow < triedBy && client.ReconnectAttempts < 2)
                await Task.Delay(100);

            Assert.Multiple(() => {
                Assert.That(client.ReconnectAttempts,    Is.GreaterThanOrEqualTo(2),
                            "The client did not try again by itself.");
                Assert.That(downstream.CSMSLastProblem,  Does.Contain("could not be reached").And.Not.Contain("connection to the CSMS was lost"),
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

        #region ACSMSThatRestartsAndRefusesIsNotSaidToBeTriedAgain()

        /// <summary>
        /// A CSMS that comes back from a restart and turns this controller away
        /// - its password changed at the other end - ends the dialling, and the
        /// controller says so rather than that it is trying again.
        /// </summary>
        /// <remarks>
        /// An answer that means no ends the client's attempts, and the line went
        /// on saying what it had said when the connection was lost: that it was
        /// trying again in a second or two, of a client that had stopped.
        /// </remarks>
        [Test]
        public async Task ACSMSThatRestartsAndRefusesIsNotSaidToBeTriedAgain()
        {

            downstream  = AControllerThatDials(DialsAgainQuickly: true);

            await downstream.Start();

            Assert.That(downstream.CSMSConnected, Is.True, downstream.CSMSLastProblem);

            var client  = TheClientOf(downstream);

            await upstream.DisposeAsync();

            // The same CSMS again, with another password for this controller.
            upstream    = ACSMS(upstreamDirectory, csmsPort, AnotherPassword);

            await upstream.Start();
            await UntilItHasGivenUp(client);

            Assert.Multiple(() => {
                Assert.That(client.KeepsTrying,          Is.False,
                            "The client still dials a CSMS that turns it away, so this test tests nothing.");
                Assert.That(downstream.CSMSConnected,    Is.False);
                Assert.That(downstream.CSMSLastProblem,  Does.Contain("refused this local controller").And.Contain("not dialled again"),
                            "The controller says of a CSMS that turned it away that it is dialled again.");
            });

        }

        #endregion

        #region ACSMSThatComesUpAndRefusesIsNotSaidToBeTriedAgain()

        /// <summary>
        /// The same for a CSMS that was not there when this controller started:
        /// once it is up and turns the controller away, the controller no longer
        /// says that it is dialled again by itself.
        /// </summary>
        [Test]
        public async Task ACSMSThatComesUpAndRefusesIsNotSaidToBeTriedAgain()
        {

            var laterPort       = TestControllers.FreePort();
            var laterDirectory  = TestControllers.TemporaryDirectory("csms-later");

            downstream          = AControllerThatDials(URL:                $"ws://127.0.0.1:{laterPort}",
                                                       DialsAgainQuickly:  true);

            await downstream.Start();

            Assert.That(downstream.CSMSLastProblem, Does.Contain("dialled again by itself"),
                        "The controller does not go on dialling, so this test tests nothing.");

            var client          = TheClientOf(downstream);
            var later           = ACSMS(laterDirectory, laterPort, AnotherPassword);

            try
            {

                await later.Start();
                await UntilItHasGivenUp(client);

                Assert.Multiple(() => {
                    Assert.That(client.KeepsTrying,          Is.False,
                                "The client still dials a CSMS that turns it away, so this test tests nothing.");
                    Assert.That(downstream.CSMSConnected,    Is.False);
                    Assert.That(downstream.CSMSLastProblem,  Does.Contain("refused this local controller").And.Contain("not dialled again"),
                                "The controller says of a CSMS that turned it away that it is dialled again.");
                });

            }
            finally
            {
                await later.DisposeAsync();
                TestControllers.Remove(laterDirectory);
            }

        }

        #endregion

        #region ACSMSStillStartingIsNotSaidToHaveRefused()

        /// <summary>
        /// A CSMS behind a reverse proxy that answers 503 while the CSMS is
        /// still starting has not said no: the controller goes on dialling, does
        /// not say that it was turned away, and gets through once the CSMS is up.
        /// </summary>
        /// <remarks>
        /// The other side of the answer that ends the dialling. 408, 429 and the
        /// 5xx are what a server says while it cannot yet, and the client comes
        /// back from them - so only an answer after which the client has stopped
        /// is one this controller may call final.
        /// </remarks>
        [Test]
        public async Task ACSMSStillStartingIsNotSaidToHaveRefused()
        {

            var laterPort       = TestControllers.FreePort();
            var laterDirectory  = TestControllers.TemporaryDirectory("csms-later");
            var answered        = 0;

            // Something on the port that answers every upgrade with 503, as a
            // reverse proxy does while the CSMS behind it is still starting.
            var proxy           = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, laterPort);

            proxy.Start();

            var answering       = Task.Run(async () => {

                                      while (true)
                                      {

                                          System.Net.Sockets.TcpClient tcp;

                                          try
                                          {
                                              tcp = await proxy.AcceptTcpClientAsync();
                                          }
                                          catch
                                          {
                                              return;
                                          }

                                          using (tcp)
                                          {

                                              var stream  = tcp.GetStream();
                                              var buffer  = new Byte[4096];
                                              var request = "";
                                              var read    = 0;

                                              while (!request.Contains("\r\n\r\n") &&
                                                     (read = await stream.ReadAsync(buffer)) > 0)
                                              {
                                                  request += System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                                              }

                                              Interlocked.Increment(ref answered);

                                              await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                                                        "HTTP/1.1 503 Service Unavailable\r\n" +
                                                        "Connection: close\r\n" +
                                                        "Content-Length: 0\r\n\r\n"));

                                          }

                                      }

                                  });

            downstream          = AControllerThatDials(URL:                $"ws://127.0.0.1:{laterPort}",
                                                       DialsAgainQuickly:  true);

            try
            {

                await downstream.Start();

                var client      = TheClientOf(downstream);
                var giveUp      = DateTimeOffset.UtcNow + BackWithin;

                while (DateTimeOffset.UtcNow < giveUp && Volatile.Read(ref answered) < 3)
                    await Task.Delay(100);

                Assert.Multiple(() => {
                    Assert.That(Volatile.Read(ref answered),  Is.GreaterThanOrEqualTo(3),
                                "The proxy was not asked again and again, so this test tests nothing.");
                    Assert.That(client.KeepsTrying,           Is.True,
                                "The client took a 503 for an answer that means no.");
                    Assert.That(downstream.CSMSLastProblem,   Does.Not.Contain("refused this local controller").And.Not.Contain("not dialled again"),
                                "The controller says that a CSMS still starting turned it away.");
                });

            }
            finally
            {
                proxy.Stop();
                await answering;
            }

            // And once the CSMS itself is there, on the same port.
            var later           = ACSMS(laterDirectory, laterPort);

            try
            {

                await later.Start();
                await UntilItIsBack(later);

                Assert.Multiple(() => {
                    Assert.That(later.StationServer?.WebSocketConnections.Count(),  Is.GreaterThan(0),
                                $"The CSMS came up behind the proxy's 503s, and the controller did not reach it within {BackWithin.TotalSeconds:F0} s.");
                    Assert.That(downstream.CSMSConnected,                           Is.True, downstream.CSMSLastProblem);
                });

            }
            finally
            {
                await later.DisposeAsync();
                TestControllers.Remove(laterDirectory);
            }

        }

        #endregion

        #region HangingUpInTheMiddleOfAnAttemptIsNotARefusal()

        /// <summary>
        /// A controller stopped while an attempt of its client waits for the
        /// CSMS to answer does not say that the CSMS turned it away.
        /// </summary>
        /// <remarks>
        /// Hanging up ends the client's dialling, and the attempt it cuts off
        /// ends with an answer of its own making - which is nobody's refusal,
        /// however much it looks like one once the client has stopped.
        /// </remarks>
        [Test]
        public async Task HangingUpInTheMiddleOfAnAttemptIsNotARefusal()
        {

            var laterPort       = TestControllers.FreePort();

            downstream          = AControllerThatDials(URL:                $"ws://127.0.0.1:{laterPort}",
                                                       DialsAgainQuickly:  true);

            await downstream.Start();

            Assert.That(downstream.CSMSLastProblem, Does.Contain("dialled again by itself"),
                        "The controller does not go on dialling, so this test tests nothing.");

            // Something on the port that takes the connection and the upgrade,
            // and never answers either.
            var asked           = 0;
            var held            = new List<System.Net.Sockets.TcpClient>();
            var silent          = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, laterPort);

            silent.Start();

            var listening       = Task.Run(async () => {

                                      while (true)
                                      {

                                          System.Net.Sockets.TcpClient tcp;

                                          try
                                          {
                                              tcp = await silent.AcceptTcpClientAsync();
                                          }
                                          catch
                                          {
                                              return;
                                          }

                                          lock (held)
                                              held.Add(tcp);

                                          var stream  = tcp.GetStream();
                                          var buffer  = new Byte[4096];
                                          var request = "";

                                          try
                                          {
                                              while (!request.Contains("\r\n\r\n"))
                                              {
                                                  var read = await stream.ReadAsync(buffer);
                                                  if (read == 0)
                                                      break;
                                                  request += System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                                              }
                                          }
                                          catch
                                          { }

                                          if (request.Contains("\r\n\r\n"))
                                              Interlocked.Increment(ref asked);

                                      }

                                  });

            try
            {

                var giveUp = DateTimeOffset.UtcNow + BackWithin;

                while (DateTimeOffset.UtcNow < giveUp && Volatile.Read(ref asked) < 1)
                    await Task.Delay(50);

                Assert.That(Volatile.Read(ref asked), Is.GreaterThanOrEqualTo(1),
                            "The controller never asked the silent end, so this test tests nothing.");

                await downstream.Stop();

                // The attempt that was cut off ends when its connection does,
                // which may be after this controller has stopped - so the
                // silent end lets go now, and what is said of it is asked for
                // once it has had a moment to be said.
                lock (held)
                    foreach (var tcp in held)
                        tcp.Dispose();

                await Task.Delay(TimeSpan.FromSeconds(1));

                Assert.That(downstream.CSMSLastProblem ?? "", Does.Not.Contain("refused this local controller"),
                            "Hanging up in the middle of an attempt was said to be the CSMS's refusal.");

            }
            finally
            {

                silent.Stop();

                lock (held)
                    foreach (var tcp in held)
                        tcp.Dispose();

                await listening;

            }

        }

        #endregion

    }

}
