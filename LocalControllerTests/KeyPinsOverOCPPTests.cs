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
    /// The key pins of the station port as the CSMS sees them, and the switch to the next key as the
    /// CSMS orders it: EVQICtrlr[TLSServer] of the local controller's own Device Model (white paper
    /// "Key Pins and TLS 1.3 Raw Public Keys for Local Controllers", sections 5.5 and 7.4), over the
    /// controller's real uplink and real station connections.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The controller 'lc002' has a server key and an announced next key, dials a CSMS and lets the
    /// stations cs001 and cs002 in by the key pins of their own keys over wss://. The stations pin the
    /// controller by both of its pins, as the CSMS would have given them before a rollover.
    /// </para>
    /// <para>
    /// The CSMS is of the least kind: a Hermod WebSocket server that answers what the controller sends
    /// and sends the frames a test writes, as they go over the wire. A request addressed to the
    /// controller itself is a plain OCPP frame, the controller's uplink being a standard one; a request
    /// for a station behind it carries its destination, in the frame of the overlay network.
    /// </para>
    /// </remarks>
    public class KeyPinsOverOCPPTests
    {

        #region (class) CSMS

        /// <summary>
        /// A CSMS of the least kind: it takes every request of the controller and answers it with what
        /// is least, keeps what it was sent, and asks the controller what a test tells it to.
        /// </summary>
        internal sealed class CSMS : IAsyncDisposable
        {

            private readonly ConcurrentQueue<JArray>                                received  = new();
            private readonly ConcurrentDictionary<String, TaskCompletionSource<JArray>>  waiting   = new();
            private          Int32                                                   lastId;

            public WebSocketServer  Server  { get; }

            public CSMS(UInt16 Port)
            {

                Server = new WebSocketServer(
                             HTTPPort:               IPPort.Parse(Port),
                             RequireAuthentication:  false,
                             SecWebSocketProtocols:  [ "ocpp2.1" ],
                             DisableWebSocketPings:  true,
                             AutoStart:              true
                         );

                Server.OnTextMessageReceived += async (timestamp, server, connection, frame, eventTrackingId, text, cancellationToken) => {

                    var message = Frame(text);

                    received.Enqueue(message);

                    switch (message[0].Value<Int32>())
                    {

                        // A request of the controller's.
                        case 2:
                            await Server.SendTextMessage(connection, new JArray(3, message[1], AnswerTo(message[2].Value<String>() ?? "")).ToString(Formatting.None));
                            break;

                        // An answer to one of this CSMS's own.
                        case 3:
                        case 4:
                            if (waiting.TryRemove(message[1].Value<String>() ?? "", out var answer))
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
            /// How many connections it has.
            /// </summary>
            public Int32 Connections
                => Server.WebSocketConnections.Count();

            /// <summary>
            /// Send the controller a request and wait for its answer: addressed to the controller itself
            /// where no destination is given, in a plain OCPP frame - or to the given destination behind
            /// it, in the frame of the overlay network, which carries the destination and where the
            /// request comes from.
            /// </summary>
            public async Task<JArray> Ask(String   Action,
                                          JObject  Payload,
                                          String?  Destination   = null)
            {

                var id      = $"csms-{Interlocked.Increment(ref lastId)}";
                var answer  = new TaskCompletionSource<JArray>(TaskCreationOptions.RunContinuationsAsynchronously);

                waiting[id] = answer;

                var frame   = Destination is null
                                  ? new JArray(2, id, Action, Payload)
                                  : new JArray(2, Destination, "CSMS", id, Action, Payload);

                await Server.SendTextMessage(Server.WebSocketConnections.Single(), frame.ToString(Formatting.None));

                if (await Task.WhenAny(answer.Task, Task.Delay(Guard)) != answer.Task)
                    Assert.Fail($"The controller did not answer {Action} {id}.");

                return await answer.Task;

            }

            /// <summary>
            /// The answer to a request of the given id that reached this CSMS without being asked for
            /// here, if any.
            /// </summary>
            public JArray? AnswerOf(String RequestId)
                => received.FirstOrDefault(message => message[0].Value<Int32>() is 3 or 4 &&
                                                      message[1].Value<String>() == RequestId);

            /// <summary>
            /// The requests of the given action the controller sent so far.
            /// </summary>
            public JArray[] Requests(String Action)
                => [.. received.Where(message => message[0].Value<Int32>() == 2 &&
                                                 message[2].Value<String>() == Action)];

            /// <summary>
            /// The first request of the given action whose payload is what is looked for, once there is
            /// one or the given time has passed.
            /// </summary>
            public async Task<JArray?> SentRequest(String                 Action,
                                                   Func<JObject, Boolean>  Wanted,
                                                   TimeSpan               Patience)
            {

                var giveUp = DateTimeOffset.UtcNow + Patience;

                do
                {

                    if (Requests(Action).FirstOrDefault(message => message[3] is JObject payload && Wanted(payload)) is JArray found)
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

        #region (static) Frame(Text)

        /// <summary>
        /// A frame as it was sent: the dates in it as the strings they were, which Json.NET would
        /// otherwise read as dates and write back in another format.
        /// </summary>
        internal static JArray Frame(String Text)
        {

            using var reader = new JsonTextReader(new StringReader(Text)) { DateParseHandling = DateParseHandling.None };

            return JArray.Load(reader);

        }

        #endregion

        #region Data

        private static readonly TimeSpan Guard            = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan EventPatience    = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long a security event that should not come is waited for.
        /// </summary>
        private static readonly TimeSpan NoEventPatience  = TimeSpan.FromSeconds(1.5);

        private const String ThePassword = "a-password-long-enough-for-ocpp";

        private String           directory   = default!;
        private CSMS             csms        = default!;
        private LocalController  controller  = default!;
        private UInt16           port;

        private KeyPin           currentPin  = default!;
        private KeyPin           nextPin     = default!;
        private String           nextId      = default!;

        private ECDsa            cs001Key    = default!;
        private ECDsa            cs002Key    = default!;

        private readonly ConcurrentQueue<String>           said          = [];
        private readonly ConcurrentQueue<(String, JArray)>  stationsGot   = [];
        private readonly List<IDisposable>                  made          = [];
        private readonly List<WebSocketClient>              clients       = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task StartAControllerWithTwoKeysAndACSMS()
        {

            directory = TestControllers.TemporaryDirectory("keypins-ocpp");
            Directory.CreateDirectory(directory);

            said.       Clear();
            stationsGot.Clear();

            cs001Key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            cs002Key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            #region The server key and the next one, made before anything starts

            // Before the controller is built, because whether the port speaks
            // TLS at all is decided when it starts.
            using (var store = new ServerCertificateStore(Path.Combine(directory, ServerCertificateStore.DefaultDirectoryName)))
            {

                Assert.That(store.TryCreateServerKey(null, AsNext: false, out _,       out var current, out var error),     Is.True, error);
                Assert.That(store.TryCreateServerKey(null, AsNext: true,  out var id,  out var next,    out var nextError), Is.True, nextError);

                currentPin  = current!;
                nextPin     = next!;
                nextId      = id!;

            }

            #endregion

            var csmsPort  = TestPorts.Free();

            csms          = new CSMS(csmsPort);

            controller    = await TestPorts.StartedOnFreshPorts(() => {

                                port      = TestPorts.Free();

                                var lc    = TestControllers.New(
                                                directory,
                                                new JObject(

                                                    new JProperty("nts",  new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                                    new JProperty("ocpp", new JObject(new JProperty("nodeId", "lc002"))),

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
                                                        // The CSMS here speaks the plain OCPP frame alone.
                                                        new JProperty("networkingMode",         "standard"),
                                                        new JProperty("reconnectInitialDelay",  1),
                                                        new JProperty("reconnectMaxDelay",      2)
                                                    ))

                                                )
                                            );

                                lc.Log.OnLogged += entry => said.Enqueue(entry.Message);

                                Assert.That(lc.TrySetUplinkPassword("lc002", ThePassword, out var loginError), Is.True, loginError);

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
                Assert.That(controller.OCPPServerTLS,  Is.True, "a controller with a server key is not encrypted");
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


        #region (private) Certificate(Key, CommonName)

        /// <summary>
        /// A self-signed client certificate for the given key, as a station without a certificate
        /// authority has one.
        /// </summary>
        private X509Certificate2 Certificate(ECDsa   Key,
                                             String  CommonName)
        {

            var request = new CertificateRequest($"CN={CommonName}", Key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.2") ], false));

            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            // SChannel wants a key it can find again: out to PKCS#12 and back.
            var usable = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet);

            made.Add(usable);

            return usable;

        }

        #endregion

        #region (private) Connect(Station, Key, params Pins)

        /// <summary>
        /// A charging station connecting to its path over wss://, checking the controller by the given
        /// key pins alone - what it was answered, and which key the controller presented. It answers
        /// what the controller forwards to it as a station without EVQICtrlr does.
        /// </summary>
        private async Task<(WebSocketClient Client, HTTPStatusCode? Status, KeyPinValidation[] Validations)> Connect(String           Station,
                                                                                                                     ECDsa            Key,
                                                                                                                     params KeyPin[]  Pins)
        {

            var told         = new ConcurrentQueue<KeyPinValidation>();
            var certificate  = Certificate(Key, "station.invalid");

            var client       = new WebSocketClient(
                                   URL.Parse($"wss://127.0.0.1:{port}/{Station}"),
                                   SecWebSocketProtocols:       [ "ocpp2.1" ],
                                   RemoteCertificateValidator:  KeyPinValidator.ForServerCertificates<IWebSocketClient>(
                                                                    KeyPinPolicy.PinnedTo(Pins),
                                                                    told.Enqueue
                                                                ),
                                   LocalCertificateSelector:    (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) => certificate,
                                   ClientCertificates:          [ certificate ],
                                   DisableWebSocketPings:       true,
                                   ConnectTimeout:              TimeSpan.FromSeconds(10)
                               );

            clients.Add(client);

            client.OnTextMessageReceived += async (timestamp, sender, connection, frame, eventTrackingId, text, cancellationToken) => {

                var message = Frame(text);

                stationsGot.Enqueue((Station, message));

                if (message.Count == 4 && message[0].Value<Int32>() == 2 && message[3] is JObject payload)
                    await sender.SendTextMessage(new JArray(3, message[1], StationAnswerTo(message[2].Value<String>() ?? "", payload)).ToString(Formatting.None));

            };

            try
            {

                var connect = client.Connect();

                if (await Task.WhenAny(connect, Task.Delay(Guard)) != connect)
                    Assert.Fail("Connect() did not return.");

                var (_, response) = await connect;

                return (client, response?.HTTPStatusCode, [.. told]);

            }
            catch (Exception)
            {
                // Refused during the handshake: no answer at all, which is not
                // a 101 either.
                return (client, null, [.. told]);
            }

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

        #region (private) StillUp(Client)

        /// <summary>
        /// Whether the given station's connection still carries frames both ways: a ping, answered.
        /// </summary>
        private static async Task<Boolean> StillUp(WebSocketClient Client)
        {

            var payload  = RandomNumberGenerator.GetBytes(8);
            var pong     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            Task OnPong(DateTimeOffset timestamp, WebSocketClient client, WebSocketClientConnection connection, WebSocketFrame frame, EventTracking_Id eventTrackingId, Byte[] message, CancellationToken cancellationToken)
            {
                if (message.AsSpan().SequenceEqual(payload))
                    pong.TrySetResult();
                return Task.CompletedTask;
            }

            Client.OnPongMessageReceived += OnPong;

            try
            {

                await Client.SendWebSocketFrame(WebSocketFrame.Ping(payload, WebSocketFrame.Fin.Final, WebSocketFrame.MaskStatus.On, RandomNumberGenerator.GetBytes(4)));

                return await Task.WhenAny(pong.Task, Task.Delay(TimeSpan.FromSeconds(10))) == pong.Task;

            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                Client.OnPongMessageReceived -= OnPong;
            }

        }

        #endregion

        #region (private) GetTLSServer() / SetActiveKeyPin(Pin, Destination = null)

        /// <summary>
        /// GetVariables of every variable of EVQICtrlr[TLSServer], from the controller itself: variable
        /// name to (status, value).
        /// </summary>
        private async Task<Dictionary<String, (String? Status, String? Value)>> GetTLSServer()
        {

            var answer = await csms.Ask(
                                   "GetVariables",
                                   new JObject(
                                       new JProperty("getVariableData", new JArray(
                                           new[] { "KeyPins", "NextKeyPin", "KeyCreated", "RawPublicKeys", "ActiveKeyPin" }.Select(variable => new JObject(
                                               new JProperty("component", EVQICtrlrTLSServer),
                                               new JProperty("variable",  new JObject(new JProperty("name", variable)))
                                           ))
                                       ))
                                   )
                               );

            Assert.That(answer[0].Value<Int32>(), Is.EqualTo(3), answer.ToString(Formatting.None));

            return (answer[2]?["getVariableResult"] as JArray ?? []).
                       OfType<JObject>().
                       ToDictionary(result => result["variable"]?["name"]?.Value<String>() ?? "",
                                    result => (result["attributeStatus"]?.Value<String>(),
                                               result["attributeValue"]?. Value<String>()));

        }

        /// <summary>
        /// SetVariables of EVQICtrlr[TLSServer].ActiveKeyPin to the given value: the result's status,
        /// reason code and additional info.
        /// </summary>
        private async Task<(String? Status, String? Reason, String? Info)> SetActiveKeyPin(String   Value,
                                                                                         String?  Destination   = null)
        {

            var answer = await csms.Ask("SetVariables", ActiveKeyPinPayload(Value), Destination);

            Assert.That(answer[0].Value<Int32>(), Is.EqualTo(3), answer.ToString(Formatting.None));

            var result = (answer[2]?["setVariableResult"] as JArray)?.OfType<JObject>().Single();

            return (result?["attributeStatus"]?.Value<String>(),
                    result?["attributeStatusInfo"]?["reasonCode"]?.    Value<String>(),
                    result?["attributeStatusInfo"]?["additionalInfo"]?.Value<String>());

        }

        /// <summary>
        /// The payload of a SetVariables of EVQICtrlr[TLSServer].ActiveKeyPin to the given value.
        /// </summary>
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

        private static JObject EVQICtrlrTLSServer
            => new (new JProperty("name", "EVQICtrlr"), new JProperty("instance", "TLSServer"));

        /// <summary>
        /// The answer to a station's own request of the given id, once there is one or the guard has
        /// passed - wherever the controller sent it: to the station, or to whom the request said it came
        /// from.
        /// </summary>
        private async Task<JArray?> AnswerTo(String RequestId)
        {

            var giveUp = DateTimeOffset.UtcNow + Guard;

            do
            {

                var answer = stationsGot.FirstOrDefault(got => got.Item2[0].Value<Int32>() is 3 or 4 &&
                                                               got.Item2[1].Value<String>() == RequestId).Item2
                                 ?? csms.AnswerOf(RequestId);

                if (answer is not null)
                    return answer;

                await Task.Delay(20);

            }
            while (DateTimeOffset.UtcNow < giveUp);

            return null;

        }

        #endregion

        #region (private) KeySwitchEvents(Within) / Registered / Log / APin()

        /// <summary>
        /// The security events ReconfigurationOfSecurityParameters the CSMS got, once there is one or the
        /// given time has passed.
        /// </summary>
        private async Task<String[]> KeySwitchEvents(TimeSpan Within)
        {

            await csms.SentRequest("SecurityEventNotification",
                                   payload => payload["type"]?.Value<String>() == "ReconfigurationOfSecurityParameters",
                                   Within);

            return [.. csms.Requests("SecurityEventNotification").
                            Select(request => request[3] as JObject).
                            Where (payload => payload?["type"]?.Value<String>() == "ReconfigurationOfSecurityParameters").
                            Select(payload => payload?["techInfo"]?.Value<String>() ?? "")];

        }

        /// <summary>
        /// The charging stations the station port has a connection of now.
        /// </summary>
        private String[] Registered
            => [ .. controller.StationServer!.ConnectedNetworkingNodeIds.Select(id => id.ToString()).Order() ];

        /// <summary>
        /// What the controller said, for the message of an assertion.
        /// </summary>
        private String Log
            => String.Join(Environment.NewLine, said);

        private static KeyPin APin()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return KeyPin.FromKey(key);
        }

        #endregion


        #region GetVariables_TheCSMSReadsBothKeyPins()

        /// <summary>
        /// The CSMS reads the pins it hands the stations ahead of a rollover: the current key's first,
        /// then the next key's, when the current key was made, and that no raw public keys are offered.
        /// </summary>
        [Test]
        public async Task GetVariables_TheCSMSReadsBothKeyPins()
        {

            var tlsServer = await GetTLSServer();

            Assert.Multiple(() => {

                Assert.That(tlsServer.Values.Select(result => result.Status),  Is.All.EqualTo("Accepted"), String.Join(", ", tlsServer));
                Assert.That(tlsServer["KeyPins"].      Value,                  Is.EqualTo($"{currentPin.ToHex()},{nextPin.ToHex()}"));
                Assert.That(tlsServer["NextKeyPin"].   Value,                  Is.EqualTo(nextPin.ToHex()));
                Assert.That(tlsServer["ActiveKeyPin"]. Value,                  Is.EqualTo(currentPin.ToHex()));
                Assert.That(tlsServer["KeyCreated"].   Value,                  Is.EqualTo(controller.ServerCertificates.CurrentKey!.CreatedAt.ToISO8601()));
                Assert.That(tlsServer["RawPublicKeys"].Value,                  Is.EqualTo("Off"));

            });

        }

        #endregion

        #region SetVariables_TheNextKeyPin_SwitchesWithoutDroppingAStation()

        /// <summary>
        /// The rollover itself. The CSMS writes the next key's pin into ActiveKeyPin: accepted, and the
        /// station port presents the next key from the next handshake on - to a station that connects
        /// now, which sees the new pin, and is refused where it pinned only the old one. A station
        /// connected before stays connected. The CSMS is told by a security event with both pins, the log
        /// says who switched, the CSMS reads the new key as the only one, and the old key is no way back.
        /// </summary>
        [Test]
        public async Task SetVariables_TheNextKeyPin_SwitchesWithoutDroppingAStation()
        {

            var (cs001, cs001Status, cs001Saw) = await Connect("cs001", cs001Key, currentPin, nextPin);

            Assert.Multiple(() => {
                Assert.That(cs001Status,                           Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);
                Assert.That(cs001Saw.Select(result => result.Pin), Has.All.EqualTo(currentPin), "before the switch the current key is presented");
            });

            var switched                          = await SetActiveKeyPin(nextPin.ToHex());

            var stillUp                           = await StillUp(cs001);

            var (cs002, cs002Status, cs002Saw)    = await Connect("cs002", cs002Key, currentPin, nextPin);
            var (_,     oldOnlyStatus, _)         = await Connect("cs002", cs002Key, currentPin);

            var events                            = await KeySwitchEvents(EventPatience);
            var tlsServer                         = await GetTLSServer();
            var back                              = await SetActiveKeyPin(currentPin.ToHex());

            Assert.Multiple(() => {

                Assert.That(switched.Status,                            Is.EqualTo("Accepted"), switched.Info);
                Assert.That(controller.ServerCertificates.CurrentKey?.KeyPin,
                                                                        Is.EqualTo(nextPin),    "the store was not switched");
                Assert.That(controller.ServerCertificates.NextKey,      Is.Null);

                Assert.That(stillUp,                                    Is.True,                "the station connected before the switch was dropped");
                Assert.That(cs001.Connected,                            Is.True,                "the station connected before the switch was dropped");
                Assert.That(Registered,                                 Has.Member("cs001"),    "the station connected before the switch was dropped");

                Assert.That(cs002Status,                                Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);
                Assert.That(cs002Saw.Select(result => result.Pin),      Has.All.EqualTo(nextPin), "a new connection does not see the new key");
                Assert.That(oldOnlyStatus,                              Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "a station that pinned only the old key was let in after the switch");

                Assert.That(events,                                     Has.Length.EqualTo(1),  "the CSMS was not told of the switch exactly once");
                Assert.That(events.FirstOrDefault() ?? "",              Does.Contain(currentPin.ToHex()).And.Contain(nextPin.ToHex()));
                Assert.That(said,                                       Has.Some.Contains("'CSMS' switched the charging station server to the server key").
                                                                                 And.Some.Contains("ActiveKeyPin"));

                Assert.That(tlsServer["KeyPins"].     Value,            Is.EqualTo(nextPin.ToHex()));
                Assert.That(tlsServer["NextKeyPin"].  Value,            Is.Empty);
                Assert.That(tlsServer["ActiveKeyPin"].Value,            Is.EqualTo(nextPin.ToHex()));

                Assert.That(back.Status,                                Is.EqualTo("Rejected"), "the key switched away from is a way back");
                Assert.That(back.Info ?? "",                            Does.Contain("none announced"));
                Assert.That(controller.ServerCertificates.CurrentKey?.KeyPin,
                                                                        Is.EqualTo(nextPin));

            });

        }

        #endregion

        #region SetVariables_TheCurrentKeyPin_IsAcceptedAndChangesNothing()

        /// <summary>
        /// The write is idempotent: the current key's pin - a CSMS repeating its switch after a lost
        /// response, or confirming the key - is accepted, and nothing is switched or reported.
        /// </summary>
        [Test]
        public async Task SetVariables_TheCurrentKeyPin_IsAcceptedAndChangesNothing()
        {

            var result  = await SetActiveKeyPin(currentPin.ToHexWithColons());
            var events  = await KeySwitchEvents(NoEventPatience);

            Assert.Multiple(() => {
                Assert.That(result.Status,                                       Is.EqualTo("Accepted"), result.Info);
                Assert.That(controller.ServerCertificates.CurrentKey?.KeyPin,    Is.EqualTo(currentPin));
                Assert.That(controller.ServerCertificates.NextKey?.KeyPin,       Is.EqualTo(nextPin));
                Assert.That(events,                                              Is.Empty, "a switch was reported where nothing switched");
            });

        }

        #endregion

        #region SetVariables_APinThatIsNeither_IsRejected()

        /// <summary>
        /// A pin that is neither the current key's nor the next key's is refused, with a reason that
        /// names both; nothing is switched or reported.
        /// </summary>
        [Test]
        public async Task SetVariables_APinThatIsNeither_IsRejected()
        {

            var result  = await SetActiveKeyPin(APin().ToHex());
            var events  = await KeySwitchEvents(NoEventPatience);

            Assert.Multiple(() => {
                Assert.That(result.Status,                                       Is.EqualTo("Rejected"));
                Assert.That(result.Reason,                                       Is.EqualTo("InvalidValue"));
                Assert.That(result.Info ?? "",                                   Does.Contain(currentPin.ToHex()).And.Contain(nextPin.ToHex()), "the reason does not name the current and the next key");
                Assert.That(controller.ServerCertificates.CurrentKey?.KeyPin,    Is.EqualTo(currentPin));
                Assert.That(controller.ServerCertificates.NextKey?.KeyPin,       Is.EqualTo(nextPin));
                Assert.That(events,                                              Is.Empty);
            });

        }

        #endregion

        #region SetVariables_WithoutANextKey_IsRejected()

        /// <summary>
        /// Where no next key is announced there is nothing to switch to: the pin of the key that was
        /// announced and taken away again, and any other, are refused, and the current key stays.
        /// </summary>
        [Test]
        public async Task SetVariables_WithoutANextKey_IsRejected()
        {

            Assert.That(controller.ServerCertificates.TryRemove(nextId, out var error), Is.True, error);

            var former  = await SetActiveKeyPin(nextPin.ToHex());
            var other   = await SetActiveKeyPin(APin().ToHex());

            Assert.Multiple(() => {
                Assert.That(former.Status,                                       Is.EqualTo("Rejected"), "a key taken away was switched to");
                Assert.That(former.Info ?? "",                                   Does.Contain("none announced"));
                Assert.That(other. Status,                                       Is.EqualTo("Rejected"));
                Assert.That(controller.ServerCertificates.CurrentKey?.KeyPin,    Is.EqualTo(currentPin));
            });

        }

        #endregion

        #region TheSwitch_IsComparedWithTheNextKeyOfTheMoment()

        /// <summary>
        /// Between EVQICtrlr's look at the keys and the switch, the keys can change on the command line or
        /// the page. The switch gets the pin the CSMS wrote, and switches to that key or to none: a next
        /// key announced meanwhile in place of it is not switched to, a switch to it made meanwhile is
        /// not made again and answered as done, and with no next key at all nothing is switched.
        /// </summary>
        [Test]
        public async Task TheSwitch_IsComparedWithTheNextKeyOfTheMoment()
        {

            var keySwitch  = new LocalController.TLSServerKeys(controller);
            var store      = controller.ServerCertificates;

            #region The next key the CSMS saw was taken away, and another announced

            Assert.That(store.TryRemove(nextId, out var removeError), Is.True, removeError);
            Assert.That(store.TryCreateServerKey(null, AsNext: true, out _, out var otherNext, out var createError), Is.True, createError);

            var replaced = await keySwitch.SwitchToNextKeyAsync(nextPin, null, CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(replaced?.ReasonCode,              Is.EqualTo("InvalidValue"), "the switch to a key no longer announced was accepted");
                Assert.That(replaced?.AdditionalInfo ?? "",    Does.Contain(currentPin.ToHex()).And.Contain(otherNext!.ToHex()));
                Assert.That(store.CurrentKey?.KeyPin,          Is.EqualTo(currentPin), "another key than the one asked for was switched to");
                Assert.That(store.NextKey?.KeyPin,             Is.EqualTo(otherNext));
            });

            #endregion

            #region No next key at all

            Assert.That(store.TryRemove(store.NextKey!.Id, out var removeAgainError), Is.True, removeAgainError);

            var none = await keySwitch.SwitchToNextKeyAsync(otherNext!, null, CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(none?.ReasonCode,                  Is.EqualTo("InvalidValue"), "a switch with no next key was accepted");
                Assert.That(store.CurrentKey?.KeyPin,          Is.EqualTo(currentPin));
            });

            #endregion

            #region Switched meanwhile, on the command line, and a new next key announced after it

            Assert.That(store.TryCreateServerKey(null, AsNext: true, out _, out var third,  out var thirdError),  Is.True, thirdError);
            Assert.That(store.TrySwitchToNextKey(out _, out var switchError),                                     Is.True, switchError);
            Assert.That(store.TryCreateServerKey(null, AsNext: true, out _, out var fourth, out var fourthError), Is.True, fourthError);

            var done = await keySwitch.SwitchToNextKeyAsync(third!, null, CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(done,                              Is.Null,               $"a switch made meanwhile was refused: {done?.AdditionalInfo}");
                Assert.That(store.CurrentKey?.KeyPin,          Is.EqualTo(third),     "the switch was made again, to the key announced after it");
                Assert.That(store.NextKey?.KeyPin,             Is.EqualTo(fourth));
            });

            #endregion

        }

        #endregion

        #region SetVariables_ForTheStationBehind_IsForwardedAndSwitchesNothing()

        /// <summary>
        /// The same write addressed to a station behind the controller is the station's: forwarded to
        /// it, answered by it, and the controller's own keys stay as they were.
        /// </summary>
        [Test]
        public async Task SetVariables_ForTheStationBehind_IsForwardedAndSwitchesNothing()
        {

            var (cs001, status, _) = await Connect("cs001", cs001Key, currentPin, nextPin);

            Assert.That(status, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);

            var result  = await SetActiveKeyPin(nextPin.ToHex(), Destination: "cs001");
            var events  = await KeySwitchEvents(NoEventPatience);

            var atStation = stationsGot.Where(got => got.Item1 == "cs001" &&
                                                     got.Item2[0].Value<Int32>() == 2 &&
                                                     got.Item2[2].Value<String>() == "SetVariables").ToArray();

            Assert.Multiple(() => {
                Assert.That(atStation,                                           Has.Length.EqualTo(1), "the station did not get the request");
                Assert.That(result.Status,                                       Is.EqualTo("UnknownComponent"), "the answer is not the station's");
                Assert.That(controller.ServerCertificates.CurrentKey?.KeyPin,    Is.EqualTo(currentPin), "the controller's key was switched by a request for a station");
                Assert.That(controller.ServerCertificates.NextKey?.KeyPin,       Is.EqualTo(nextPin));
                Assert.That(events,                                              Is.Empty);
            });

        }

        #endregion

        #region SetVariables_FromAStation_IsRefused()

        /// <summary>
        /// A station on the station port that addresses the controller itself, in the frame of the
        /// overlay network, cannot switch the key every station of the site pins - neither as itself nor
        /// calling itself the CSMS, which that frame lets it write: refused at the door with a
        /// SecurityError, and nothing is switched or reported.
        /// </summary>
        [Test]
        public async Task SetVariables_FromAStation_IsRefused()
        {

            var (cs001, status, _) = await Connect("cs001", cs001Key, currentPin, nextPin);

            Assert.That(status, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), Log);

            await cs001.SendTextMessage(new JArray(2, "lc002", "cs001", "station-1", "SetVariables", ActiveKeyPinPayload(nextPin.ToHex())).ToString(Formatting.None));
            await cs001.SendTextMessage(new JArray(2, "lc002", "CSMS",  "station-2", "SetVariables", ActiveKeyPinPayload(nextPin.ToHex())).ToString(Formatting.None));

            var asItself  = await AnswerTo("station-1");
            var asCSMS    = await AnswerTo("station-2");
            var events    = await KeySwitchEvents(NoEventPatience);

            Assert.Multiple(() => {

                Assert.That(asItself?[0].Value<Int32>(),                         Is.EqualTo(4), $"a station's write of the controller's ActiveKeyPin was not refused: {asItself?.ToString(Formatting.None)}");
                Assert.That(asItself?[2].Value<String>(),                        Is.EqualTo("SecurityError"));
                Assert.That(asCSMS?  [0].Value<Int32>(),                         Is.EqualTo(4), $"a station calling itself the CSMS was not refused: {asCSMS?.ToString(Formatting.None)}");
                Assert.That(asCSMS?  [2].Value<String>(),                        Is.EqualTo("SecurityError"));

                Assert.That(controller.ServerCertificates.CurrentKey?.KeyPin,    Is.EqualTo(currentPin), "a station switched the controller's key");
                Assert.That(controller.ServerCertificates.NextKey?.KeyPin,       Is.EqualTo(nextPin));
                Assert.That(events,                                              Is.Empty);

            });

        }

        #endregion

        #region ASwitchOnTheCommandLine_IsToldToTheCSMS()

        /// <summary>
        /// A switch the CSMS did not ask for - on the command line, as "serverKeys switch" makes it - is
        /// told to it all the same: the stations' pins are the CSMS's to hand out.
        /// </summary>
        [Test]
        public async Task ASwitchOnTheCommandLine_IsToldToTheCSMS()
        {

            Assert.That(controller.ServerCertificates.TrySwitchToNextKey(out _, out var error), Is.True, error);

            var events = await KeySwitchEvents(EventPatience);

            Assert.Multiple(() => {
                Assert.That(events,                          Has.Length.EqualTo(1), "the CSMS was not told of the switch");
                Assert.That(events.FirstOrDefault() ?? "",   Is.EqualTo($"TLS server key switched: key pin {currentPin.ToHex()} -> {nextPin.ToHex()}"));
            });

        }

        #endregion

    }

}
