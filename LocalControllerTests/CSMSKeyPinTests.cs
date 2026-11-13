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
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.OCPP.Node.KeyPins;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The line up to the CSMS, key-pinned as a charging station's wss:// connections are: a CSMS whose
    /// certificate no CA vouches for is believed by the key pin of its key, learned on first use and on
    /// probation - and while it is, nothing it sends is carried out or sent on to a station, but the pin
    /// set for this controller's own TLSClientController - until a signed pin set confirms the key.
    /// </summary>
    /// <remarks>
    /// The CSMS is a server of the least kind over wss://, with a self-signed certificate this
    /// controller has no root for: without its key pin, the controller would not believe it at all.
    /// </remarks>
    [TestFixture]
    public class CSMSKeyPinTests
    {

        #region (class) CSMS

        /// <summary>
        /// A CSMS of the least kind over TLS: it answers every request of the controller with what is
        /// least, keeps what it was sent, and asks the controller what a test tells it to.
        /// </summary>
        private sealed class CSMS : IAsyncDisposable
        {

            private readonly ConcurrentQueue<JArray>                                    received  = new();
            private readonly ConcurrentDictionary<String, TaskCompletionSource<JArray>>  waiting   = new();
            private          Int32                                                       lastId;

            public WebSocketServer   Server       { get; }
            public X509Certificate2  Certificate  { get; }

            public CSMS(X509Certificate2 Certificate)
            {

                this.Certificate = Certificate;

                Server = new WebSocketServer(
                             HTTPPort:                   IPPort.Zero,
                             RequireAuthentication:      false,
                             ServerCertificateSelector:  (tcpServer, tcpClient) => Certificate,
                             SecWebSocketProtocols:      [ "ocpp2.1" ],
                             DisableWebSocketPings:      true,
                             AutoStart:                  true
                         );

                Server.OnTextMessageReceived += async (timestamp, server, connection, frame, eventTrackingId, text, cancellationToken) => {

                    var raw      = KeyPinsOverOCPPTests.Frame(text);
                    var overlay  = IsOverlay(raw);
                    var message  = Plain(raw);

                    received.Enqueue(message);

                    switch (message[0].Value<Int32>())
                    {

                        // Answered in the frame it came in: in the overlay network to whoever sent it.
                        case 2:
                            await Server.SendTextMessage(connection, (overlay
                                                                         ? new JArray(3, raw[2], new JArray("CSMS"), message[1], AnswerTo(message[2].Value<String>() ?? ""))
                                                                         : new JArray(3, message[1], AnswerTo(message[2].Value<String>() ?? ""))).ToString(Formatting.None));
                            break;

                        case 3:
                        case 4:
                            if (waiting.TryRemove(message[1].Value<String>() ?? "", out var answer))
                                answer.TrySetResult(message);
                            break;

                    }

                };

            }

            /// <summary>
            /// Whether a frame is one of the overlay network, which names where it goes and where it came
            /// from: [2, destination, source, id, action, payload], [3, destination, path, id, payload],
            /// [4, destination, path, id, code, description, details].
            /// </summary>
            private static Boolean IsOverlay(JArray Frame)

                => Frame[0].Value<Int32>() switch {
                       2 => Frame.Count == 6,
                       3 => Frame.Count == 5,
                       4 => Frame.Count == 7,
                       _ => false
                   };

            /// <summary>
            /// A frame in the plain OCPP form: [2, id, action, payload], [3, id, payload], [4, id, code,
            /// description, details].
            /// </summary>
            private static JArray Plain(JArray Frame)

                => !IsOverlay(Frame)
                       ? Frame
                       : Frame[0].Value<Int32>() switch {
                             2 => new JArray(2, Frame[3], Frame[4], Frame[5]),
                             3 => new JArray(3, Frame[3], Frame[4]),
                             _ => new JArray(4, Frame[3], Frame[4], Frame[5], Frame[6])
                         };

            private static JObject AnswerTo(String Action)

                => Action switch {
                       "BootNotification"  => new JObject(new JProperty("status", "Accepted"), new JProperty("currentTime", DateTimeOffset.UtcNow.ToString("o")), new JProperty("interval", 300)),
                       "Heartbeat"         => new JObject(new JProperty("currentTime", DateTimeOffset.UtcNow.ToString("o"))),
                       _                   => new JObject()
                   };

            public UInt16 Port
                => Server.IPPort.ToUInt16();

            public Int32 Connections
                => Server.WebSocketConnections.Count();

            /// <summary>
            /// Send the controller a request and wait for its answer: to the controller itself, in a plain
            /// frame, or to a destination behind it in the frame of the overlay network.
            /// </summary>
            public async Task<JArray> Ask(String   Action,
                                          JObject  Payload,
                                          String?  Id            = null,
                                          String?  Destination   = null)
            {

                var id      = Id ?? $"csms-{Interlocked.Increment(ref lastId)}";
                var answer  = new TaskCompletionSource<JArray>(TaskCreationOptions.RunContinuationsAsynchronously);

                waiting[id] = answer;

                var frame   = Destination is null
                                  ? new JArray(2, id, Action, Payload)
                                  : new JArray(2, Destination, "CSMS", id, Action, Payload);

                await Server.SendTextMessage(Server.WebSocketConnections.Single(), frame.ToString(Formatting.None));

                if (await Task.WhenAny(answer.Task, Task.Delay(TimeSpan.FromSeconds(30))) != answer.Task)
                    Assert.Fail($"The controller did not answer {Action} {id}. The CSMS got: {String.Join(" | ", received.Select(message => message.ToString(Formatting.None)))}");

                return await answer.Task;

            }

            /// <summary>
            /// The security events of the given type the controller sent so far.
            /// </summary>
            public JObject[] SecurityEvents(String Type)
                => [.. received.Where (message => message[0].Value<Int32>() == 2 && message[2].Value<String>() == "SecurityEventNotification").
                                Select(message => message[3] as JObject).
                                OfType<JObject>().
                                Where (payload => payload.Value<String>("type") == Type)];

            public async ValueTask DisposeAsync()
            {
                await Server.Shutdown(Wait: true);
                Certificate.Dispose();
            }

        }

        #endregion

        #region Data

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

        private String            directory   = "";
        private CSMS?             csms;
        private LocalController?  controller;

        private readonly ECCKeyPair signer = ECCKeyPair.GenerateKeys()!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("csms-keypins");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public async Task StopThem()
        {

            if (controller is not null)
                await controller.DisposeAsync();

            if (csms is not null)
                await csms.DisposeAsync();

            controller  = null;
            csms        = null;

            TestControllers.Remove(directory);

        }

        #endregion


        #region (private static) ACertificate()

        /// <summary>
        /// A self-signed certificate for 127.0.0.1, which no CA vouches for.
        /// </summary>
        private static X509Certificate2 ACertificate()
        {

            using var key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request    = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
            var names      = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(System.Net.IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());

            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            // Exported and read again: SslStream on Windows takes no ephemeral key.
            return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null);

        }

        #endregion

        #region (private) AControllerThatLearnsTheKeyOfItsCSMS(ProbationSeconds = null)

        /// <summary>
        /// The CSMS, and the controller below it: set to dial it over wss:// with a password, to trust its
        /// key on first use, and to believe the pin sets this test's signer signs - then started.
        /// </summary>
        private async Task AControllerThatLearnsTheKeyOfItsCSMS(Int64? ProbationSeconds = null)
        {

            csms = new CSMS(ACertificate());

            controller = await TestPorts.StartedOnFreshPorts(() => {

                             var made = TestControllers.New(
                                            directory,
                                            new JObject(
                                                new JProperty("nts",  new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                                new JProperty("ocpp", new JObject(new JProperty("nodeId", "lc002")))
                                            )
                                        );

                             Assert.That(made.CSMSLogin.TrySetPassword("lc002", "a-password-of-the-line-to-the-csms", out var loginError), Is.True, loginError);

                             Assert.That(made.CSMSPins.TrySetTrust(new ServerKeyPinSettings(null, "Key", ProbationSeconds), out var trustError), Is.True, trustError);

                             Assert.That(made.CSMSPins.TryAddPinSetSigner(Convert.ToBase64String(signer.PublicKeyBytes), "The CSMS", out var signerError), Is.True, signerError);

                             Assert.That(made.TryUpdateCSMSConfiguration(new JObject(
                                                                             new JProperty("enabled",                true),
                                                                             new JProperty("url",                    $"wss://127.0.0.1:{csms.Port}/lc002"),
                                                                             new JProperty("securityProfile",        2),
                                                                             new JProperty("reconnectInitialDelay",  1),
                                                                             new JProperty("reconnectMaxDelay",      2)
                                                                         ), out var csmsError),
                                         Is.True, csmsError);

                             return made;

                         });

            await Until(() => csms.Connections > 0 && controller.CSMSConnected, "The controller got through to its CSMS", () => controller.CSMSLastProblem ?? "");

        }

        #endregion

        #region (private static) Until(Condition, What, Said)

        private static async Task Until(Func<Boolean> Condition, String What, Func<String>? Said = null)
        {

            var giveUp = DateTimeOffset.UtcNow + Patience;

            while (!Condition())
            {

                if (DateTimeOffset.UtcNow > giveUp)
                    Assert.Fail($"{What}: not within {Patience.TotalSeconds:0} s. {Said?.Invoke()}");

                await Task.Delay(50);

            }

        }

        #endregion

        #region (private) PinSet(Pins, Version)

        /// <summary>
        /// A SetVariables of the line's TLSClientController - its key pins and their version - signed by
        /// this test's signer for the controller, as the CSMS sends it, with the message id it goes under.
        /// </summary>
        private (JObject Payload, String Id) PinSet(String Pins, UInt64 Version)
        {

            var id       = Guid.NewGuid().ToString();

            var request  = new OCPPv2_1.CSMS.SetVariablesRequest(
                               SourceRouting.To(NetworkingNode_Id.Parse("lc002")),
                               [
                                   new OCPPv2_1.SetVariableData(new OCPPv2_1.Component("TLSClientController", "csms"), new OCPPv2_1.Variable("ServerKeyPins"),        Pins),
                                   new OCPPv2_1.SetVariableData(new OCPPv2_1.Component("TLSClientController", "csms"), new OCPPv2_1.Variable("ServerKeyPinsVersion"), Version.ToString())
                               ],
                               RequestId: Request_Id.Parse(id)
                           );

            Assert.That(new SignaturePolicy().BindTo(NetworkingNode_Id.CSMS, OCPPv2_1.Version.WebSocketSubProtocolId).
                            SignRequestMessage(request, request.ToJSON(true), out var notSigned,
                                               signer.ToSignInfo1("CSMS", I18NString.Create("pin set"), DateTimeOffset.UtcNow)),
                        Is.True, notSigned);

            return (request.ToJSON(false), id);

        }

        #endregion

        #region (private static) GetVariables(Component, Variable, Instance = null)

        private static JObject GetVariables(String Component, String Variable, String? Instance = null)

            => new (new JProperty("getVariableData", new JArray(
                       new JObject(
                           new JProperty("component", Instance is null
                                                          ? new JObject(new JProperty("name", Component))
                                                          : new JObject(new JProperty("name", Component), new JProperty("instance", Instance))),
                           new JProperty("variable",  new JObject(new JProperty("name", Variable)))
                       )
                   )));

        #endregion


        #region TheKeyOfTheCSMSIsLearnedOnFirstUse_OnProbation()

        /// <summary>
        /// A CSMS no CA vouches for is believed by the key pin of the key it showed first, which is
        /// written down as learned and on probation, and told to the CSMS in a security event.
        /// </summary>
        [Test]
        public async Task TheKeyOfTheCSMSIsLearnedOnFirstUse_OnProbation()
        {

            await AControllerThatLearnsTheKeyOfItsCSMS();

            var pin = KeyPin.FromCertificate(csms!.Certificate);

            await Until(() => csms.SecurityEvents("KeyPinLearned").Length > 0, "The CSMS was told of the key pin learned");

            var written = controller!.CSMSPins.ToJSON();

            Assert.Multiple(() => {
                Assert.That(written.Value<String>("serverKeyPinState"),    Is.EqualTo("Learned"));
                Assert.That(written.Value<String>("serverKeyPinLearned"),  Is.EqualTo(pin.ToHex()));
                Assert.That(File.Exists(controller.CSMSPins.Path),         Is.True, "nothing was written down");
                Assert.That(controller.CSMSKeyPinning!.TrustOf("csms")?.OnProbation, Is.True);
                Assert.That(csms.SecurityEvents("KeyPinLearned").Single().Value<String>("techInfo"), Does.Contain(pin.ToString()));
            });

        }

        #endregion

        #region OnProbation_NothingIsCarriedOutOrSentOn_ButThePinSet()

        /// <summary>
        /// While the line is on probation, what its CSMS asks of this controller is refused with a
        /// SecurityError, and what it sends for a station behind it is not sent on - but what it reads of
        /// the line's TLSClientController is answered, so that it can find the key pin to confirm.
        /// </summary>
        [Test]
        public async Task OnProbation_NothingIsCarriedOutOrSentOn_ButThePinSet()
        {

            await AControllerThatLearnsTheKeyOfItsCSMS();

            var ofTheController  = await csms!.Ask("GetVariables", GetVariables("DNSClientController", "Servers"));
            var forAStation      = await csms.  Ask("GetVariables", GetVariables("DNSClientController", "Servers"), Destination: "cs001");
            var ofTheLine        = await csms.  Ask("GetVariables", GetVariables("TLSClientController", "LearnedKeyPin", "csms"));

            Assert.Multiple(() => {

                Assert.That(ofTheController[0].Value<Int32>(),   Is.EqualTo(4), $"a request was carried out on probation: {ofTheController}");
                Assert.That(ofTheController[2].Value<String>(),  Is.EqualTo("SecurityError"));
                Assert.That(ofTheController[3].Value<String>(),  Does.Contain("on probation"));

                Assert.That(forAStation[0].Value<Int32>(),       Is.EqualTo(4), $"a request was sent on to a station on probation: {forAStation}");
                Assert.That(forAStation.ToString(),              Does.Contain("on probation"));

                Assert.That(ofTheLine[0].Value<Int32>(),         Is.EqualTo(3), $"the line's TLSClientController was not read on probation: {ofTheLine}");
                Assert.That(ofTheLine[2]?["getVariableResult"]?[0]?.Value<String>("attributeValue"),
                                                                 Is.EqualTo(KeyPin.FromCertificate(csms.Certificate).ToHex()));

            });

        }

        #endregion

        #region ASignedPinSetConfirmsTheKey_AndTheCSMSIsBelieved()

        /// <summary>
        /// A pin set with the key pin learned, signed by a signer the controller believes, confirms it: the
        /// probation ends, the CSMS is told so, and what it asks is carried out again. An unsigned one is
        /// refused, and changes nothing.
        /// </summary>
        [Test]
        public async Task ASignedPinSetConfirmsTheKey_AndTheCSMSIsBelieved()
        {

            await AControllerThatLearnsTheKeyOfItsCSMS();

            var pin       = KeyPin.FromCertificate(csms!.Certificate).ToHex();

            var unsigned  = await csms.Ask("SetVariables", new JObject(new JProperty("setVariableData", new JArray(
                                                               new JObject(new JProperty("component", new JObject(new JProperty("name", "TLSClientController"), new JProperty("instance", "csms"))),
                                                                           new JProperty("variable",  new JObject(new JProperty("name", "ServerKeyPins"))),
                                                                           new JProperty("attributeValue", pin)),
                                                               new JObject(new JProperty("component", new JObject(new JProperty("name", "TLSClientController"), new JProperty("instance", "csms"))),
                                                                           new JProperty("variable",  new JObject(new JProperty("name", "ServerKeyPinsVersion"))),
                                                                           new JProperty("attributeValue", "1"))))));

            var (payload, id) = PinSet(pin, 1);
            var confirmed = await csms.Ask("SetVariables", payload, id);

            await Until(() => csms.SecurityEvents("KeyPinConfirmed").Length > 0, "The CSMS was told the key pin is confirmed");

            var afterwards = await csms.Ask("GetVariables", GetVariables("DNSClientController", "Servers"));

            Assert.Multiple(() => {

                Assert.That(unsigned[2]?["setVariableResult"]?.Select(result => result.Value<String>("attributeStatus")), Is.All.EqualTo("Rejected"), unsigned.ToString());
                Assert.That(unsigned[2]?["setVariableResult"]?[0]?["attributeStatusInfo"]?.Value<String>("reasonCode"),   Is.EqualTo("Unsigned"));

                Assert.That(confirmed[0].Value<Int32>(), Is.EqualTo(3), confirmed.ToString());
                Assert.That(confirmed[2]?["setVariableResult"]?.Select(result => result.Value<String>("attributeStatus")), Is.All.EqualTo("Accepted"), confirmed.ToString());

                Assert.That(controller!.CSMSPins.ToJSON().Value<String>("serverKeyPinState"),  Is.EqualTo("Confirmed"));
                Assert.That(controller.CSMSKeyPinning!.TrustOf("csms")?.OnProbation,           Is.False);

                Assert.That(afterwards[0].Value<Int32>(), Is.EqualTo(3), $"a request was refused after the key was confirmed: {afterwards}");

            });

        }

        #endregion

        #region APinSetWithAnotherKey_RefusesTheCSMS()

        /// <summary>
        /// A signed pin set without the key pin learned: that CSMS is not the one its operator knows. The
        /// pin set is answered, the line is closed, and it is not believed when it is dialled again.
        /// </summary>
        [Test]
        public async Task APinSetWithAnotherKey_RefusesTheCSMS()
        {

            await AControllerThatLearnsTheKeyOfItsCSMS();

            using var other    = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var (payload, id)  = PinSet(KeyPin.FromKey(other).ToHex(), 1);
            var taken          = await csms!.Ask("SetVariables", payload, id);

            await Until(() => csms.SecurityEvents("KeyPinRejected").Length > 0, "The CSMS was told its key is rejected");
            await Until(() => !controller!.CSMSConnected && csms.Connections == 0, "The line was closed", () => controller!.CSMSLastProblem ?? "");

            Assert.Multiple(() => {
                Assert.That(taken[2]?["setVariableResult"]?.Select(result => result.Value<String>("attributeStatus")), Is.All.EqualTo("Accepted"), taken.ToString());
                Assert.That(controller!.CSMSPins.ToJSON().Value<String>("serverKeyPinState"), Is.EqualTo("Configured"));
                Assert.That(controller.CSMSPins.ToJSON()["serverKeyPins"]?.Select(pin => pin.Value<String>()), Is.EqualTo(new[] { KeyPin.FromKey(other).ToHex() }));
            });

        }

        #endregion

    }

}
