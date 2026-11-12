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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// What only the CSMS asks of this local controller - InstallSSHKey, DeleteSSHKey, GetInstalledSSHKeys,
    /// SetVariables, Reset and the like - a charging station on the station port cannot: refused at the
    /// door with a SecurityError, over a WebSocket as over SSH, whether it sends as itself or calls itself
    /// the CSMS, which the frame of the overlay network lets it write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A station that could install an SSH key for another station's identity would sign in as that
    /// station next - and one that could delete a key would lock it out.
    /// </para>
    /// <para>
    /// A plain WebSocket client and a plain SSH message client rather than charging stations of the OCPP
    /// library: what is tested is what a station can write, and that is any frame at all.
    /// </para>
    /// </remarks>
    public class StationRequestGateTests : ALocalControllerTests
    {

        #region Data

        private const String ThePassword = "a-password-long-enough-for-ocpp";
        private const String Subsystem   = "ocpp@open.charging.cloud";

        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

        private readonly ISshHostKey cs001Key     = SshHostKey.GenerateEd25519();
        private readonly ISshHostKey cs002Key     = SshHostKey.GenerateEd25519();
        private readonly ISshHostKey strangerKey  = SshHostKey.GenerateEd25519();

        private Int32 lastId;

        #endregion

        #region (override) Configuration

        /// <summary>
        /// A controller whose station server is switched on, with passwords, and OCPP over SSH with it.
        /// </summary>
        protected override JObject Configuration
        {
            get
            {

                var configuration = TestControllers.Offline;

                configuration["ocppServer"] = new JObject(
                                                  new JProperty("enabled",           true),
                                                  new JProperty("address",           "127.0.0.1"),
                                                  new JProperty("port",              TestPorts.Free()),
                                                  new JProperty("securityProfiles",  new JArray(1)),
                                                  new JProperty("subprotocols",      new JArray("ocpp2.1")),
                                                  new JProperty("ssh",               new JObject(
                                                      new JProperty("enabled",       true)
                                                  ))
                                              );

                configuration["ssh"]        = new JObject(
                                                  new JProperty("enabled",           true),
                                                  new JProperty("port",              TestPorts.Free())
                                              );

                return configuration;

            }
        }

        #endregion

        #region (private) helpers

        private static String LineOf(ISshHostKey Key)
            => SshPublicKey.FromHostKey(Key).ToAuthorizedKeyLine();

        private static String FingerprintOf(ISshHostKey Key)
            => SshFingerprint.Sha256(Key.PublicKeyBlob);

        private void GiveAKey(String Id, ISshHostKey Key)
        {
            Assert.That(Controller.TryAddStationSSHKey(Id, LineOf(Key), "test", null, null, "tests", out _, out var error, out _),
                        Is.True, error);
        }

        /// <summary>
        /// The fingerprints of the SSH keys the given station signs in with.
        /// </summary>
        private String[] KeysOf(String Id)

            => [.. Controller.StationLogins.Logins.
                       Where     (login => login.Id == Id).
                       SelectMany(login => login.Keys.Select(key => key.Fingerprint))];

        /// <summary>
        /// A request in the frame of the overlay network, addressed to this controller itself and sent as
        /// the given node.
        /// </summary>
        private (String Id, String Text) Frame(String   From,
                                               String   Action,
                                               JObject  Payload)
        {

            var id = $"station-{Interlocked.Increment(ref lastId)}";

            return (id, new JArray(2, Controller.Node.Id.ToString(), From, id, Action, Payload).ToString(Formatting.None));

        }

        private static JObject InstallFor(String Station, ISshHostKey Key)

            => new (new JProperty("networkingNodeId",  Station),
                    new JProperty("purpose",           "OCPP"),
                    new JProperty("publicKey",         LineOf(Key)));

        private static JObject DeleteOf(String Station, ISshHostKey Key)

            => new (new JProperty("networkingNodeId",  Station),
                    new JProperty("keyFingerprint",    FingerprintOf(Key)));

        private static void IsRefused(JArray? Answer, String What)
        {

            Assert.That(Answer,                      Is.Not.Null,                $"{What}: no answer at all");
            Assert.That(Answer![0].Value<Int32>(),   Is.EqualTo(4),              $"{What} was not refused: {Answer.ToString(Formatting.None)}");
            Assert.That(Answer[2].Value<String>(),   Is.EqualTo("SecurityError"), What);

        }

        #endregion

        #region (private) A station over a WebSocket

        /// <summary>
        /// A station on the station port over ws://, signed in with its password.
        /// </summary>
        private async Task<ClientWebSocket> ConnectOverWebSocket(String Id)
        {

            Assert.That(Controller.StationLogins.TrySetPassword(Id, ThePassword, null, null, out _, out var error), Is.True, error);

            var client = new ClientWebSocket();

            client.Options.AddSubProtocol("ocpp2.1");
            client.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Id}:{ThePassword}")));

            using var timeout = new CancellationTokenSource(Guard);

            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{Controller.OCPPServerSettings.TCPPort}/{Id}"), timeout.Token);

            return client;

        }

        /// <summary>
        /// Send the given frame and wait for no answer: one sent as "the CSMS" is answered to the CSMS, which
        /// this controller has not.
        /// </summary>
        private static async Task Tell(ClientWebSocket Client, (String Id, String Text) Frame)
        {

            using var timeout = new CancellationTokenSource(Guard);

            await Client.SendAsync(Encoding.UTF8.GetBytes(Frame.Text), WebSocketMessageType.Text, true, timeout.Token);

        }

        /// <summary>
        /// Send the given frame and read the answer to it - skipping whatever else the controller sends.
        /// </summary>
        private static async Task<JArray?> Ask(ClientWebSocket Client, (String Id, String Text) Frame)
        {

            using var timeout = new CancellationTokenSource(Guard);

            await Client.SendAsync(Encoding.UTF8.GetBytes(Frame.Text), WebSocketMessageType.Text, true, timeout.Token);

            var buffer = new Byte[64 * 1024];

            try
            {
                while (true)
                {

                    using var message = new MemoryStream();

                    WebSocketReceiveResult result;

                    do
                    {
                        result = await Client.ReceiveAsync(buffer, timeout.Token);
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;

                    var answer = JArray.Parse(Encoding.UTF8.GetString(message.ToArray()));

                    if (answer[0].Value<Int32>() is 3 or 4 && answer[1].Value<String>() == Frame.Id)
                        return answer;

                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }

        }

        #endregion

        #region (private) A station over SSH

        /// <summary>
        /// A station on the SSH port, signed in with its key: the connection, and how to send a frame and
        /// wait for the answer to it - or, for a frame sent as "the CSMS", for no answer.
        /// </summary>
        private async Task<(SshMessageConnection Connection, Func<(String Id, String Text), Task<JArray?>> Ask)> ConnectOverSSH(String Id, ISshHostKey Key)
        {

            GiveAKey(Id, Key);

            var client = new SshMessageClient(new SshMessageClientOptions {
                                                  Host          = "127.0.0.1",
                                                  Port          = Controller.SSHPort!.Value.ToUInt16(),
                                                  Ssh           = new SshClientOptions {
                                                                      Username       = Id,
                                                                      VerifyHostKey  = blob => Controller.SSHHostKey == $"ssh-ed25519 {SshFingerprint.Sha256(blob)}",
                                                                      Credentials    = [ Key ]
                                                                  },
                                                  Subsystem     = Subsystem,
                                                  Subprotocols  = [ "ocpp2.1" ]
                                              });

            var answers     = new System.Collections.Concurrent.ConcurrentDictionary<String, TaskCompletionSource<JArray>>();

            client.OnTextMessageReceived += (timestamp, sender, conn, text, ct) => {

                var answer = JArray.Parse(text);

                if (answer[0].Value<Int32>() is 3 or 4 && answers.TryGetValue(answer[1].Value<String>() ?? "", out var waiting))
                    waiting.TrySetResult(answer);

                return Task.CompletedTask;

            };

            var connection  = await client.ConnectAsync().WaitAsync(Guard);

            async Task<JArray?> Ask((String Id, String Text) Frame)
            {

                var waiting = answers.GetOrAdd(Frame.Id, _ => new TaskCompletionSource<JArray>(TaskCreationOptions.RunContinuationsAsynchronously));

                await connection.SendTextMessage(Frame.Text);

                return await Task.WhenAny(waiting.Task, Task.Delay(Guard)) == waiting.Task
                           ? await waiting.Task
                           : null;

            }

            return (connection, Ask);

        }

        #endregion


        #region AStation_CannotInstallAKeyForAnother_OverAWebSocket()

        /// <summary>
        /// A station signed in with its password asks the controller to install its own key for another
        /// station - as "the CSMS" and as itself. Refused, and the other station has no key.
        /// </summary>
        /// <remarks>
        /// The answer to the frame sent as "the CSMS" would go to the CSMS: what is looked at is whether the
        /// key was installed. The station's own frame comes after it on the same connection, so once that is
        /// answered, the other has been dealt with too.
        /// </remarks>
        [Test]
        public async Task AStation_CannotInstallAKeyForAnother_OverAWebSocket()
        {

            using var cs001  = await ConnectOverWebSocket("cs001");

            await Tell(cs001, Frame("CSMS",  "InstallSSHKey", InstallFor("cs002", strangerKey)));

            var asItself     = await Ask(cs001, Frame("cs001", "InstallSSHKey", InstallFor("cs002", cs001Key)));

            await Task.Delay(TimeSpan.FromMilliseconds(300));

            Assert.Multiple(() => {
                IsRefused(asItself, "InstallSSHKey as itself");
                Assert.That(KeysOf("cs002"), Is.Empty, "a station installed an SSH key for another one");
            });

        }

        #endregion

        #region AStation_CannotInstallAKeyForAnother_OverSSH()

        /// <summary>
        /// The same over SSH, by a station signed in with its own key.
        /// </summary>
        [Test]
        public async Task AStation_CannotInstallAKeyForAnother_OverSSH()
        {

            var (connection, ask) = await ConnectOverSSH("cs001", cs001Key);

            try
            {

                _            = ask(Frame("CSMS",  "InstallSSHKey", InstallFor("cs002", strangerKey)));

                var asItself = await ask(Frame("cs001", "InstallSSHKey", InstallFor("cs002", cs001Key)));

                await Task.Delay(TimeSpan.FromMilliseconds(300));

                Assert.Multiple(() => {
                    IsRefused(asItself, "InstallSSHKey as itself");
                    Assert.That(KeysOf("cs002"), Is.Empty, "a station installed an SSH key for another one");
                });

            }
            finally
            {
                await connection.Close(org.GraphDefined.Vanaheimr.Hermod.Messaging.MessageCloseCode.NormalClosure, "done");
            }

        }

        #endregion

        #region AStation_CannotDeleteOrListTheKeysOfAnother()

        /// <summary>
        /// Nor can it take another station's key away, which would lock it out, or read which keys are
        /// installed - and the key stays.
        /// </summary>
        [Test]
        public async Task AStation_CannotDeleteOrListTheKeysOfAnother()
        {

            GiveAKey("cs002", cs002Key);

            using var cs001  = await ConnectOverWebSocket("cs001");

            await Tell(cs001, Frame("CSMS", "DeleteSSHKey", DeleteOf("cs002", cs002Key)));

            var deleted      = await Ask(cs001, Frame("cs001", "DeleteSSHKey",        DeleteOf("cs002", cs002Key)));
            var listed       = await Ask(cs001, Frame("cs001", "GetInstalledSSHKeys", new JObject(new JProperty("networkingNodeId", "cs002"))));

            Assert.Multiple(() => {
                IsRefused(deleted, "DeleteSSHKey");
                IsRefused(listed,  "GetInstalledSSHKeys");
                Assert.That(KeysOf("cs002"), Is.EqualTo(new[] { FingerprintOf(cs002Key) }), "a station took another station's SSH key away");
            });

        }

        #endregion

        #region AStation_CannotAskTheControllerWhatOnlyTheCSMSAsks(Action)

        /// <summary>
        /// The rest of what the CSMS asks of a node it manages - its Device Model, a reset, its files and its
        /// signature policy - is refused alike: one gate for all of it.
        /// </summary>
        [TestCase("SetVariables",        """{"setVariableData":[{"component":{"name":"EVQICtrlr","instance":"TLSServer"},"variable":{"name":"ActiveKeyPin"},"attributeValue":"00"}]}""")]
        [TestCase("GetVariables",        """{"getVariableData":[{"component":{"name":"EVQICtrlr","instance":"TLSServer"},"variable":{"name":"KeyPins"}}]}""")]
        [TestCase("GetBaseReport",       """{"requestId":1,"reportBase":"FullInventory"}""")]
        [TestCase("Reset",               """{"type":"Immediate"}""")]
        [TestCase("ListDirectory",       """{"directoryPath":"/"}""")]
        [TestCase("AddSignaturePolicy",  """{"signaturePolicy":{}}""")]
        public async Task AStation_CannotAskTheControllerWhatOnlyTheCSMSAsks(String  Action,
                                                                            String  Payload)
        {

            using var cs001 = await ConnectOverWebSocket("cs001");

            IsRefused(await Ask(cs001, Frame("cs001", Action, JObject.Parse(Payload))), Action);

        }

        #endregion

        #region AStation_CannotSendAnotherStationARequest_OverSSH()

        /// <summary>
        /// A station over SSH addresses another station behind the controller - as "the CSMS" and as itself.
        /// The other station gets neither; the station is told about the one in its own name.
        /// </summary>
        [Test]
        public async Task AStation_CannotSendAnotherStationARequest_OverSSH()
        {

            using var cs002        = await ConnectOverWebSocket("cs002");
            var (connection, ask)  = await ConnectOverSSH("cs001", cs001Key);

            try
            {

                var reset    = new JObject(new JProperty("type", "Immediate"));
                var asCSMS   = (Id: "lateral-1", Text: new JArray(2, "cs002", "CSMS",  "lateral-1", "Reset", reset).ToString(Formatting.None));
                var asItself = (Id: "lateral-2", Text: new JArray(2, "cs002", "cs001", "lateral-2", "Reset", reset).ToString(Formatting.None));

                _            = ask(asCSMS);
                var refused  = await ask(asItself);

                var atCS002  = await Received(cs002, TimeSpan.FromMilliseconds(800));

                Assert.Multiple(() => {
                    Assert.That(refused?[0].Value<Int32>(),  Is.EqualTo(4), $"the station was not told: {refused?.ToString(Formatting.None)}");
                    Assert.That(atCS002,                     Is.Empty,      $"a station sent another one a Reset through the controller: {String.Join(" ", atCS002)}");
                });

            }
            finally
            {
                await connection.Close(org.GraphDefined.Vanaheimr.Hermod.Messaging.MessageCloseCode.NormalClosure, "done");
            }

        }

        /// <summary>
        /// What the station got within the given time.
        /// </summary>
        private static async Task<String[]> Received(ClientWebSocket Client, TimeSpan Within)
        {

            var got     = new List<String>();
            var buffer  = new Byte[64 * 1024];

            using var timeout = new CancellationTokenSource(Within);

            try
            {
                while (true)
                {
                    var result = await Client.ReceiveAsync(buffer, timeout.Token);
                    got.Add(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            }
            catch (OperationCanceledException)
            { }
            catch (WebSocketException)
            { }

            return [.. got];

        }

        #endregion

        #region TheCSMSOnlyActions_AreWhatACSMSAsks()

        /// <summary>
        /// The actions held back are those a CSMS asks of a node it manages - found by their request types,
        /// so this pins a few of each kind - and not those a station sends.
        /// </summary>
        [Test]
        public void TheCSMSOnlyActions_AreWhatACSMSAsks()
        {

            Assert.Multiple(() => {

                foreach (var action in new[] { "InstallSSHKey", "DeleteSSHKey", "GetInstalledSSHKeys", "SetVariables", "GetVariables", "GetBaseReport",
                                               "GetReport", "Reset", "UpdateFirmware", "GetLog", "InstallCertificate", "CertificateSigned", "SetNetworkProfile",
                                               "TriggerMessage", "ChangeAvailability", "RequestStartTransaction", "AddSignaturePolicy", "AddUserRole",
                                               "GetFile", "SendFile", "DeleteFile", "ListDirectory" })
                    Assert.That(LocalController.CSMSOnlyActions, Does.Contain(action), action);

                foreach (var action in new[] { "BootNotification", "Heartbeat", "StatusNotification", "TransactionEvent", "SecurityEventNotification",
                                               "DataTransfer", "BinaryDataTransfer", "SecureDataTransfer", "NotifyNetworkTopology" })
                    Assert.That(LocalController.CSMSOnlyActions, Does.Not.Contain(action), action);

            });

        }

        #endregion

        #region AStation_StillAsksTheControllerWhatAStationMayAsk()

        /// <summary>
        /// The gate holds back the CSMS's requests, and nothing else: a station's DataTransfer to the
        /// controller is answered, not refused.
        /// </summary>
        [Test]
        public async Task AStation_StillAsksTheControllerWhatAStationMayAsk()
        {

            using var cs001  = await ConnectOverWebSocket("cs001");

            var answer       = await Ask(cs001, Frame("cs001", "DataTransfer", new JObject(new JProperty("vendorId", "GraphDefined"))));

            Assert.That(answer, Is.Not.Null, "no answer to a station's DataTransfer");
            Assert.That(answer![0].Value<Int32>() == 4 ? answer[2].Value<String>() : null, Is.Not.EqualTo("SecurityError"), answer.ToString(Formatting.None));

        }

        #endregion

    }


    /// <summary>
    /// The CSMS asks the same over the controller's own line up to it, and is answered.
    /// </summary>
    public class CSMSRequestsThroughTheGateTests
    {

        #region Data

        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

        private const String ThePassword = "a-password-long-enough-for-ocpp";

        private readonly ISshHostKey cs002Key = SshHostKey.GenerateEd25519();

        private String                     directory   = default!;
        private KeyPinsOverOCPPTests.CSMS  csms        = default!;
        private LocalController            controller  = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task StartAControllerAndItsCSMS()
        {

            directory     = TestControllers.TemporaryDirectory("gate-csms");
            Directory.CreateDirectory(directory);

            var csmsPort  = TestPorts.Free();

            csms          = new KeyPinsOverOCPPTests.CSMS(csmsPort);

            controller    = await TestPorts.StartedOnFreshPorts(() => {

                                var lc = TestControllers.New(
                                             directory,
                                             new JObject(

                                                 new JProperty("nts",  new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                                 new JProperty("ocpp", new JObject(new JProperty("nodeId", "lc002"))),

                                                 new JProperty("ocppServer", new JObject(
                                                     new JProperty("enabled",           true),
                                                     new JProperty("address",           "127.0.0.1"),
                                                     new JProperty("port",              TestPorts.Free()),
                                                     new JProperty("securityProfiles",  new JArray(1))
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

                                Assert.That(lc.CSMSLogin.TrySetPassword("lc002", ThePassword, out var loginError), Is.True, loginError);

                                return lc;

                            });

            var giveUp = DateTimeOffset.UtcNow + Guard;

            while (DateTimeOffset.UtcNow < giveUp && !(controller.CSMSConnected && csms.Connections == 1))
                await Task.Delay(50);

            Assert.That(controller.CSMSConnected, Is.True, "the controller did not reach its CSMS");

        }

        [TearDown]
        public async Task StopIt()
        {

            if (controller is not null)
                await controller.DisposeAsync();

            if (csms is not null)
                await csms.DisposeAsync();

            TestControllers.Remove(directory);

        }

        #endregion


        #region TheCSMS_InstallsListsAndDeletesAKey_OverTheUplink()

        /// <summary>
        /// InstallSSHKey, GetInstalledSSHKeys and DeleteSSHKey of the CSMS, over the connection the controller
        /// dialled: each answered, and done.
        /// </summary>
        [Test]
        public async Task TheCSMS_InstallsListsAndDeletesAKey_OverTheUplink()
        {

            var fingerprint  = SshFingerprint.Sha256(cs002Key.PublicKeyBlob);

            var installed    = await csms.Ask("InstallSSHKey",       new JObject(new JProperty("networkingNodeId", "cs002"),
                                                                                 new JProperty("purpose",          "OCPP"),
                                                                                 new JProperty("publicKey",        SshPublicKey.FromHostKey(cs002Key).ToAuthorizedKeyLine())));

            var listed       = await csms.Ask("GetInstalledSSHKeys", new JObject(new JProperty("networkingNodeId", "cs002")));

            var deleted      = await csms.Ask("DeleteSSHKey",        new JObject(new JProperty("networkingNodeId", "cs002"),
                                                                                 new JProperty("keyFingerprint",   fingerprint)));

            Assert.Multiple(() => {

                Assert.That(installed[0].Value<Int32>(),             Is.EqualTo(3), installed.ToString(Formatting.None));
                Assert.That(installed[2]!.Value<String>("status"),   Is.EqualTo("Accepted"));

                Assert.That(listed[0].Value<Int32>(),                Is.EqualTo(3), listed.ToString(Formatting.None));
                Assert.That(listed[2]!["keys"]!.Select(key => key.Value<String>("keyFingerprint")), Is.EqualTo(new[] { fingerprint }));

                Assert.That(deleted[0].Value<Int32>(),               Is.EqualTo(3), deleted.ToString(Formatting.None));
                Assert.That(deleted[2]!.Value<String>("status"),     Is.EqualTo("Accepted"));

            });

        }

        #endregion

    }

}
