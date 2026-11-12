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
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The line up to the CSMS in either networking mode ("csms.networkingMode"): standard, the plain
    /// OCPP frame, as every controller dialled before there was a choice - or overlay, the frame of the
    /// overlay network, which names where a message goes and where it comes from, and without which a
    /// CSMS cannot reach the stations behind the controller (white paper "Key Pins and TLS 1.3 Raw Public
    /// Keys for Local Controllers", work package K4, phase B3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each test runs in both modes: the controller says which in the header X-OCPP-NetworkingMode of its
    /// dial, answers the requests addressed to itself - EVQICtrlr[TLSServer], read and switched as in
    /// KeyPinsOverOCPPTests - forwards those for a station behind it by their destination, and sends a
    /// station's requests up, in either mode.
    /// </para>
    /// <para>
    /// The CSMS is of the least kind: a Hermod WebSocket server that answers what the controller sends in
    /// the frame it came in, and sends the frames a test writes as they go over the wire - addressed to the
    /// controller in the frame of the line's mode, and to a station behind it in the frame of the overlay
    /// network, which the controller reads in either mode.
    /// </para>
    /// </remarks>
    [TestFixture(false)]
    [TestFixture(true)]
    public class CSMSNetworkingModeTests(Boolean Overlay)
    {

        #region (class) CSMS

        /// <summary>
        /// A CSMS of the least kind, in the networking mode of the controller's line.
        /// </summary>
        internal sealed class CSMS : IAsyncDisposable
        {

            private readonly ConcurrentQueue<JArray>                                    received  = new();
            private readonly ConcurrentDictionary<String, TaskCompletionSource<JArray>>  waiting   = new();
            private readonly Boolean                                                    overlay;
            private          Int32                                                      lastId;

            public WebSocketServer  Server  { get; }

            public CSMS(UInt16   Port,
                        Boolean  Overlay)
            {

                overlay = Overlay;

                Server  = new WebSocketServer(
                              HTTPPort:               IPPort.Parse(Port),
                              RequireAuthentication:  false,
                              SecWebSocketProtocols:  [ "ocpp2.1" ],
                              DisableWebSocketPings:  true,
                              AutoStart:              true
                          );

                Server.OnTextMessageReceived += async (timestamp, server, connection, frame, eventTrackingId, text, cancellationToken) => {

                    var message = KeyPinsOverOCPPTests.Frame(text);

                    received.Enqueue(message);

                    switch (message[0].Value<Int32>())
                    {

                        // A request of the controller's, or of a station behind it:
                        // answered in the frame it came in, to whom it came from.
                        case 2:
                            await Server.SendTextMessage(connection,
                                                         (IsOverlay(message)
                                                              ? new JArray(3, SourceOf(message), "CSMS", IdOf(message), AnswerTo(ActionOf(message)))
                                                              : new JArray(3, IdOf(message), AnswerTo(ActionOf(message)))).ToString(Formatting.None));
                            break;

                        // An answer to one of this CSMS's own.
                        case 3:
                        case 4:
                            if (waiting.TryRemove(IdOf(message), out var answer))
                                answer.TrySetResult(message);
                            break;

                    }

                };

            }

            private static JObject AnswerTo(String Action)

                => Action switch {

                       "BootNotification"  => new JObject(
                                                  new JProperty("status",       "Accepted"),
                                                  new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o")),
                                                  new JProperty("interval",     300)
                                              ),

                       "Heartbeat"         => new JObject(
                                                  new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o"))
                                              ),

                       _                   => new JObject()

                   };

            /// <summary>
            /// The connections it has.
            /// </summary>
            public Int32 Connections
                => Server.WebSocketConnections.Count();

            /// <summary>
            /// The networking mode the controller said in the header of its dial, or null where it said none.
            /// </summary>
            public String? NetworkingModeSaid
                => Server.WebSocketConnections.Single().HTTPRequest?.TryGetHeaderField("X-OCPP-NetworkingMode", out var value) == true
                       ? value?.ToString()
                       : null;

            /// <summary>
            /// Send the controller a request and wait for its answer: addressed to the controller itself
            /// where no destination is given, in the frame of the line's mode - or to the given destination
            /// behind it, in the frame of the overlay network.
            /// </summary>
            public async Task<JArray> Ask(String   Action,
                                          JObject  Payload,
                                          String?  Destination   = null)
            {

                var id      = $"csms-{Interlocked.Increment(ref lastId)}";
                var answer  = new TaskCompletionSource<JArray>(TaskCreationOptions.RunContinuationsAsynchronously);

                waiting[id] = answer;

                var frame   = Destination is null && !overlay
                                  ? new JArray(2, id, Action, Payload)
                                  : new JArray(2, Destination ?? ControllerId, "CSMS", id, Action, Payload);

                await Server.SendTextMessage(Server.WebSocketConnections.Single(), frame.ToString(Formatting.None));

                if (await Task.WhenAny(answer.Task, Task.Delay(Guard)) != answer.Task)
                    Assert.Fail($"The controller did not answer {Action} {id}.");

                return await answer.Task;

            }

            /// <summary>
            /// The requests of the given action that reached this CSMS so far.
            /// </summary>
            public JArray[] Requests(String Action)
                => [.. received.Where(message => message[0].Value<Int32>() == 2 && ActionOf(message) == Action)];

            /// <summary>
            /// The first request of the given action whose payload is what is looked for, once there is
            /// one or the given time has passed.
            /// </summary>
            public async Task<JArray?> SentRequest(String                  Action,
                                                   Func<JObject, Boolean>  Wanted,
                                                   TimeSpan                Patience)
            {

                var giveUp = DateTimeOffset.UtcNow + Patience;

                do
                {

                    if (Requests(Action).FirstOrDefault(message => PayloadOf(message) is JObject payload && Wanted(payload)) is JArray found)
                        return found;

                    await Task.Delay(20);

                }
                while (DateTimeOffset.UtcNow < giveUp);

                return null;

            }

            public ValueTask DisposeAsync()
                => Server.DisposeAsync();

        }

        #endregion

        #region (static) Frames: IsOverlay / IdOf / ActionOf / PayloadOf / SourceOf / DestinationOf

        /// <summary>
        /// Whether the frame is one of the overlay network: a request of six elements, a result of five, an
        /// error of seven.
        /// </summary>
        internal static Boolean IsOverlay(JArray Frame)

            => Frame[0].Value<Int32>() switch {
                   2 => Frame.Count == 6,
                   3 => Frame.Count == 5,
                   4 => Frame.Count == 7,
                   _ => false
               };

        internal static String IdOf(JArray Frame)
            => (IsOverlay(Frame) ? Frame[3] : Frame[1]).Value<String>() ?? "";

        internal static String ActionOf(JArray Frame)
            => (IsOverlay(Frame) ? Frame[4] : Frame[2]).Value<String>() ?? "";

        internal static JObject? PayloadOf(JArray Frame)
            => Frame[0].Value<Int32>() == 2
                   ? (IsOverlay(Frame) ? Frame[5] : Frame[3]) as JObject
                   : (IsOverlay(Frame) ? Frame[4] : Frame[2]) as JObject;

        /// <summary>
        /// The network path of an overlay frame - one hop or several - as a list, where the message came
        /// from first.
        /// </summary>
        internal static String[] PathOf(JArray Frame)

            => Frame[2] is JArray hops
                   ? [.. hops.Select(hop => hop.Value<String>() ?? "")]
                   : [ Frame[2].Value<String>() ?? "" ];

        internal static String SourceOf(JArray Frame)
            => PathOf(Frame)[0];

        internal static String DestinationOf(JArray Frame)
            => Frame[1] is JArray hops ? hops.Last().Value<String>() ?? "" : Frame[1].Value<String>() ?? "";

        #endregion

        #region Data

        private const           String    ControllerId     = "lc002";

        private static readonly TimeSpan  Guard            = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan  EventPatience    = TimeSpan.FromSeconds(10);

        private const String ThePassword = "a-password-long-enough-for-ocpp";

        private String           directory   = default!;
        private CSMS             csms        = default!;
        private LocalController  controller  = default!;
        private UInt16           port;

        private KeyPin           currentPin  = default!;
        private KeyPin           nextPin     = default!;

        private ECDsa            cs001Key    = default!;
        private ECDsa            cs002Key    = default!;

        private readonly ConcurrentQueue<String>            said          = [];
        private readonly ConcurrentQueue<(String, JArray)>  stationsGot   = [];
        private readonly List<IDisposable>                  made          = [];
        private readonly List<WebSocketClient>              clients       = [];

        /// <summary>
        /// How a station answers a request of the controller, where a test says so: the frame it sends back.
        /// </summary>
        private readonly ConcurrentDictionary<String, Func<JArray, String>>  answering  = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task StartAControllerWithTwoKeysAndACSMS()
        {

            directory = TestControllers.TemporaryDirectory("networking-mode");
            Directory.CreateDirectory(directory);

            said.       Clear();
            stationsGot.Clear();
            answering.  Clear();

            cs001Key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            cs002Key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            using (var store = new ServerCertificateStore(Path.Combine(directory, ServerCertificateStore.DefaultDirectoryName)))
            {

                Assert.That(store.TryCreateServerKey(null, AsNext: false, out _, out var current, out var error),     Is.True, error);
                Assert.That(store.TryCreateServerKey(null, AsNext: true,  out _, out var next,    out var nextError), Is.True, nextError);

                currentPin  = current!;
                nextPin     = next!;

            }

            var csmsPort  = TestPorts.Free();

            csms          = new CSMS(csmsPort, Overlay);

            // Overlay said in the file; standard said there too, by name, as a
            // file written by the page would say it.
            controller    = await TestPorts.StartedOnFreshPorts(() => {

                                port      = TestPorts.Free();

                                var lc    = TestControllers.New(
                                                directory,
                                                new JObject(

                                                    new JProperty("nts",  new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                                    new JProperty("ocpp", new JObject(new JProperty("nodeId", ControllerId))),

                                                    new JProperty("ocppServer", new JObject(
                                                        new JProperty("enabled",           true),
                                                        new JProperty("address",           "127.0.0.1"),
                                                        new JProperty("port",              port),
                                                        new JProperty("securityProfiles",  new JArray(2, 3))
                                                    )),

                                                    new JProperty("csms", new JObject(
                                                        new JProperty("enabled",                true),
                                                        new JProperty("url",                    $"ws://127.0.0.1:{csmsPort}"),
                                                        new JProperty("securityProfile",        1),
                                                        new JProperty("networkingMode",         Overlay ? "overlay" : "standard"),
                                                        new JProperty("reconnectInitialDelay",  1),
                                                        new JProperty("reconnectMaxDelay",      2)
                                                    ))

                                                )
                                            );

                                lc.Log.OnLogged += entry => said.Enqueue(entry.Message);

                                Assert.That(lc.CSMSLogin.TrySetPassword(ControllerId, ThePassword, out var loginError), Is.True, loginError);

                                Assert.That(lc.StationLogins.TryAddKeyPin("cs001", KeyPin.FromKey(cs001Key).ToHex(), "bay 1", null, null, null, out _, out var error1), Is.True, error1);
                                Assert.That(lc.StationLogins.TryAddKeyPin("cs002", KeyPin.FromKey(cs002Key).ToHex(), "bay 2", null, null, null, out _, out var error2), Is.True, error2);

                                return lc;

                            });

            var giveUp = DateTimeOffset.UtcNow + Guard;

            while (DateTimeOffset.UtcNow < giveUp && !(controller.CSMSConnected && csms.Connections == 1))
                await Task.Delay(50);

            Assert.Multiple(() => {
                Assert.That(controller.CSMSConnected,  Is.True, Log);
                Assert.That(csms.Connections,          Is.EqualTo(1));
            });

        }

        [TearDown]
        public async Task StopIt()
        {

            foreach (var client in clients)
            {
                try
                {
                    await client.DisposeAsync();
                }
                catch
                { }
            }

            clients.Clear();

            if (controller is not null)
                await controller.DisposeAsync();

            if (csms is not null)
                await csms.DisposeAsync();

            foreach (var disposable in made)
                disposable.Dispose();

            made.Clear();

            cs001Key?.Dispose();
            cs002Key?.Dispose();

            TestControllers.Remove(directory);

        }

        #endregion


        #region (private) Connect(Station, Key, params Pins)

        /// <summary>
        /// A charging station connecting to its path over wss://, checking the controller by the given key
        /// pins alone. It answers what the controller forwards to it as a station without EVQICtrlr does.
        /// </summary>
        private async Task<(WebSocketClient Client, HTTPStatusCode? Status)> Connect(String           Station,
                                                                                    ECDsa            Key,
                                                                                    params KeyPin[]  Pins)
        {

            var request      = new CertificateRequest("CN=station.invalid", Key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.2") ], false));

            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            // SChannel wants a key it can find again: out to PKCS#12 and back.
            var certificate  = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet);

            made.Add(certificate);

            var client       = new WebSocketClient(
                                   URL.Parse($"wss://127.0.0.1:{port}/{Station}"),
                                   SecWebSocketProtocols:       [ "ocpp2.1" ],
                                   RemoteCertificateValidator:  KeyPinValidator.ForServerCertificates<IWebSocketClient>(KeyPinPolicy.PinnedTo(Pins)),
                                   LocalCertificateSelector:    (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) => certificate,
                                   ClientCertificates:          [ certificate ],
                                   DisableWebSocketPings:       true,
                                   ConnectTimeout:              TimeSpan.FromSeconds(10)
                               );

            clients.Add(client);

            client.OnTextMessageReceived += async (timestamp, sender, connection, frame, eventTrackingId, text, cancellationToken) => {

                var message = KeyPinsOverOCPPTests.Frame(text);

                stationsGot.Enqueue((Station, message));

                if (message.Count == 4 && message[0].Value<Int32>() == 2 && message[3] is JObject payload)
                    await sender.SendTextMessage(answering.TryGetValue(Station, out var answer)
                                                     ? answer(message)
                                                     : new JArray(3, message[1], StationAnswerTo(message[2].Value<String>() ?? "", payload)).ToString(Formatting.None));

            };

            var connect = client.Connect();

            if (await Task.WhenAny(connect, Task.Delay(Guard)) != connect)
                Assert.Fail("Connect() did not return.");

            var (_, response) = await connect;

            return (client, response?.HTTPStatusCode);

        }

        /// <summary>
        /// What a station that has no EVQICtrlr answers: SetVariables of it is an unknown component.
        /// </summary>
        private static JObject StationAnswerTo(String   Action,
                                               JObject  Payload)

            => Action == "SetVariables"
                   ? new JObject(
                         new JProperty("setVariableResult", new JArray(
                             (Payload["setVariableData"] as JArray ?? []).OfType<JObject>().Select(data => new JObject(
                                 new JProperty("attributeStatus",  "UnknownComponent"),
                                 new JProperty("component",        data["component"]),
                                 new JProperty("variable",         data["variable"])
                             ))
                         ))
                     )
                   : new JObject();

        #endregion

        #region (private) Helpers

        private static JObject EVQICtrlrTLSServer
            => new (new JProperty("name", "EVQICtrlr"), new JProperty("instance", "TLSServer"));

        private static JObject ActiveKeyPinPayload(String Value)

            => new (
                   new JProperty("setVariableData", new JArray(
                       new JObject(
                           new JProperty("attributeValue",  Value),
                           new JProperty("component",       EVQICtrlrTLSServer),
                           new JProperty("variable",        new JObject(new JProperty("name", "ActiveKeyPin")))
                       )
                   ))
               );

        /// <summary>
        /// The result of the only entry of a SetVariables answer: status and additional info.
        /// </summary>
        private static (String? Status, String? Info) SetResult(JArray Answer)
        {

            var result = (PayloadOf(Answer)?["setVariableResult"] as JArray)?.OfType<JObject>().Single();

            return (result?["attributeStatus"]?.Value<String>(),
                    result?["attributeStatusInfo"]?["additionalInfo"]?.Value<String>());

        }

        /// <summary>
        /// The answer a station got to its own request of the given id, once there is one or the guard has
        /// passed.
        /// </summary>
        private async Task<JArray?> StationsAnswerTo(String Station, String RequestId)
        {

            var giveUp = DateTimeOffset.UtcNow + Guard;

            do
            {

                var answer = stationsGot.FirstOrDefault(got => got.Item1 == Station &&
                                                               got.Item2[0].Value<Int32>() is 3 or 4 &&
                                                               got.Item2[1].Value<String>() == RequestId).Item2;

                if (answer is not null)
                    return answer;

                await Task.Delay(20);

            }
            while (DateTimeOffset.UtcNow < giveUp);

            return null;

        }

        /// <summary>
        /// What the controller said, for the message of an assertion.
        /// </summary>
        private String Log
            => String.Join(Environment.NewLine, said);

        private String Mode
            => Overlay ? "overlay" : "standard";

        #endregion


        #region TheDial_SaysTheModeInItsHeader_OnlyForTheOverlayNetwork()

        /// <summary>
        /// A controller in the overlay network says so in the header X-OCPP-NetworkingMode of its dial,
        /// which is how the CSMS knows to address the stations behind it; a standard one says nothing, as
        /// every controller before the choice did.
        /// </summary>
        [Test]
        public void TheDial_SaysTheModeInItsHeader_OnlyForTheOverlayNetwork()
        {

            Assert.Multiple(() => {
                Assert.That(csms.NetworkingModeSaid,                       Overlay ? Is.EqualTo("OverlayNetwork") : Is.Null, $"in {Mode} mode");
                Assert.That(controller.CSMSSettings.InOverlayNetwork,      Is.EqualTo(Overlay));
                Assert.That(controller.CSMSConfigurationJSON()["networkingMode"]?.Value<String>(),
                                                                           Is.EqualTo(Mode));
            });

        }

        #endregion

        #region TheControllersOwnRequests_GoUpInTheFrameOfTheMode()

        /// <summary>
        /// What the controller sends of its own - the security event of a key switched on its command line
        /// - goes up in the frame of the line's mode: in the overlay network naming the CSMS as where it
        /// goes and the controller as where it comes from, in standard mode naming neither.
        /// </summary>
        [Test]
        public async Task TheControllersOwnRequests_GoUpInTheFrameOfTheMode()
        {

            Assert.That(controller.ServerCertificates.TrySwitchToNextKey(out _, out var error), Is.True, error);

            var told = await csms.SentRequest("SecurityEventNotification",
                                              payload => payload["type"]?.Value<String>() == "ReconfigurationOfSecurityParameters",
                                              EventPatience);

            Assert.That(told, Is.Not.Null, "the controller sent no security event of its own");

            Assert.Multiple(() => {

                Assert.That(IsOverlay(told!),  Is.EqualTo(Overlay), told!.ToString(Formatting.None));

                if (Overlay)
                {
                    Assert.That(DestinationOf(told!),  Is.EqualTo("CSMS"));
                    Assert.That(PathOf(told!),         Is.EqualTo(new[] { ControllerId }), "the controller's own request names another source");
                }

            });

        }

        #endregion

        #region ARequestForTheController_IsAnsweredByIt()

        /// <summary>
        /// GetVariables of EVQICtrlr[TLSServer] addressed to the controller - in the overlay network by its
        /// identity - is the controller's: answered by it, with both key pins, in the frame of the mode.
        /// </summary>
        [Test]
        public async Task ARequestForTheController_IsAnsweredByIt()
        {

            var answer = await csms.Ask(
                                   "GetVariables",
                                   new JObject(
                                       new JProperty("getVariableData", new JArray(
                                           new[] { "KeyPins", "NextKeyPin", "ActiveKeyPin" }.Select(variable => new JObject(
                                               new JProperty("component", EVQICtrlrTLSServer),
                                               new JProperty("variable",  new JObject(new JProperty("name", variable)))
                                           ))
                                       ))
                                   )
                               );

            var results = (PayloadOf(answer)?["getVariableResult"] as JArray ?? []).
                              OfType<JObject>().
                              ToDictionary(result => result["variable"]?["name"]?.Value<String>() ?? "",
                                           result => (Status: result["attributeStatus"]?.Value<String>(),
                                                      Value:  result["attributeValue"]?. Value<String>()));

            Assert.Multiple(() => {

                Assert.That(answer[0].Value<Int32>(),          Is.EqualTo(3), answer.ToString(Formatting.None));
                Assert.That(IsOverlay(answer),                 Is.EqualTo(Overlay), answer.ToString(Formatting.None));

                if (Overlay)
                {
                    Assert.That(DestinationOf(answer),         Is.EqualTo("CSMS"));
                    Assert.That(SourceOf(answer),              Is.EqualTo(ControllerId));
                }

                Assert.That(results.Values.Select(result => result.Status), Is.All.EqualTo("Accepted"), answer.ToString(Formatting.None));
                Assert.That(results.GetValueOrDefault("KeyPins").     Value, Is.EqualTo($"{currentPin.ToHex()},{nextPin.ToHex()}"));
                Assert.That(results.GetValueOrDefault("NextKeyPin").  Value, Is.EqualTo(nextPin.ToHex()));
                Assert.That(results.GetValueOrDefault("ActiveKeyPin").Value, Is.EqualTo(currentPin.ToHex()));

            });

        }

        #endregion

        #region TheSwitch_OverTheLine_IsTakenAndReported()

        /// <summary>
        /// The CSMS writes the next key's pin into the controller's ActiveKeyPin: accepted, switched without
        /// dropping a station connected before, and reported by the security event with both pins - in the
        /// frame of the mode, from the controller.
        /// </summary>
        [Test]
        public async Task TheSwitch_OverTheLine_IsTakenAndReported()
        {

            var (cs001, status) = await Connect("cs001", cs001Key, currentPin, nextPin);

            Assert.That(status, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);

            var answer  = await csms.Ask("SetVariables", ActiveKeyPinPayload(nextPin.ToHex()));
            var result  = SetResult(answer);

            var told    = await csms.SentRequest("SecurityEventNotification",
                                                 payload => payload["type"]?.Value<String>() == "ReconfigurationOfSecurityParameters",
                                                 EventPatience);

            Assert.Multiple(() => {

                Assert.That(result.Status,                                       Is.EqualTo("Accepted"), result.Info);
                Assert.That(controller.ServerCertificates.CurrentKey?.KeyPin,    Is.EqualTo(nextPin), "the store was not switched");
                Assert.That(cs001.Connected,                                     Is.True, "the station connected before the switch was dropped");

                Assert.That(told,                                                Is.Not.Null, "the CSMS was not told of the switch");
                Assert.That(told is null ? "" : PayloadOf(told)?["techInfo"]?.Value<String>(),
                                                                                 Is.EqualTo($"TLS server key switched: key pin {currentPin.ToHex()} -> {nextPin.ToHex()}"));
                Assert.That(told is not null && IsOverlay(told),                 Is.EqualTo(Overlay));

                if (Overlay && told is not null)
                    Assert.That(SourceOf(told),                                  Is.EqualTo(ControllerId));

            });

        }

        #endregion

        #region ARequestForAStation_IsForwardedByItsDestination()

        /// <summary>
        /// A request addressed to a station behind the controller is the station's: forwarded to it, answered
        /// by it, and the answer comes back up - in the overlay network naming the station as where it comes
        /// from. The controller's own keys stay as they were.
        /// </summary>
        [Test]
        public async Task ARequestForAStation_IsForwardedByItsDestination()
        {

            var (_, status) = await Connect("cs001", cs001Key, currentPin, nextPin);

            Assert.That(status, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);

            var answer     = await csms.Ask("SetVariables", ActiveKeyPinPayload(nextPin.ToHex()), Destination: "cs001");

            var atStation  = stationsGot.Where(got => got.Item1 == "cs001" &&
                                                      got.Item2[0].Value<Int32>() == 2 &&
                                                      got.Item2[2].Value<String>() == "SetVariables").ToArray();

            Assert.Multiple(() => {

                Assert.That(atStation,                                           Has.Length.EqualTo(1), "the station did not get the request");
                Assert.That(SetResult(answer).Status,                            Is.EqualTo("UnknownComponent"), $"the answer is not the station's: {answer.ToString(Formatting.None)}");

                if (Overlay)
                {
                    Assert.That(IsOverlay(answer),                               Is.True, answer.ToString(Formatting.None));
                    Assert.That(SourceOf(answer),                                Is.EqualTo("cs001"), "the answer does not name the station as where it comes from");
                }

                Assert.That(controller.ServerCertificates.CurrentKey?.KeyPin,    Is.EqualTo(currentPin), "the controller's key was switched by a request for a station");
                Assert.That(controller.ServerCertificates.NextKey?.KeyPin,       Is.EqualTo(nextPin));

            });

        }

        #endregion

        #region AStationsRequest_GoesUp_AndItsAnswerComesBack()

        /// <summary>
        /// A station's own request - its BootNotification - goes up to the CSMS and the CSMS's answer comes
        /// back to it. In the overlay network the request names the station as where it comes from and the
        /// controller as the last hop, which is how the CSMS learns which stations are behind which
        /// controller; in standard mode it names nobody.
        /// </summary>
        [Test]
        public async Task AStationsRequest_GoesUp_AndItsAnswerComesBack()
        {

            var (cs001, status) = await Connect("cs001", cs001Key, currentPin, nextPin);

            Assert.That(status, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);

            await cs001.SendTextMessage(new JArray(2, "boot-1", "BootNotification", new JObject(
                                                       new JProperty("chargingStation", new JObject(
                                                           new JProperty("model",       "Bay"),
                                                           new JProperty("vendorName",  "GraphDefined")
                                                       )),
                                                       new JProperty("reason", "PowerUp")
                                                   )).ToString(Formatting.None));

            var up      = await csms.SentRequest("BootNotification",
                                                 payload => payload["chargingStation"]?["model"]?.Value<String>() == "Bay",
                                                 EventPatience);

            var answer  = await StationsAnswerTo("cs001", "boot-1");

            Assert.Multiple(() => {

                Assert.That(up,                              Is.Not.Null, "the station's BootNotification did not reach the CSMS");
                Assert.That(answer?[0].Value<Int32>(),       Is.EqualTo(3), $"the station got no answer: {answer?.ToString(Formatting.None)}");
                Assert.That(answer?[2]?["status"]?.Value<String>(),
                                                             Is.EqualTo("Accepted"));

                if (up is not null)
                {

                    Assert.That(IsOverlay(up),               Is.EqualTo(Overlay), up.ToString(Formatting.None));

                    if (Overlay)
                    {
                        Assert.That(SourceOf(up),            Is.EqualTo("cs001"),      $"the request does not name the station as where it comes from: {up.ToString(Formatting.None)}");
                        Assert.That(PathOf(up).Last(),       Is.EqualTo(ControllerId), $"the request does not name the controller as its last hop: {up.ToString(Formatting.None)}");
                        Assert.That(DestinationOf(up),       Is.EqualTo("CSMS"));
                    }

                }

            });

        }

        #endregion

        #region AStationsSecurityEvent_GoesUp_NamingTheStation()

        /// <summary>
        /// A station's security event - the KeyPinLearned of a station that learned the controller's key pin
        /// on first use, which the CSMS answers with a signed pin set - goes up to the CSMS: in the overlay
        /// network naming the station as where it comes from, which is what the CSMS sends the pin set to.
        /// </summary>
        [Test]
        public async Task AStationsSecurityEvent_GoesUp_NamingTheStation()
        {

            var (cs001, status) = await Connect("cs001", cs001Key, currentPin, nextPin);

            Assert.That(status, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);

            await cs001.SendTextMessage(new JArray(2, "event-1", "SecurityEventNotification", new JObject(
                                                       new JProperty("type",       "KeyPinLearned"),
                                                       new JProperty("timestamp",  DateTimeOffset.UtcNow.ToString("o")),
                                                       new JProperty("techInfo",   $"Key pin {currentPin.ToHex()} of endpoint 'lc' learned on first use, on probation")
                                                   )).ToString(Formatting.None));

            var up      = await csms.SentRequest("SecurityEventNotification",
                                                 payload => payload["type"]?.Value<String>() == "KeyPinLearned",
                                                 EventPatience);

            var answer  = await StationsAnswerTo("cs001", "event-1");

            Assert.Multiple(() => {

                Assert.That(up,                              Is.Not.Null, "the station's security event did not reach the CSMS");
                Assert.That(answer?[0].Value<Int32>(),       Is.EqualTo(3), $"the station got no answer: {answer?.ToString(Formatting.None)}");

                if (up is not null && Overlay)
                {
                    Assert.That(IsOverlay(up),               Is.True,                  up.ToString(Formatting.None));
                    Assert.That(SourceOf(up),                Is.EqualTo("cs001"),      $"the event does not name the station as where it comes from: {up.ToString(Formatting.None)}");
                    Assert.That(PathOf(up).Last(),           Is.EqualTo(ControllerId), $"the event does not name the controller as its last hop: {up.ToString(Formatting.None)}");
                }

            });

        }

        #endregion

        #region AStation_CannotSendAnotherStation_WhatTheCSMSSends()

        /// <summary>
        /// A station addresses another station behind the same controller, in the frame of the overlay
        /// network, which it can write whatever the controller's own line is: a Reset, as itself and calling
        /// itself the CSMS. The other station gets neither. The first is refused to the station that sent it;
        /// the second is dropped, as its answer would go to the CSMS it names.
        /// </summary>
        [Test]
        public async Task AStation_CannotSendAnotherStation_WhatTheCSMSSends()
        {

            var (cs001, status1) = await Connect("cs001", cs001Key, currentPin, nextPin);
            var (_,     status2) = await Connect("cs002", cs002Key, currentPin, nextPin);

            Assert.That(status1, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);
            Assert.That(status2, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);

            var reset = new JObject(new JProperty("type", "Immediate"));

            await cs001.SendTextMessage(new JArray(2, "cs002", "CSMS",  "lateral-1", "Reset", reset).ToString(Formatting.None));
            await cs001.SendTextMessage(new JArray(2, "cs002", "cs001", "lateral-2", "Reset", reset).ToString(Formatting.None));

            var refused = await StationsAnswerTo("cs001", "lateral-2");

            // Whatever was sent on would have arrived by now.
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            var atCS002 = stationsGot.Where(got => got.Item1 == "cs002" && got.Item2[0].Value<Int32>() == 2 && got.Item2.Any(item => item.Type == JTokenType.String && item.Value<String>() == "Reset")).ToArray();

            Assert.Multiple(() => {
                Assert.That(atCS002,                       Is.Empty,      $"a station sent another one a Reset through the controller: {String.Join(" ", atCS002.Select(got => got.Item2.ToString(Formatting.None)))}");
                Assert.That(refused?[0].Value<Int32>(),    Is.EqualTo(4), $"the station was not told: {refused?.ToString(Formatting.None)}");
                Assert.That(csms.Requests("Reset"),        Is.Empty,      "a station's Reset went up to the CSMS");
            });

        }

        #endregion

        #region AStationsAnswer_GoesToWhoAsked_WhateverItNames()

        /// <summary>
        /// A station answers a request of the CSMS in the frame of the overlay network, naming another
        /// station as where the answer goes. The answer goes to whoever asked - the CSMS - and to no other
        /// station.
        /// </summary>
        [Test]
        public async Task AStationsAnswer_GoesToWhoAsked_WhateverItNames()
        {

            answering["cs001"] = request => new JArray(3, "cs002", "cs001", request[1], new JObject(new JProperty("status", "Accepted"))).ToString(Formatting.None);

            var (_, status1) = await Connect("cs001", cs001Key, currentPin, nextPin);
            var (_, status2) = await Connect("cs002", cs002Key, currentPin, nextPin);

            Assert.That(status1, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);
            Assert.That(status2, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);

            var answer   = await csms.Ask("Reset", new JObject(new JProperty("type", "Immediate")), Destination: "cs001");

            await Task.Delay(TimeSpan.FromMilliseconds(500));

            var atCS002  = stationsGot.Where(got => got.Item1 == "cs002" && got.Item2[0].Value<Int32>() is 3 or 4).ToArray();

            Assert.Multiple(() => {
                Assert.That(answer[0].Value<Int32>(),  Is.EqualTo(3),  $"the CSMS did not get the station's answer: {answer.ToString(Formatting.None)}");
                Assert.That(atCS002,                   Is.Empty,       $"the station's answer went to another station: {String.Join(" ", atCS002.Select(got => got.Item2.ToString(Formatting.None)))}");
            });

        }

        #endregion

        #region AStation_CannotSendInAnotherStationsName()

        /// <summary>
        /// A station sends the CSMS a security event in another station's name: dropped, while one in its own
        /// name goes up as before. In the overlay network the CSMS would take the first for the other
        /// station's - in standard mode for the controller's.
        /// </summary>
        [Test]
        public async Task AStation_CannotSendInAnotherStationsName()
        {

            var (cs001, status) = await Connect("cs001", cs001Key, currentPin, nextPin);

            Assert.That(status, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);

            JObject Event(String Marker)
                => new (new JProperty("type",       "SettingSystemTime"),
                        new JProperty("timestamp",  DateTimeOffset.UtcNow.ToString("o")),
                        new JProperty("techInfo",   Marker));

            await cs001.SendTextMessage(new JArray(2, "CSMS", "cs002", "forged-1", "SecurityEventNotification", Event("forged")).ToString(Formatting.None));
            await cs001.SendTextMessage(new JArray(2, "CSMS", "cs001", "honest-1", "SecurityEventNotification", Event("honest")).ToString(Formatting.None));

            var honest = await csms.SentRequest("SecurityEventNotification", payload => payload["techInfo"]?.Value<String>() == "honest", EventPatience);

            await Task.Delay(TimeSpan.FromMilliseconds(500));

            var forged = csms.Requests("SecurityEventNotification").Where(request => PayloadOf(request)?["techInfo"]?.Value<String>() == "forged").ToArray();

            Assert.Multiple(() => {
                Assert.That(honest,  Is.Not.Null, "the station's event in its own name did not reach the CSMS");
                Assert.That(forged,  Is.Empty,     $"a station's event in another station's name reached the CSMS: {String.Join(" ", forged.Select(request => request.ToString(Formatting.None)))}");
            });

        }

        #endregion

    }

}
