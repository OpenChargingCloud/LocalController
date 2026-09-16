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

using System.Net.WebSockets;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// A charging station actually connecting - the one thing the stores and
    /// the JSON API cannot prove between them.
    /// </summary>
    /// <remarks>
    /// Over security profile 1, on the loopback address: what is being tested
    /// is who is let in and who is turned away, and that is the same decision
    /// whether or not the connection underneath it is encrypted. The
    /// certificates have their own tests.
    ///
    /// A plain WebSocket client rather than a charging station of the OCPP
    /// library: this is about the door, not about what is said after it, and a
    /// client that speaks nothing but the handshake cannot pass a test by
    /// accident.
    /// </remarks>
    public class ChargingStationConnectionTests : ALocalControllerTests
    {

        #region Data

        private const String ThePassword = "a-password-long-enough-for-ocpp";

        #endregion

        #region (override) Configuration

        /// <summary>
        /// A controller whose charging station server is switched on, on the
        /// loopback address and a port nothing else has.
        /// </summary>
        protected override JObject Configuration

            => new (

                   new JProperty("nts",  new JObject(new JProperty("enabled", false))),

                   new JProperty("ocppServer", new JObject(
                       new JProperty("enabled",           true),
                       new JProperty("address",           "127.0.0.1"),
                       new JProperty("port",              TestControllers.FreePort()),
                       new JProperty("securityProfiles",  new JArray(1)),
                       new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1"))
                   ))

               );

        #endregion

        #region (private) Connect(Id, Password, Subprotocol)

        /// <summary>
        /// One charging station trying to get in.
        /// </summary>
        private async Task<(Boolean Connected, String? Why, String? Subprotocol)> Connect(String   Id,
                                                                                          String   Password,
                                                                                          String   Subprotocol = "ocpp2.1")
        {

            using var client = new ClientWebSocket();

            client.Options.AddSubProtocol(Subprotocol);

            // The way a charging station signs in under security profiles 1
            // and 2: HTTP Basic Authentication on the upgrade request.
            client.Options.SetRequestHeader(
                "Authorization",
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Id}:{Password}"))
            );

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            try
            {

                await client.ConnectAsync(
                          new Uri($"ws://127.0.0.1:{Controller.OCPPServerSettings.TCPPort}"),
                          timeout.Token
                      );

                var subprotocol = client.SubProtocol;

                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);

                return (true, null, subprotocol);

            }
            catch (Exception e)
            {
                return (false, e.Message, null);
            }

        }

        #endregion


        #region TheServerIsListeningWhenItIsSwitchedOn()

        [Test]
        public void TheServerIsListeningWhenItIsSwitchedOn()
        {

            Assert.Multiple(() => {
                Assert.That(Controller.OCPPServerEnabled,  Is.True);
                Assert.That(Controller.OCPPServerRunning,  Is.True);
                Assert.That(Controller.OCPPServerTLS,      Is.False,
                            "There is no certificate, so this port cannot be encrypted - and saying it is would be worse than not being it.");
                Assert.That(Controller.StationServer,      Is.Not.Null);
            });

        }

        #endregion

        #region AStationWithAPasswordGetsIn()

        [Test]
        public async Task AStationWithAPasswordGetsIn()
        {

            Assert.That(Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, "Ladepunkt 1", out _, out var error),
                        Is.True, error);

            var (connected, why, subprotocol) = await Connect("cs001", ThePassword);

            Assert.Multiple(() => {
                Assert.That(connected,    Is.True, why);
                Assert.That(subprotocol,  Is.EqualTo("ocpp2.1"));
            });

        }

        #endregion

        #region AStationThisControllerNeverHeardOfIsTurnedAway()

        [Test]
        public async Task AStationThisControllerNeverHeardOfIsTurnedAway()
        {

            var (connected, _, _) = await Connect("cs404", ThePassword);

            Assert.That(connected, Is.False,
                        "A charging station nobody added was let in.");

        }

        #endregion

        #region AWrongPasswordIsTurnedAway()

        [Test]
        public async Task AWrongPasswordIsTurnedAway()
        {

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out _);

            var (connected, _, _) = await Connect("cs001", "not-the-password-at-all");

            Assert.That(connected, Is.False);

        }

        #endregion

        #region AStationWithNoCredentialsAtAllIsTurnedAway()

        [Test]
        public async Task AStationWithNoCredentialsAtAllIsTurnedAway()
        {

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out _);

            using var client  = new ClientWebSocket();

            client.Options.AddSubProtocol("ocpp2.1");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var connected = true;

            try
            {
                await client.ConnectAsync(new Uri($"ws://127.0.0.1:{Controller.OCPPServerSettings.TCPPort}"), timeout.Token);
            }
            catch
            {
                connected = false;
            }

            Assert.That(connected, Is.False);

        }

        #endregion

        #region AStationThatWasSwitchedOffIsTurnedAwayAtOnce()

        /// <summary>
        /// Now, and not at the next start: a password that was taken away has
        /// to stop working while somebody is still standing there.
        /// </summary>
        [Test]
        public async Task AStationThatWasSwitchedOffIsTurnedAwayAtOnce()
        {

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out _);

            var (before, why, _) = await Connect("cs001", ThePassword);

            Assert.That(before, Is.True, why);

            Assert.That(Controller.StationLogins.TrySetEnabled("cs001", false, out var error), Is.True, error);

            var (after, _, _) = await Connect("cs001", ThePassword);

            Assert.That(after, Is.False,
                        "A charging station that was switched off was still let in.");

        }

        #endregion

        #region AStationThatWasRemovedIsTurnedAwayAtOnce()

        [Test]
        public async Task AStationThatWasRemovedIsTurnedAwayAtOnce()
        {

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out _);
            Controller.StationLogins.TryRemove     ("cs001", out _);

            var (connected, _, _) = await Connect("cs001", ThePassword);

            Assert.That(connected, Is.False);

        }

        #endregion

        #region AStationSpeakingAnotherOCPPVersionIsTurnedAway()

        [Test]
        public async Task AStationSpeakingAnotherOCPPVersionIsTurnedAway()
        {

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out _);

            var (connected, _, _) = await Connect("cs001", ThePassword, "ocpp1.6");

            Assert.That(connected, Is.False,
                        "A charging station speaking a version this controller does not offer was let in.");

        }

        #endregion

        #region WhatHappenedIsInTheLog()

        /// <summary>
        /// Both halves: who got in, and who was turned away and why. The second
        /// is the only record of somebody trying.
        /// </summary>
        [Test]
        public async Task WhatHappenedIsInTheLog()
        {

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out _);

            await Connect("cs001", ThePassword);
            await Connect("cs404", ThePassword);

            var messages = Controller.Log.Recent(200).Select(entry => entry.Message).ToArray();

            Assert.Multiple(() => {

                Assert.That(messages.Any(message => message.Contains("'cs001' signed in")), Is.True,
                            "A charging station signing in was not logged.");

                Assert.That(messages.Any(message => message.Contains("turned away") &&
                                                    message.Contains("cs404")), Is.True,
                            "A charging station being turned away was not logged.");

                Assert.That(messages.Any(message => message.Contains("'cs001' is connected")), Is.True,
                            "A charging station connecting was not logged.");

            });

        }

        #endregion

        #region SwitchingTheServerOffStopsLettingAnybodyIn()

        [Test]
        public async Task SwitchingTheServerOffStopsLettingAnybodyIn()
        {

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out _);

            Assert.That(Controller.TryUpdateOCPPServerConfiguration(
                            new JObject(new JProperty("enabled", false)),
                            out var error
                        ), Is.True, error);

            Assert.That(Controller.OCPPServerRunning, Is.False);

            var (connected, _, _) = await Connect("cs001", ThePassword);

            Assert.That(connected, Is.False,
                        "The charging station server was switched off and went on answering.");

        }

        #endregion

        #region SwitchingTheServerBackOnLetsThemInAgain()

        /// <summary>
        /// The other direction, and the one that can go wrong quietly: the
        /// switch is thrown from inside a request, so starting the server
        /// happens on a thread that is itself answering one.
        /// </summary>
        [Test]
        public async Task SwitchingTheServerBackOnLetsThemInAgain()
        {

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out _);

            Controller.TryUpdateOCPPServerConfiguration(new JObject(new JProperty("enabled", false)), out _);

            Assert.That(Controller.OCPPServerRunning, Is.False);

            Assert.That(Controller.TryUpdateOCPPServerConfiguration(
                            new JObject(new JProperty("enabled", true)),
                            out var error
                        ), Is.True, error);

            Assert.That(Controller.OCPPServerRunning, Is.True);

            var (connected, why, _) = await Connect("cs001", ThePassword);

            Assert.That(connected, Is.True, why);

        }

        #endregion

        #region WhatTheCertificatesSayReachesTheControllersLog()

        /// <summary>
        /// The stores say things; the controller is what writes them down. A
        /// warning that never left the store is a warning nobody gets.
        /// </summary>
        [Test]
        public void WhatTheCertificatesSayReachesTheControllersLog()
        {

            Assert.That(Controller.ServerCertificates.TryCreateKey(
                            "lc001.example.org",
                            [ "lc001.example.org" ],
                            null,
                            out var id,
                            out var csr,
                            out var error
                        ), Is.True, error);

            using var ca          = TestCA.Create("Test CA");
            using var certificate = ca.Sign(csr!, DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddDays(9));

            Assert.That(Controller.ServerCertificates.TryAddCertificate(
                            ca.ChainPEM(certificate),
                            [ "lc001.example.org" ],
                            out _, out _, out var problem
                        ), Is.True, problem);

            Controller.ServerCertificates.CheckExpiry([ "lc001.example.org" ]);

            var messages = Controller.Log.Recent(200).Select(entry => entry.Message).ToArray();

            Assert.Multiple(() => {

                Assert.That(messages.Any(message => message.Contains("key") && message.Contains(id!)), Is.True,
                            "Generating a key was not written to the log of this controller.");

                Assert.That(messages.Any(message => message.Contains("runs out in")), Is.True,
                            $"A certificate nine days from running out was not announced: {String.Join(" | ", messages.TakeLast(6))}");

            });

        }

        #endregion

        #region StoppingTheControllerLetsGoOfThePort()

        /// <summary>
        /// A charging station holds its connection open for as long as it is
        /// switched on, which is the same shape of problem as a browser hanging
        /// on the event stream: a shutdown that waited for it would wait
        /// forever.
        /// </summary>
        [Test]
        public async Task StoppingTheControllerLetsGoOfThePort()
        {

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out _);

            using var client  = new ClientWebSocket();
            client.Options.AddSubProtocol("ocpp2.1");
            client.Options.SetRequestHeader(
                "Authorization",
                "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"cs001:{ThePassword}"))
            );

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{Controller.OCPPServerSettings.TCPPort}"), timeout.Token);

            // Left open on purpose: this is a charging station that is simply
            // switched on, and the controller has to be able to stop anyway.
            var stopping = Controller.Stop();

            var finished = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(20)));

            Assert.That(finished, Is.SameAs(stopping),
                        "Stopping the controller did not return while a charging station was connected.");

            Assert.That(Controller.OCPPServerRunning, Is.False);

        }

        #endregion

    }

}
