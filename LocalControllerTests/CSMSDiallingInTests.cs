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

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Collections.Concurrent;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

using OCPPConnections = cloud.charging.open.protocols.OCPP.Node.Connections;

using static cloud.charging.open.LocalController.Tests.CSMSNetworkingModeTests;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// A CSMS this controller cannot dial dials the controller: on the port of the charging stations,
    /// under the name a connection the other end makes is written down with, by what that connection
    /// names. From then on it is the line up of the controller as much as one the controller dialled -
    /// what the stations send goes up over it, what it asks of a station goes down, and what only the
    /// CSMS asks of the controller it may ask - and the stations beside it on the same port are
    /// stations still.
    /// </summary>
    /// <remarks>
    /// Plain WebSocket clients at both ends rather than a CSMS and charging stations of the OCPP
    /// library: what is tested is who is let in as what, and what is passed on, frame by frame. The
    /// CSMS in the overlay network, where it says so in the header of its dial, and in the plain frame
    /// of OCPP.
    /// </remarks>
    [TestFixture(false)]
    [TestFixture(true)]
    public class CSMSDiallingInTests(Boolean Overlay) : ALocalControllerTests
    {

        #region (class) Line

        /// <summary>
        /// One end of a WebSocket to the station port: a CSMS that dialled, or a charging station.
        /// It keeps what it gets, and answers what it is asked.
        /// </summary>
        private sealed class Line : IAsyncDisposable
        {

            private readonly ClientWebSocket          socket  = new();
            private readonly CancellationTokenSource  ended   = new();
            private readonly ConcurrentQueue<JArray>  got     = new();
            private          Task?                    reading;

            /// <summary>
            /// What it is in the frames it writes: "CSMS", or the station.
            /// </summary>
            public String  Name    { get; }

            public Line(String Name)
            {
                this.Name = Name;
            }

            /// <summary>
            /// Everything that came.
            /// </summary>
            public JArray[] Got
                => [.. got];

            public Boolean IsOpen
                => socket.State == WebSocketState.Open;

            /// <summary>
            /// Dial, and say what the door answered: 101, or what it refused with.
            /// </summary>
            public async Task<HttpStatusCode> Dial(String   URL,
                                                   String   Login,
                                                   String   Password,
                                                   Boolean  InTheOverlayNetwork = false)
            {

                socket.Options.CollectHttpResponseDetails = true;
                socket.Options.AddSubProtocol("ocpp2.1");
                socket.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Login}:{Password}")));

                if (InTheOverlayNetwork)
                    socket.Options.SetRequestHeader("X-OCPP-NetworkingMode", "OverlayNetwork");

                using var timeout = new CancellationTokenSource(Guard);

                try
                {
                    await socket.ConnectAsync(new Uri(URL), timeout.Token);
                }
                catch (WebSocketException)
                {
                    return socket.HttpStatusCode;
                }

                reading = Task.Run(Read);

                return socket.HttpStatusCode;

            }

            private async Task Read()
            {

                var buffer = new Byte[64 * 1024];

                try
                {
                    while (!ended.IsCancellationRequested)
                    {

                        using var message = new MemoryStream();

                        WebSocketReceiveResult result;

                        do
                        {
                            result = await socket.ReceiveAsync(buffer, ended.Token);
                            message.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close)
                            return;

                        var frame = KeyPinsOverOCPPTests.Frame(Encoding.UTF8.GetString(message.ToArray()));

                        got.Enqueue(frame);

                        // A request: answered in the frame it came in, to whom it came from.
                        if (frame[0].Value<Int32>() == 2)
                            await Send(IsOverlay(frame)
                                           ? new JArray(3, SourceOf(frame), Name, IdOf(frame), AnswerTo(ActionOf(frame)))
                                           : new JArray(3, IdOf(frame), AnswerTo(ActionOf(frame))));

                    }
                }
                catch (Exception)
                {
                    // Closed under it: the end of the test, or the door letting go of it.
                }

            }

            private JObject AnswerTo(String Action)

                => Action switch {
                       "BootNotification"  => new JObject(
                                                  new JProperty("status",       "Accepted"),
                                                  new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o")),
                                                  new JProperty("interval",     300)
                                              ),
                       "Heartbeat"         => new JObject(
                                                  new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o"))
                                              ),
                       _                   => new JObject(new JProperty("answeredBy", Name))
                   };

            public async Task Send(JArray Frame)
            {

                using var timeout = new CancellationTokenSource(Guard);

                await socket.SendAsync(Encoding.UTF8.GetBytes(Frame.ToString(Formatting.None)), WebSocketMessageType.Text, true, timeout.Token);

            }

            /// <summary>
            /// The answer to the request of the given id, once there is one or the guard has passed.
            /// </summary>
            public async Task<JArray?> AnswerTo(String RequestId, TimeSpan? Patience = null)
            {

                var giveUp = DateTimeOffset.UtcNow + (Patience ?? Guard);

                do
                {

                    if (got.FirstOrDefault(frame => frame[0].Value<Int32>() is 3 or 4 && IdOf(frame) == RequestId) is JArray answer)
                        return answer;

                    await Task.Delay(20);

                }
                while (DateTimeOffset.UtcNow < giveUp);

                return null;

            }

            /// <summary>
            /// The first request of the given action, once there is one or the guard has passed.
            /// </summary>
            public async Task<JArray?> Asked(String Action, TimeSpan? Patience = null)
            {

                var giveUp = DateTimeOffset.UtcNow + (Patience ?? Guard);

                do
                {

                    if (got.FirstOrDefault(frame => frame[0].Value<Int32>() == 2 && ActionOf(frame) == Action) is JArray request)
                        return request;

                    await Task.Delay(20);

                }
                while (DateTimeOffset.UtcNow < giveUp);

                return null;

            }

            public async ValueTask DisposeAsync()
            {

                ended.Cancel();

                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "The test is over", timeout.Token);
                    }
                }
                catch
                { }

                socket.Dispose();
                ended. Dispose();

            }

        }

        #endregion

        #region Data

        private const           String    ControllerId   = "lc002";
        private const           String    CSMSName       = "csms-01";
        private const           String    ThePassword    = "a-password-long-enough-for-ocpp";
        private const           String    ItsPassword    = "what-the-csms-signs-in-with-here";

        private static readonly TimeSpan  Guard          = TimeSpan.FromSeconds(20);

        private readonly ConcurrentQueue<String>  said   = [];

        #endregion

        #region (override) Configuration

        /// <summary>
        /// A controller that reports to a CSMS and dials none, with its station server switched on, for
        /// passwords.
        /// </summary>
        protected override JObject Configuration
        {
            get
            {

                var configuration = TestControllers.Offline;

                configuration["ocpp"]       = new JObject(
                                                  new JProperty("nodeId",            ControllerId)
                                              );

                configuration["ocppServer"] = new JObject(
                                                  new JProperty("enabled",           true),
                                                  new JProperty("address",           "127.0.0.1"),
                                                  new JProperty("port",              TestPorts.Free()),
                                                  new JProperty("securityProfiles",  new JArray(1)),
                                                  new JProperty("subprotocols",      new JArray("ocpp2.1"))
                                              );

                configuration["csms"]       = new JObject(
                                                  new JProperty("enabled",           true)
                                              );

                return configuration;

            }
        }

        #endregion

        #region (private) helpers

        [SetUp]
        public void ListenToWhatItSays()
        {
            said.Clear();
            Controller.Log.OnLogged += entry => said.Enqueue(entry.Message);
        }

        private String Log
            => String.Join(Environment.NewLine, said);

        private String Mode
            => Overlay ? "overlay" : "standard";

        private String Door(String Name)
            => $"ws://127.0.0.1:{Controller.OCPPServerSettings.TCPPort}/{Name}";

        /// <summary>
        /// Write down the CSMS that dials: what it signs in with, and the connection the other end makes.
        /// </summary>
        private String WriteDownTheCSMS(String Name = CSMSName)
        {

            Assert.That(Controller.Connections.TryAddAuthentication("What the CSMS signs in with", "basic", Name, ItsPassword,
                                                                    out var login, out var error),
                        Is.True, error);

            Assert.That(Controller.Connections.TryAddConnection("The CSMS, dialling in", Door(Name), "CSMS", true, login, null,
                                                                out var id, out error,
                                                                Inbound: true),
                        Is.True, error);

            return id!;

        }

        /// <summary>
        /// Switch the connection of the CSMS on or off: "Connect by itself", which is what lets it in.
        /// </summary>
        private void LetIn(String Id, Boolean LetIn)
        {

            var connection = Controller.Connections.Connections.Single(one => one.Id == Id);

            Assert.That(Controller.Connections.TryUpdateConnection(Id, connection.Description, connection.URL.ToString(), "CSMS", LetIn,
                                                                   connection.AuthenticationId, null,
                                                                   out var error),
                        Is.True, error);

        }

        private String StatusOf(String Id)
            => Controller.ConnectionStates.TryGetValue(Id, out var state)
                   ? OCPPConnections.ConnectionState.AsText(state.Status)
                   : "nothing yet";

        private String SaidOf(String Id)
            => Controller.DialledConnections.TryGetValue(Id, out var itSaid) ? itSaid : "nothing yet";

        private async Task Until(Func<Boolean> Condition, String What)
        {

            var giveUp = DateTimeOffset.UtcNow + Guard;

            while (!Condition())
            {

                if (DateTimeOffset.UtcNow > giveUp)
                    Assert.Fail($"{What} did not happen ({Mode}).{Environment.NewLine}{Log}");

                await Task.Delay(25);

            }

        }

        /// <summary>
        /// The CSMS, dialled in and the line up of the controller.
        /// </summary>
        private async Task<(Line CSMS, String Id)> TheCSMSIsIn()
        {

            var id    = WriteDownTheCSMS();
            var csms  = new Line("CSMS");

            Assert.That(await csms.Dial(Door(CSMSName), CSMSName, ItsPassword, Overlay), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

            await Until(() => StatusOf(id) == "connected", "The CSMS being connected");

            return (csms, id);

        }

        /// <summary>
        /// A charging station on the station port, signed in with its password.
        /// </summary>
        private async Task<Line> AStation(String Id)
        {

            Assert.That(Controller.StationLogins.TrySetPassword(Id, ThePassword, null, null, out _, out var error), Is.True, error);

            var station = new Line(Id);

            Assert.That(await station.Dial(Door(Id), Id, ThePassword), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

            return station;

        }

        private static JObject SomeVariable

            => new (
                   new JProperty("getVariableData", new JArray(
                       new JObject(
                           new JProperty("component", new JObject(new JProperty("name", "EVQICtrlr"), new JProperty("instance", "TLSServer"))),
                           new JProperty("variable",  new JObject(new JProperty("name", "ActiveKeyPin")))
                       )
                   ))
               );

        #endregion


        #region TheCSMSThatDials_IsLetInByWhatItsConnectionNames()

        /// <summary>
        /// Written down, it is waited for. It is let in under its name with its password and with nothing
        /// else - and is the line up of the controller then, not a charging station of it.
        /// </summary>
        [Test]
        public async Task TheCSMSThatDials_IsLetInByWhatItsConnectionNames()
        {

            var id = WriteDownTheCSMS();

            await Until(() => StatusOf(id) == "waiting", "Waiting for the CSMS");

            Assert.Multiple(() => {
                Assert.That(SaidOf(id),                Does.Contain($"Waiting for '{CSMSName}' to dial in"));
                Assert.That(Controller.CSMSConnected,  Is.False);
            });

            await using var guessing  = new Line("CSMS");
            await using var stranger  = new Line("CSMS");
            await using var asAnother = new Line("CSMS");
            await using var csms      = new Line("CSMS");

            Assert.Multiple(async () => {
                Assert.That(await guessing. Dial(Door(CSMSName),  CSMSName,  ThePassword, Overlay), Is.EqualTo(HttpStatusCode.Unauthorized), "a password that is not its own");
                Assert.That(await stranger. Dial(Door("csms-02"), "csms-02", ItsPassword, Overlay), Is.EqualTo(HttpStatusCode.Unauthorized), "a name that is not written down");
                Assert.That(await asAnother.Dial(Door(CSMSName),  "csms-02", ItsPassword, Overlay), Is.EqualTo(HttpStatusCode.Unauthorized), "another login than its name");
            });

            Assert.That(StatusOf(id), Is.EqualTo("waiting"), "something that was turned away counted as the CSMS");

            Assert.That(await csms.Dial(Door(CSMSName), CSMSName, ItsPassword, Overlay), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

            await Until(() => StatusOf(id) == "connected", "The CSMS being connected");

            var state = Controller.StateJSON();

            Assert.Multiple(() => {
                Assert.That(Controller.CSMSConnected,                               Is.True);
                Assert.That(state["csms"]?["connected"]?.Value<Boolean>(),          Is.True);
                Assert.That(state["stations"]?["connected"]?.Value<Int32>(),        Is.Zero, "the CSMS is counted among the charging stations");
                Assert.That(state["stations"]?["seen"] as JArray,                   Is.Empty, "the CSMS is listed among the charging stations");
                Assert.That(Log,                                                    Does.Contain($"The back end '{CSMSName}' dialled this local controller"));
                Assert.That(Log,                                                    Does.Not.Contain(ItsPassword), "what it signed in with is in the log");
            });

        }

        #endregion

        #region WhatOnlyTheCSMSAsks_ItAsksOverTheLineItDialled()

        /// <summary>
        /// What only the CSMS asks of the controller - GetVariables, here - the CSMS that dialled may ask,
        /// and a charging station on the same port may not.
        /// </summary>
        [Test]
        public async Task WhatOnlyTheCSMSAsks_ItAsksOverTheLineItDialled()
        {

            var (csms, _) = await TheCSMSIsIn();

            await using var theCSMS  = csms;
            await using var cs001    = await AStation("cs001");

            await csms.Send(Overlay
                                ? new JArray(2, ControllerId, "CSMS", "csms-1", "GetVariables", SomeVariable)
                                : new JArray(2, "csms-1", "GetVariables", SomeVariable));

            // In the frame of the overlay network, which lets a station write whatever it likes.
            await cs001.Send(new JArray(2, ControllerId, "cs001", "station-1", "GetVariables", SomeVariable));

            var toTheCSMS     = await csms. AnswerTo("csms-1");
            var toTheStation  = await cs001.AnswerTo("station-1");

            Assert.Multiple(() => {

                Assert.That(toTheCSMS?[0].Value<Int32>(),     Is.EqualTo(3), $"the CSMS that dialled was not answered: {toTheCSMS?.ToString(Formatting.None)}{Environment.NewLine}{Log}");
                Assert.That(PayloadOf(toTheCSMS!)?["getVariableResult"], Is.Not.Null, toTheCSMS?.ToString(Formatting.None));

                if (Overlay)
                    Assert.That(SourceOf(toTheCSMS!),         Is.EqualTo(ControllerId));

                Assert.That(toTheStation?[0].Value<Int32>(),  Is.EqualTo(4), $"a charging station was answered what only the CSMS asks: {toTheStation?.ToString(Formatting.None)}");
                Assert.That(toTheStation?.ToString(Formatting.None), Does.Contain("SecurityError"));

            });

        }

        #endregion

        #region WhatAStationSends_GoesUpToIt_AndWhatItAsksOfAStationGoesDown()

        /// <summary>
        /// The stations' requests go up to the CSMS that dialled, and its answers come back to them; what
        /// it asks of a station behind the controller reaches that station, and the answer the CSMS.
        /// </summary>
        [Test]
        public async Task WhatAStationSends_GoesUpToIt_AndWhatItAsksOfAStationGoesDown()
        {

            var (csms, _) = await TheCSMSIsIn();

            await using var theCSMS  = csms;
            await using var cs001    = await AStation("cs001");
            await using var cs002    = await AStation("cs002");

            #region Up

            await cs001.Send(new JArray(2, "boot-1", "BootNotification", new JObject(
                                            new JProperty("chargingStation", new JObject(
                                                new JProperty("model",       "Bay"),
                                                new JProperty("vendorName",  "GraphDefined")
                                            )),
                                            new JProperty("reason", "PowerUp")
                                        )));

            var answer  = await cs001.AnswerTo("boot-1");
            var up      = csms.Got.FirstOrDefault(frame => frame[0].Value<Int32>() == 2 &&
                                                           ActionOf(frame) == "BootNotification" &&
                                                           PayloadOf(frame)?["chargingStation"]?["model"]?.Value<String>() == "Bay");

            Assert.Multiple(() => {

                Assert.That(up,                                       Is.Not.Null, $"the station's BootNotification did not reach the CSMS that dialled.{Environment.NewLine}{Log}");
                Assert.That(answer?[0].Value<Int32>(),                Is.EqualTo(3), $"the station got no answer: {answer?.ToString(Formatting.None)}");
                Assert.That(answer?[2]?["status"]?.Value<String>(),   Is.EqualTo("Accepted"));

                if (up is not null)
                {

                    Assert.That(IsOverlay(up),                        Is.EqualTo(Overlay), up.ToString(Formatting.None));

                    if (Overlay)
                    {
                        Assert.That(SourceOf(up),                     Is.EqualTo("cs001"),      up.ToString(Formatting.None));
                        Assert.That(PathOf(up).Last(),                Is.EqualTo(ControllerId), up.ToString(Formatting.None));
                    }

                }

            });

            #endregion

            #region Down

            // To a station behind the controller: in the frame of the overlay network, which the
            // controller reads in either mode.
            await csms.Send(new JArray(2, "cs002", "CSMS", "csms-7", "GetLocalListVersion", new JObject()));

            var down      = await cs002.Asked("GetLocalListVersion");
            var itsAnswer = await csms. AnswerTo("csms-7");

            Assert.Multiple(() => {
                Assert.That(down,                                          Is.Not.Null, $"the request of the CSMS did not reach the station.{Environment.NewLine}{Log}");
                Assert.That(cs001.Got.Any(frame => frame[0].Value<Int32>() == 2 && ActionOf(frame) == "GetLocalListVersion"),
                                                                           Is.False, "it reached another station as well");
                Assert.That(itsAnswer?[0].Value<Int32>(),                  Is.EqualTo(3), $"the CSMS got no answer: {itsAnswer?.ToString(Formatting.None)}");
                Assert.That(PayloadOf(itsAnswer!)?["answeredBy"]?.Value<String>(), Is.EqualTo("cs002"));
            });

            #endregion

            #region And a station still sends another station nothing

            await cs001.Send(new JArray(2, "cs002", "cs001", "station-9", "Reset", new JObject(new JProperty("type", "Immediate"))));

            var refused = await cs001.AnswerTo("station-9");

            Assert.Multiple(() => {
                Assert.That(refused?[0].Value<Int32>(),   Is.EqualTo(4), $"a station's request for another station was not refused: {refused?.ToString(Formatting.None)}");
                Assert.That(cs002.Got.Any(frame => frame[0].Value<Int32>() == 2 && ActionOf(frame) == "Reset"), Is.False, "a station reset another station");
            });

            #endregion

        }

        #endregion

        #region ItsName_IsNoStations_AndAStationIsNoCSMS()

        /// <summary>
        /// One port, and names that are told apart at its door: a CSMS cannot be written down under the
        /// name of a charging station, a station's password opens nothing under the name of the CSMS, and
        /// the stations sign in beside it as before.
        /// </summary>
        [Test]
        public async Task ItsName_IsNoStations_AndAStationIsNoCSMS()
        {

            Assert.That(Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out var error), Is.True, error);

            Assert.That(Controller.Connections.TryAddAuthentication("What it signs in with", "basic", "cs001", ItsPassword,
                                                                    out var login, out error),
                        Is.True, error);

            Assert.Multiple(() => {

                Assert.That(Controller.Connections.TryAddConnection("A CSMS under the name of a station", Door("cs001"), "CSMS", true, login, null,
                                                                    out _, out var refused,
                                                                    Inbound: true),
                            Is.False);

                Assert.That(refused, Does.Contain("is a charging station of this local controller"));

            });

            var (csms, _) = await TheCSMSIsIn();

            await using var theCSMS       = csms;
            await using var asTheCSMS     = new Line("cs001");
            await using var cs001         = new Line("cs001");

            Assert.Multiple(async () => {
                Assert.That(await asTheCSMS.Dial(Door(CSMSName), "cs001", ThePassword), Is.Not.EqualTo(HttpStatusCode.SwitchingProtocols), "a station's password opened the name of the CSMS");
                Assert.That(await cs001.    Dial(Door("cs001"),  "cs001", ThePassword), Is.EqualTo(HttpStatusCode.SwitchingProtocols),     "a station is not let in beside the CSMS");
            });

            await Until(() => Controller.StateJSON()["stations"]?["connected"]?.Value<Int32>() == 1, "The station being connected");

            Assert.That(csms.IsOpen, Is.True, "the CSMS was let go of");

        }

        #endregion

        #region SwitchedOff_ItIsLetGoOf_AndLetInAgainOnceSwitchedOn()

        /// <summary>
        /// "Connect by itself" is what lets it in: switched off, the CSMS that is in is let go of and
        /// turned away at the door - and let in again once it is switched on.
        /// </summary>
        [Test]
        public async Task SwitchedOff_ItIsLetGoOf_AndLetInAgainOnceSwitchedOn()
        {

            var (csms, id) = await TheCSMSIsIn();

            await using var theCSMS = csms;

            LetIn(id, false);

            await Until(() => StatusOf(id) == "waiting" && !csms.IsOpen, "The CSMS being let go of");

            await using var turnedAway = new Line("CSMS");

            Assert.Multiple(async () => {
                Assert.That(SaidOf(id),                                                               Does.Contain("is not let in"));
                Assert.That(Controller.CSMSConnected,                                                 Is.False);
                Assert.That(await turnedAway.Dial(Door(CSMSName), CSMSName, ItsPassword, Overlay),    Is.EqualTo(HttpStatusCode.Unauthorized));
            });

            LetIn(id, true);

            await using var again = new Line("CSMS");

            Assert.That(await again.Dial(Door(CSMSName), CSMSName, ItsPassword, Overlay), Is.EqualTo(HttpStatusCode.SwitchingProtocols), Log);

            await Until(() => StatusOf(id) == "connected", "The CSMS being connected again");

        }

        #endregion

    }

}
