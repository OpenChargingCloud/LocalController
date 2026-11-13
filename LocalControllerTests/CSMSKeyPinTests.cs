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

            /// <summary>
            /// What the TLS handshakes of its connections came to.
            /// </summary>
            public ConcurrentQueue<TLSSession?> TLSSessions { get; } = new();

            public CSMS(X509Certificate2  Certificate,
                        RawPublicKeyMode  RawPublicKeys = RawPublicKeyMode.Off)
            {

                this.Certificate = Certificate;

                Server = new WebSocketServer(
                             HTTPPort:                   IPPort.Zero,
                             RequireAuthentication:      false,
                             ServerCertificateSelector:  (tcpServer, tcpClient) => Certificate,
                             SecWebSocketProtocols:      [ "ocpp2.1" ],
                             DisableWebSocketPings:      true,
                             AutoStart:                  true
                         ) {
                             // Whether this CSMS shows its key raw, without its certificate (RFC 7250).
                             RawPublicKeys = RawPublicKeys
                         };

                Server.OnNewWebSocketConnection += (timestamp, server, connection, eventTrackingId, sharedSubprotocols, selectedSubprotocol, cancellationToken) => {
                    TLSSessions.Enqueue(connection.TLSSession);
                    return Task.CompletedTask;
                };

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
            /// <summary>
            /// What this CSMS was sent, in its order: for a test that has to say what did not come.
            /// </summary>
            public String Heard()
                => String.Join(", ", received.Select(message => message[0].Value<Int32>() == 2
                                                                    ? $"{message[2]}{(message[2].Value<String>() == "SecurityEventNotification" ? $"({message[3]?.Value<String>("type")})" : "")}"
                                                                    : $"[{message[0]}]"));

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
        /// <param name="ProbationSeconds">How long a learned key pin is on probation.</param>
        /// <param name="RawPublicKeys">Whether the line offers raw public keys (RFC 7250): nothing, "Off", "Prefer" or "Require".</param>
        /// <param name="CSMSShowsItsKeyRaw">Whether the CSMS shows its key raw.</param>
        /// <param name="GetsThrough">Whether the controller is waited for to get through.</param>
        private async Task AControllerThatLearnsTheKeyOfItsCSMS(Int64?            ProbationSeconds     = null,
                                                                String?           RawPublicKeys        = null,
                                                                RawPublicKeyMode  CSMSShowsItsKeyRaw   = RawPublicKeyMode.Off,
                                                                Boolean           GetsThrough          = true)
        {

            csms = new CSMS(ACertificate(), CSMSShowsItsKeyRaw);

            controller = await TestPorts.StartedOnFreshPorts(() => {

                             var made = TestControllers.New(
                                            directory,
                                            new JObject(
                                                new JProperty("nts",  new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                                new JProperty("ocpp", new JObject(new JProperty("nodeId", "lc002")))
                                            )
                                        );

                             Assert.That(made.CSMSLogin.TrySetPassword("lc002", "a-password-of-the-line-to-the-csms", out var loginError), Is.True, loginError);

                             Assert.That(made.CSMSPins.TrySetTrust(new ServerKeyPinSettings(RawPublicKeys, "Key", ProbationSeconds), out var trustError), Is.True, trustError);

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

            if (GetsThrough)
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

            => Signed(("ServerKeyPins", Pins), ("ServerKeyPinsVersion", Version.ToString()));

        /// <summary>
        /// A SetVariables of the line's TLSClientController, signed by this test's signer for the
        /// controller, as the CSMS sends it, with the message id it goes under.
        /// </summary>
        private (JObject Payload, String Id) Signed(params (String Variable, String Value)[] Variables)
        {

            var id       = Guid.NewGuid().ToString();

            var request  = new OCPPv2_1.CSMS.SetVariablesRequest(
                               SourceRouting.To(NetworkingNode_Id.Parse("lc002")),
                               Variables.Select(one => new OCPPv2_1.SetVariableData(
                                                           new OCPPv2_1.Component("TLSClientController", "csms"),
                                                           new OCPPv2_1.Variable(one.Variable),
                                                           one.Value
                                                       )),
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

        #region TheKeyOfTheCSMS_ComesRaw_IsLearned_AndConfirmedByASignedPinSet(RawPublicKeys)

        /// <summary>
        /// A line that prefers or requires raw public keys (RFC 7250), and a CSMS that shows its key raw: no
        /// certificate goes over the line. The key is learned on first use and put on probation as the key
        /// of a certificate is, the CSMS is told, the variable says what was asked, and a signed pin set
        /// confirms the key - the key pin rules do not ask how the key came.
        /// </summary>
        [Test]
        public async Task TheKeyOfTheCSMS_ComesRaw_IsLearned_AndConfirmedByASignedPinSet([Values("Prefer", "Require")] String RawPublicKeys)
        {

            await AControllerThatLearnsTheKeyOfItsCSMS(RawPublicKeys: RawPublicKeys, CSMSShowsItsKeyRaw: RawPublicKeyMode.Prefer);

            var pin = KeyPin.FromCertificate(csms!.Certificate);

            await Until(() => csms.SecurityEvents("KeyPinLearned").Length > 0, "The CSMS was told of the key pin learned");

            var trust     = controller!.CSMSKeyPinning!.TrustOf("csms");
            var session   = csms.TLSSessions.First();
            var asked     = await csms.Ask("GetVariables", GetVariables("TLSClientController", "RawPublicKeys", "csms"));

            Assert.Multiple(() => {

                // What the controller was shown: a key, and no certificate.
                Assert.That(trust?.LastValidation?.Outcome,          Is.EqualTo(KeyPinOutcome.Learned));
                Assert.That(trust?.LastValidation?.CertificateType,  Is.EqualTo(TLSCertificateType.RawPublicKey));
                Assert.That(trust?.Learned,                          Is.EqualTo(pin));
                Assert.That(trust?.OnProbation,                      Is.True);

                // What the CSMS showed.
                Assert.That(session?.Provider,                       Is.EqualTo(TLSProvider.BouncyCastle));
                Assert.That(session?.LocalCertificateType,           Is.EqualTo(TLSCertificateType.RawPublicKey));

                // The key pin rules as for a key in a certificate.
                Assert.That(controller.CSMSPins.ToJSON().Value<String>("serverKeyPinState"),    Is.EqualTo("Learned"));
                Assert.That(controller.CSMSPins.ToJSON().Value<String>("serverKeyPinLearned"),  Is.EqualTo(pin.ToHex()));
                Assert.That(csms.SecurityEvents("KeyPinLearned").Single().Value<String>("techInfo"), Does.Contain(pin.ToString()));

                Assert.That(asked[2]?["getVariableResult"]?[0]?.Value<String>("attributeValue"),  Is.EqualTo(RawPublicKeys), asked.ToString());

            });

            var (payload, id) = PinSet(pin.ToHex(), 1);
            var confirmed     = await csms.Ask("SetVariables", payload, id);

            await Until(() => csms.SecurityEvents("KeyPinConfirmed").Length > 0, "The CSMS was told the key pin is confirmed");

            var afterwards    = await csms.Ask("GetVariables", GetVariables("DNSClientController", "Servers"));

            Assert.Multiple(() => {
                Assert.That(confirmed[2]?["setVariableResult"]?.Select(result => result.Value<String>("attributeStatus")), Is.All.EqualTo("Accepted"), confirmed.ToString());
                Assert.That(controller.CSMSPins.ToJSON().Value<String>("serverKeyPinState"),  Is.EqualTo("Confirmed"));
                Assert.That(trust?.OnProbation,                                               Is.False);
                Assert.That(afterwards[0].Value<Int32>(), Is.EqualTo(3), $"a request was refused after the key was confirmed: {afterwards}");
            });

        }

        #endregion

        #region TheCSMSMayAskForRawPublicKeysAlone()

        /// <summary>
        /// "Require" is a value of the variable RawPublicKeys as "Prefer" and "Off" are: a CSMS whose key
        /// was confirmed asks for it in a signed SetVariables, it is taken and written down - the line
        /// offers raw public keys, so it is not one that could not be dialled any more.
        /// </summary>
        [Test]
        public async Task TheCSMSMayAskForRawPublicKeysAlone()
        {

            await AControllerThatLearnsTheKeyOfItsCSMS(CSMSShowsItsKeyRaw: RawPublicKeyMode.Prefer);

            var (pinSet, pinSetId)  = PinSet(KeyPin.FromCertificate(csms!.Certificate).ToHex(), 1);
            await csms.Ask("SetVariables", pinSet, pinSetId);

            await Until(() => csms.SecurityEvents("KeyPinConfirmed").Length > 0, "The CSMS was told the key pin is confirmed");

            var before          = await csms.Ask("GetVariables", GetVariables("TLSClientController", "RawPublicKeys", "csms"));

            var (payload, id)   = Signed(("RawPublicKeys", "Require"));
            var taken           = await csms.Ask("SetVariables", payload, id);

            var afterwards      = await csms.Ask("GetVariables", GetVariables("TLSClientController", "RawPublicKeys", "csms"));

            Assert.Multiple(() => {
                Assert.That(before    [2]?["getVariableResult"]?[0]?.Value<String>("attributeValue"),   Is.EqualTo("Off"), before.ToString());
                Assert.That(taken     [2]?["setVariableResult"]?.Select(result => result.Value<String>("attributeStatus")), Is.All.EqualTo("Accepted"), taken.ToString());
                Assert.That(afterwards[2]?["getVariableResult"]?[0]?.Value<String>("attributeValue"),   Is.EqualTo("Require"), afterwards.ToString());
                Assert.That(controller!.CSMSKeyPinning!.TrustOf("csms")?.Policy()?.RawPublicKeys,       Is.EqualTo(RawPublicKeyMode.Require));
            });

        }

        #endregion

        #region ALineThatPrefersRawPublicKeys_TakesTheCertificateOfACSMSThatHasNone_ByItsKeyPin()

        /// <summary>
        /// A CSMS that knows no raw public keys shows its certificate, and a line that only prefers them
        /// believes it by the key pin of the key in it, as before.
        /// </summary>
        [Test]
        public async Task ALineThatPrefersRawPublicKeys_TakesTheCertificateOfACSMSThatHasNone_ByItsKeyPin()
        {

            await AControllerThatLearnsTheKeyOfItsCSMS(RawPublicKeys: "Prefer");

            var pin   = KeyPin.FromCertificate(csms!.Certificate);
            var trust = controller!.CSMSKeyPinning!.TrustOf("csms");

            await Until(() => csms.SecurityEvents("KeyPinLearned").Length > 0, "The CSMS was told of the key pin learned");

            Assert.Multiple(() => {
                Assert.That(trust?.LastValidation?.Outcome,          Is.EqualTo(KeyPinOutcome.Learned));
                Assert.That(trust?.LastValidation?.CertificateType,  Is.EqualTo(TLSCertificateType.X509));
                Assert.That(trust?.Learned,                          Is.EqualTo(pin));
                Assert.That(csms.TLSSessions.First()?.Provider,      Is.EqualTo(TLSProvider.SslStream));
                Assert.That(controller.CSMSPins.ToJSON().Value<String>("serverKeyPinState"), Is.EqualTo("Learned"));
            });

        }

        #endregion

        #region ALineThatRequiresRawPublicKeys_DoesNotBelieveACSMSThatShowsACertificate()

        /// <summary>
        /// A line that requires raw public keys is dialled - and a CSMS that shows a certificate is turned
        /// away, whatever key is in it: nothing is learned, and the controller says why it has no line.
        /// </summary>
        [Test]
        public async Task ALineThatRequiresRawPublicKeys_DoesNotBelieveACSMSThatShowsACertificate()
        {

            await AControllerThatLearnsTheKeyOfItsCSMS(RawPublicKeys: "Require", GetsThrough: false);

            await Until(() => controller!.CSMSKeyPinning?.TrustOf("csms")?.LastValidation is not null,
                        "The controller dialled its CSMS",
                        () => controller!.CSMSLastProblem ?? "");

            var trust = controller!.CSMSKeyPinning!.TrustOf("csms");

            await Until(() => controller.CSMSLastProblem is not null, "The controller says why it has no line");

            Assert.Multiple(() => {
                Assert.That(trust?.LastValidation?.Outcome,          Is.EqualTo(KeyPinOutcome.Refused));
                Assert.That(trust?.LastValidation?.CertificateType,  Is.EqualTo(TLSCertificateType.X509));
                Assert.That(trust?.LastValidation?.Reason,           Does.Contain("requires a raw public key"));
                Assert.That(trust?.Learned,                          Is.Null);
                Assert.That(controller.CSMSConnected,                Is.False);
                Assert.That(csms!.Connections,                       Is.Zero);
                Assert.That(controller.CSMSPins.ToJSON().Value<String>("serverKeyPinState"), Is.Not.EqualTo("Learned"));
            });

        }

        #endregion

        #region APinSetWithAnotherKey_RefusesTheCSMS_WhoseKeyCameRaw()

        /// <summary>
        /// A signed pin set without the key pin learned refuses a CSMS whose key came raw as it refuses one
        /// whose key came in a certificate: the line is closed, and dialled again the raw key is turned away
        /// by the pins that were signed.
        /// </summary>
        [Test]
        public async Task APinSetWithAnotherKey_RefusesTheCSMS_WhoseKeyCameRaw()
        {

            await AControllerThatLearnsTheKeyOfItsCSMS(RawPublicKeys: "Require", CSMSShowsItsKeyRaw: RawPublicKeyMode.Prefer);

            var pin            = KeyPin.FromCertificate(csms!.Certificate);

            // The controller tells its events in their order, each once the one before was taken: the
            // pin set is sent once the first one is through, or the line is closed over what waits behind it.
            await Until(() => csms.SecurityEvents("KeyPinLearned").Length > 0, "The CSMS was told of the key pin learned");

            using var other    = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var (payload, id)  = PinSet(KeyPin.FromKey(other).ToHex(), 1);
            var taken          = await csms.Ask("SetVariables", payload, id);

            // Not waited for here: the security event KeyPinRejected. The line is closed once the answer
            // to the pin set is out, and the event waits in the controller's queue beside it - which of
            // the two is first is not said (see APinSetWithAnotherKey_RefusesTheCSMS, which asks for it).
            await Until(() => !controller!.CSMSConnected && csms.Connections == 0, "The line was closed",
                        () => $"heard: {csms.Heard()}; said: {controller!.CSMSLastProblem}");

            // Dialled again, with its back-off: the key that was learned is none of those pinned now.
            await Until(() => controller!.CSMSKeyPinning!.TrustOf("csms")?.LastValidation is { Outcome: KeyPinOutcome.Refused },
                        "The CSMS was dialled again and turned away",
                        () => controller!.CSMSLastProblem ?? "");

            var refused = controller!.CSMSKeyPinning!.TrustOf("csms")!.LastValidation!;

            Assert.Multiple(() => {
                Assert.That(taken[2]?["setVariableResult"]?.Select(result => result.Value<String>("attributeStatus")), Is.All.EqualTo("Accepted"), taken.ToString());
                Assert.That(refused.CertificateType,   Is.EqualTo(TLSCertificateType.RawPublicKey));
                Assert.That(refused.Pin,               Is.EqualTo(pin));
                Assert.That(controller.CSMSConnected,  Is.False);
                Assert.That(controller.CSMSPins.ToJSON()["serverKeyPins"]?.Select(one => one.Value<String>()), Is.EqualTo(new[] { KeyPin.FromKey(other).ToHex() }));
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
