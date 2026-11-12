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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// A charging station's Authorize, sent on to the CSMS above this local
    /// controller, and its answer back down.
    /// </summary>
    /// <remarks>
    /// The OCPP node forwards an Authorize for the stations on a list of its
    /// own, which nothing in this controller fills: every card of every
    /// station was answered "Unauthorized!" here, and no CSMS ever heard of
    /// it. A plain WebSocket client stands in for the station; a second local
    /// controller, answering as "CSMS", for the CSMS.
    /// </remarks>
    [TestFixture]
    public class AuthorizationTests
    {

        #region Data

        private const String ThePassword     = "a-password-long-enough-for-ocpp";
        private const String UplinkPassword  = "the-controllers-password-at-its-csms";

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        private String            upstreamDirectory    = default!;
        private String            downstreamDirectory  = default!;
        private LocalController?  upstream;
        private LocalController?  downstream;
        private UInt16            csmsPort;

        /// <summary>
        /// The Authorize requests that reached the CSMS: who asked, and about which card.
        /// </summary>
        private readonly ConcurrentQueue<(String Path, String Card)>  atTheCSMS  = new ();

        /// <summary>
        /// What the CSMS says of a card.
        /// </summary>
        private AuthorizationStatus  cardStatus  = AuthorizationStatus.Accepted;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeTheDirectories()
        {

            upstreamDirectory    = TestControllers.TemporaryDirectory("authorize-upstream");
            downstreamDirectory  = TestControllers.TemporaryDirectory("authorize-downstream");

            Directory.CreateDirectory(upstreamDirectory);
            Directory.CreateDirectory(downstreamDirectory);

            cardStatus = AuthorizationStatus.Accepted;
            atTheCSMS.Clear();

        }

        [TearDown]
        public async Task StopThem()
        {

            if (downstream is not null)
                await downstream.DisposeAsync();

            if (upstream is not null)
                await upstream.DisposeAsync();

            downstream  = null;
            upstream    = null;

            TestControllers.Remove(downstreamDirectory);
            TestControllers.Remove(upstreamDirectory);

        }

        #endregion

        #region (private) ACSMS() / AControllerBelowIt(WithACSMS)

        private static JObject NoTimeServers
            => new (new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))));

        /// <summary>
        /// Something that answers as "CSMS": a controller of its own, which
        /// says of every card what the test tells it to and keeps who asked.
        /// </summary>
        private async Task ACSMS()
        {

            upstream = await TestPorts.StartedOnFreshPorts(() => {

                var csms = TestControllers.New(
                               upstreamDirectory,
                               new JObject(
                                   new JProperty("nts",        NoTimeServers),
                                   new JProperty("ocppServer", new JObject(
                                       new JProperty("enabled",           true),
                                       new JProperty("address",           "127.0.0.1"),
                                       new JProperty("port",              csmsPort = TestPorts.Free()),
                                       new JProperty("securityProfiles",  new JArray(1)),
                                       new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1"))
                                   ))
                               )
                           );

                if (!csms.StationLogins.TrySetPassword("lc002", UplinkPassword, null, "The controller below", out _, out var error))
                    throw new InvalidOperationException($"The test's own upstream login was refused: {error}");

                csms.Node.OCPP.IN.AnycastIds.Add(NetworkingNode_Id.CSMS);

                csms.Node.OCPP.IN.OnAuthorize += (timestamp, sender, connection, request, cancellationToken) => {

                    atTheCSMS.Enqueue((request.NetworkPath.ToString(), request.IdToken.Value.ToString()));

                    return Task.FromResult(
                               new protocols.OCPPv2_1.CSMS.AuthorizeResponse(
                                   Request:      request,
                                   IdTokenInfo:  new IdTokenInfo(cardStatus)
                               )
                           );

                };

                return csms;

            });

        }

        /// <summary>
        /// The controller under test, with a station port and a login for one
        /// station - reporting to the CSMS above, or to nobody.
        /// </summary>
        private async Task<LocalController> AControllerBelowIt(Boolean WithACSMS)
        {

            downstream = await TestPorts.StartedOnFreshPorts(() => {

                var controller = TestControllers.New(
                                     downstreamDirectory,
                                     new JObject(
                                         new JProperty("nts",        NoTimeServers),
                                         new JProperty("ocpp",       new JObject(new JProperty("nodeId", "lc002"))),
                                         new JProperty("csms",       WithACSMS
                                             ? new JObject(
                                                   new JProperty("enabled",          true),
                                                   new JProperty("url",              $"ws://127.0.0.1:{csmsPort}"),
                                                   new JProperty("securityProfile",  1),
                                                   new JProperty("nextHopNodeId",    "CSMS"),

                                                   // What stands in for the CSMS here is a plain OCPP
                                                   // node, which knows nothing of the overlay network
                                                   // a controller dials by default.
                                                   new JProperty("networkingMode",   "standard")
                                               )
                                             : new JObject(
                                                   new JProperty("enabled",          false)
                                               )),
                                         new JProperty("ocppServer", new JObject(
                                             new JProperty("enabled",           true),
                                             new JProperty("address",           "127.0.0.1"),
                                             new JProperty("port",              TestPorts.Free()),
                                             new JProperty("securityProfiles",  new JArray(1)),
                                             new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1"))
                                         ))
                                     )
                                 );

                if (WithACSMS && !controller.CSMSLogin.TrySetPassword("lc002", UplinkPassword, out var loginError))
                    throw new InvalidOperationException($"The test's own credentials were refused: {loginError}");

                if (!controller.StationLogins.TrySetPassword("cs001", ThePassword, null, "A station", out _, out var error))
                    throw new InvalidOperationException($"The test's own station login was refused: {error}");

                return controller;

            });

            if (WithACSMS)
            {

                var watch = Stopwatch.StartNew();

                while (!downstream.CSMSConnected && watch.Elapsed < Patience)
                    await Task.Delay(20);

                Assert.That(downstream.CSMSConnected, Is.True, "the controller did not connect to its CSMS");

            }

            return downstream;

        }

        #endregion

        #region (private) AStationAsksAbout(Controller, Card)

        /// <summary>
        /// A station signed in at the controller with a password asks about a
        /// card: the frame it is answered with, and how long that took.
        /// </summary>
        private static async Task<(JArray Answer, TimeSpan After)> AStationAsksAbout(LocalController Controller, String Card)
        {

            using var station = new ClientWebSocket();

            station.Options.AddSubProtocol("ocpp2.1");
            station.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"cs001:{ThePassword}")));

            await station.ConnectAsync(new Uri($"ws://127.0.0.1:{Controller.OCPPServerSettings.TCPPort}"), CancellationToken.None);

            var request = new JArray(2, "asked-1", "Authorize", new JObject(
                              new JProperty("idToken", new JObject(
                                  new JProperty("idToken",  Card),
                                  new JProperty("type",     "ISO14443")
                              ))
                          ));

            var watch = Stopwatch.StartNew();

            await station.SendAsync(Encoding.UTF8.GetBytes(request.ToString(Newtonsoft.Json.Formatting.None)), WebSocketMessageType.Text, true, CancellationToken.None);

            using var patience  = new CancellationTokenSource(Patience);
            var buffer          = new Byte[1 << 16];

            while (true)
            {

                var text = new StringBuilder();
                WebSocketReceiveResult result;

                do
                {
                    result = await station.ReceiveAsync(buffer, patience.Token);
                    text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                var frame = JArray.Parse(text.ToString());

                // Whatever the controller asks by itself in between is not the answer.
                if (frame[1]?.Value<String>() != "asked-1")
                    continue;

                station.Abort();

                return (frame, watch.Elapsed);

            }

        }

        #endregion


        #region AStationsAuthorizeIsAnsweredByTheCSMS(Status)

        /// <summary>
        /// The station's Authorize reaches the CSMS, and what the CSMS says of
        /// the card is what the station is told - yes or no.
        /// </summary>
        [TestCase("Accepted")]
        [TestCase("Blocked")]
        public async Task AStationsAuthorizeIsAnsweredByTheCSMS(String Status)
        {

            await ACSMS();

            var controller = await AControllerBelowIt(WithACSMS: true);

            cardStatus = AuthorizationStatus.Parse(Status);

            var (answer, _) = await AStationAsksAbout(controller, "04A2B3C4D5E6F7");

            Assert.Multiple(() => {

                Assert.That(answer[0].Value<Int32>(),                               Is.EqualTo(3), $"The Authorize was answered with an error: {answer}");
                Assert.That(answer[2]?["idTokenInfo"]?.Value<String>("status"),     Is.EqualTo(Status), answer.ToString());

                Assert.That(atTheCSMS.Count,                                        Is.EqualTo(1), "The CSMS was not asked exactly once.");
                Assert.That(atTheCSMS.FirstOrDefault().Card,                        Is.EqualTo("04A2B3C4D5E6F7"));
                // In standard networking mode the CSMS is asked by the
                // controller, as for everything else it sends on: which
                // station stood behind it is the controller's to know.
                Assert.That(atTheCSMS.FirstOrDefault().Path,                        Does.Contain("lc002"));

                Assert.That(controller.Log.Recent(200).Select(entry => entry.Message),
                            Has.Some.Contains("The Authorize of 'cs001' is sent on to the CSMS."));

            });

        }

        #endregion

        #region AControllerWithoutACSMS_AnswersAtOnce_AndWithNoYes()

        /// <summary>
        /// A controller that reports to no CSMS has nobody to ask and decides
        /// on no card: the station is answered at once, not with "Accepted",
        /// and the log says why.
        /// </summary>
        [Test]
        public async Task AControllerWithoutACSMS_AnswersAtOnce_AndWithNoYes()
        {

            var controller = await AControllerBelowIt(WithACSMS: false);

            var (answer, after) = await AStationAsksAbout(controller, "04A2B3C4D5E6F7");

            Assert.Multiple(() => {

                Assert.That(after,  Is.LessThan(TimeSpan.FromSeconds(5)), "The station was left to wait.");

                // An error, or an answer that is no yes.
                if (answer[0].Value<Int32>() == 3)
                    Assert.That(answer[2]?["idTokenInfo"]?.Value<String>("status"), Is.Not.EqualTo("Accepted"), answer.ToString());

                Assert.That(controller.Log.Recent(200).Select(entry => entry.Message),
                            Has.Some.Contains("The Authorize of 'cs001' was not sent on").And.Contains("reports to no CSMS"));

            });

        }

        #endregion

        #region AControllerWhoseCSMSIsGone_AnswersAtOnce_AndWithNoYes()

        /// <summary>
        /// The line to the CSMS went down: the station is answered at once
        /// rather than left to wait for an answer that cannot come.
        /// </summary>
        [Test]
        public async Task AControllerWhoseCSMSIsGone_AnswersAtOnce_AndWithNoYes()
        {

            await ACSMS();

            var controller = await AControllerBelowIt(WithACSMS: true);

            await upstream!.DisposeAsync();
            upstream = null;

            var watch = Stopwatch.StartNew();

            while (controller.CSMSConnected && watch.Elapsed < Patience)
                await Task.Delay(20);

            Assert.That(controller.CSMSConnected, Is.False, "the controller did not notice that its CSMS is gone");

            var (answer, after) = await AStationAsksAbout(controller, "04A2B3C4D5E6F7");

            Assert.Multiple(() => {

                Assert.That(after,  Is.LessThan(TimeSpan.FromSeconds(5)), "The station was left to wait.");

                if (answer[0].Value<Int32>() == 3)
                    Assert.That(answer[2]?["idTokenInfo"]?.Value<String>("status"), Is.Not.EqualTo("Accepted"), answer.ToString());

                Assert.That(atTheCSMS, Is.Empty);

                Assert.That(controller.Log.Recent(200).Select(entry => entry.Message),
                            Has.Some.Contains("The Authorize of 'cs001' was not sent on").And.Contains("the line to the CSMS is down"));

            });

        }

        #endregion

    }

}
