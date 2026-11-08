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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.LocalController.OCPP;

using LCConfig = cloud.charging.open.LocalController.Configuration;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Charging stations connecting over SSH: who is let in, to what, and who
    /// is signed out again.
    /// </summary>
    /// <remarks>
    /// A plain SSH message client rather than a charging station of the OCPP
    /// library, as over the WebSocket port: this is about the door - the key,
    /// the group, the subsystem and the HELLO - and a client that says nothing
    /// beyond it cannot pass a test by accident.
    /// </remarks>
    public class StationSSHTests : ALocalControllerTests
    {

        #region Data

        private const String Subsystem = "ocpp@open.charging.cloud";

        private readonly ISshHostKey cs001Key  = SshHostKey.GenerateEd25519();
        private readonly ISshHostKey cs002Key  = SshHostKey.GenerateEd25519();
        private readonly ISshHostKey strangerKey = SshHostKey.GenerateEd25519();

        #endregion

        #region (override) Configuration

        /// <summary>
        /// A controller whose station server and SSH server are switched on,
        /// and OCPP over SSH with them.
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
                                                  new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1")),
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

        private static String LineOf(ISshHostKey Key, String? Options = null)

            => (Options is not null ? Options + " " : "") +
               SshPublicKey.FromHostKey(Key).ToAuthorizedKeyLine();

        private static String FingerprintOf(ISshHostKey Key)

            => SshFingerprint.Sha256(Key.PublicKeyBlob);

        private void GiveAKey(String Id, ISshHostKey Key, String? Group = null)
        {

            Assert.That(Controller.TryAddStationSSHKey(Id, LineOf(Key), "test", Group, null, "tests", out _, out var error, out _),
                        Is.True, error);

        }

        private SshClientOptions SSHAs(String Id, ISshHostKey Key)

            => new () {
                   Username       = Id,
                   VerifyHostKey  = blob => Controller.SSHHostKey == $"ssh-ed25519 {SshFingerprint.Sha256(blob)}",
                   Credentials    = [ Key ]
               };

        /// <summary>
        /// A station connecting over SSH: the connection, or why it was not
        /// let in.
        /// </summary>
        private async Task<(SshMessageConnection? Connection, String? Why)> Connect(String     Id,
                                                                                  ISshHostKey Key,
                                                                                  params String[] Subprotocols)
        {

            var client = new SshMessageClient(new SshMessageClientOptions {
                                                  Host          = "127.0.0.1",
                                                  Port          = Controller.SSHPort!.Value.ToUInt16(),
                                                  Ssh           = SSHAs(Id, Key),
                                                  Subsystem     = Subsystem,
                                                  Subprotocols  = Subprotocols.Length > 0 ? Subprotocols : [ "ocpp2.1" ]
                                              });

            try
            {
                return (await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(15)), null);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception e)
            {
                return (null, e.Message);
            }

        }

        private static async Task<Boolean> EndsWithin(SshMessageConnection Connection, TimeSpan Within)
        {

            var until = DateTime.UtcNow + Within;

            while (Connection.IsAlive && DateTime.UtcNow < until)
                await Task.Delay(50);

            return !Connection.IsAlive;

        }

        private async Task<Boolean> RegisteredWithin(String Id, TimeSpan Within)
        {

            var until = DateTime.UtcNow + Within;

            while (!Controller.StationSSHServer!.ConnectedNetworkingNodeIds.Any(id => id.ToString() == Id) && DateTime.UtcNow < until)
                await Task.Delay(50);

            return Controller.StationSSHServer!.ConnectedNetworkingNodeIds.Any(id => id.ToString() == Id);

        }

        #endregion


        #region AStationWithItsKey_ConnectsOverSSH_AndIsAStationOfTheController()

        [Test]
        public async Task AStationWithItsKey_ConnectsOverSSH_AndIsAStationOfTheController()
        {

            GiveAKey("cs001", cs001Key);

            Assert.That(Controller.OCPPOverSSHServed, Is.True, "OCPP over SSH is switched on, and not served.");

            var (connection, why) = await Connect("cs001", cs001Key);

            Assert.That(connection, Is.Not.Null, why);

            Assert.Multiple(() => {
                Assert.That(connection!.Subprotocol,  Is.EqualTo("ocpp2.1"));
                Assert.That(connection.IsAlive,       Is.True);
            });

            Assert.That(await RegisteredWithin("cs001", TimeSpan.FromSeconds(10)), Is.True,
                        "The station connected, and the controller does not know it as one of its own.");

            // And the node's SSH server says who it is and with what.
            Assert.That(Controller.SSHSessions.Any(session => session.Account == "cs001" && session.Label == LocalController.OCPPOverSSHLabel),
                        Is.True);

            await connection!.Close(org.GraphDefined.Vanaheimr.Hermod.Messaging.MessageCloseCode.NormalClosure, "done");

        }

        #endregion

        #region AStrangersKey_OrAnotherStationsKey_IsTurnedAway()

        [Test]
        public async Task AStrangersKey_OrAnotherStationsKey_IsTurnedAway()
        {

            GiveAKey("cs001", cs001Key);
            GiveAKey("cs002", cs002Key);

            var (stranger, _)  = await Connect("cs001", strangerKey);
            var (other,    _)  = await Connect("cs001", cs002Key);
            var (nobody,   _)  = await Connect("cs404", strangerKey);

            Assert.Multiple(() => {
                Assert.That(stranger,  Is.Null, "A key nobody gave was let in.");
                Assert.That(other,     Is.Null, "One station's key let it in as another.");
                Assert.That(nobody,    Is.Null, "A station nobody added was let in.");
            });

        }

        #endregion

        #region AStation_IsGivenOCPP_AndNothingElse()

        /// <summary>
        /// No command line, no command, no SFTP: what the node confines every
        /// login of a kind to, held to here for the stations.
        /// </summary>
        [Test]
        public async Task AStation_IsGivenOCPP_AndNothingElse()
        {

            GiveAKey("cs001", cs001Key);

            await using var client = await SshClient.ConnectAsync("127.0.0.1",
                                                                  Controller.SSHPort!.Value.ToUInt16(),
                                                                  SSHAs("cs001", cs001Key)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(await Asks(client, "shell", []),               Is.False, "A station got the command line.");
            Assert.That(await Asks(client, "exec",  StringOf("who")),  Is.False, "A station got a command.");
            Assert.That(await Refused(client, "sftp"),                 Is.True,  "A station got SFTP.");
            Assert.That(await Refused(client, Subsystem),              Is.False, "A station did not get OCPP.");

        }

        private static Byte[] StringOf(String Text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(Text);
            var wire  = new Byte[4 + bytes.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(wire, (UInt32) bytes.Length);
            bytes.CopyTo(wire, 4);
            return wire;
        }

        private static async Task<Boolean> Asks(SshClient Client, String Type, Byte[] Payload)
        {
            var channel = await Client.Multiplexer.OpenChannelAsync("session");
            return await channel.SendRequestAsync(Type, true, Payload);
        }

        private static async Task<Boolean> Refused(SshClient Client, String Subsystem)
        {
            try
            {
                var channel = await Client.OpenSubsystemAsync(Subsystem).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                await channel.CloseAsync();
                return false;
            }
            catch (SshWireException)
            {
                return true;
            }
        }

        #endregion

        #region TheGroup_Decides_WhetherAnSSHKeyLetsAStationIn()

        [Test]
        public async Task TheGroup_Decides_WhetherAnSSHKeyLetsAStationIn()
        {

            Assert.That(Controller.StationLogins.TryAddOrUpdateGroup("field-test", "Field test", true, [ AuthMethod.Basic ], [ 1, 2 ], null, out var error),
                        Is.True, error);

            GiveAKey("cs001", cs001Key, "field-test");

            var (before, _) = await Connect("cs001", cs001Key);

            Assert.That(before, Is.Null, "A group that accepts no SSH keys let a station in with one.");

            Assert.That(Controller.StationLogins.TryAddOrUpdateGroup("field-test", "Field test", true, [ AuthMethod.Basic, AuthMethod.SSHKey ], [ 1 ], null, out error),
                        Is.True, error);

            var (after, why) = await Connect("cs001", cs001Key);

            Assert.That(after, Is.Not.Null,
                        $"A group that accepts SSH keys - and only security profile 1, which SSH is not - did not let the station in: {why}");

            Assert.That(Controller.StationLogins.TryAddOrUpdateGroup("field-test", "Field test", false, [ AuthMethod.Basic, AuthMethod.SSHKey ], [ 1 ], null, out error),
                        Is.True, error);

            Assert.That(await EndsWithin(after!, TimeSpan.FromSeconds(10)), Is.True, "A station of a group switched off stayed connected.");

            var (off, _) = await Connect("cs001", cs001Key);

            Assert.That(off, Is.Null, "A group switched off let a station in.");

        }

        #endregion

        #region AKeysOwnFromOption_IsHeldTo()

        /// <summary>
        /// from= on the key's line, as in an authorized_keys file: a station
        /// whose key is meant for the station network is not let in from
        /// anywhere else.
        /// </summary>
        [Test]
        public async Task AKeysOwnFromOption_IsHeldTo()
        {

            Assert.That(Controller.TryAddStationSSHKey("cs001", LineOf(cs001Key, "from=\"10.10.0.0/16\""), null, null, null, null, out _, out var error, out _),
                        Is.True, error);
            Assert.That(Controller.TryAddStationSSHKey("cs002", LineOf(cs002Key, "from=\"127.0.0.0/8\""), null, null, null, null, out _, out error, out _),
                        Is.True, error);

            var (elsewhere, _)  = await Connect("cs001", cs001Key);
            var (here, why)     = await Connect("cs002", cs002Key);

            Assert.Multiple(() => {
                Assert.That(elsewhere,  Is.Null,     "A key for 10.10.0.0/16 let its station in from the loopback address.");
                Assert.That(here,       Is.Not.Null, why);
            });

        }

        #endregion

        #region OCPPOverSSH_SwitchedOff_LetsNoStationSignInAtAll()

        /// <summary>
        /// Not merely no subsystem: a station is not let in to the SSH server
        /// at all while there is nothing on it for it.
        /// </summary>
        [Test]
        public async Task OCPPOverSSH_SwitchedOff_LetsNoStationSignInAtAll()
        {

            GiveAKey("cs001", cs001Key);

            Assert.That(Controller.TryUpdateOCPPServerConfiguration(JObject.Parse("""{ "ssh": { "enabled": false } }"""), out var error),
                        Is.True, error);

            var signedIn = true;

            try
            {
                await using var client = await SshClient.ConnectAsync("127.0.0.1",
                                                                      Controller.SSHPort!.Value.ToUInt16(),
                                                                      SSHAs("cs001", cs001Key)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                var channel = await client.Multiplexer.OpenChannelAsync("session").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                await channel.CloseAsync();
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
                signedIn = false;
            }

            Assert.That(signedIn, Is.False, "A station signed in to the SSH server while OCPP over SSH is switched off.");

        }

        #endregion

        #region AStationSwitchedOff_OrItsKeyTakenAway_IsSignedOutAtOnce()

        [Test]
        public async Task AStationSwitchedOff_OrItsKeyTakenAway_IsSignedOutAtOnce()
        {

            // Longer than the test, so that only the store's own news can do it.
            Controller.SSHSessionRecheckEvery = TimeSpan.FromMinutes(10);

            GiveAKey("cs001", cs001Key);
            GiveAKey("cs002", cs002Key);

            var (cs001, why1) = await Connect("cs001", cs001Key);
            var (cs002, why2) = await Connect("cs002", cs002Key);

            Assert.That(cs001, Is.Not.Null, why1);
            Assert.That(cs002, Is.Not.Null, why2);

            Assert.That(Controller.StationLogins.TrySetEnabled("cs001", false, out var error), Is.True, error);
            Assert.That(await EndsWithin(cs001!, TimeSpan.FromSeconds(10)), Is.True, "A station switched off stayed connected over SSH.");
            Assert.That(cs002!.IsAlive, Is.True, "Switching one station off ended another's session.");

            Assert.That(Controller.StationLogins.TryRemoveSSHKey("cs002", FingerprintOf(cs002Key), out error), Is.True, error);
            Assert.That(await EndsWithin(cs002, TimeSpan.FromSeconds(10)), Is.True, "A station whose key was taken away stayed connected over SSH.");

            var (again, _) = await Connect("cs002", cs002Key);

            Assert.That(again, Is.Null, "A key taken away still let its station in.");

        }

        #endregion

        #region OCPPOverSSH_SwitchedOff_EndsItsSessions_AndOn_ServesAgain_WithoutARestart()

        [Test]
        public async Task OCPPOverSSH_SwitchedOff_EndsItsSessions_AndOn_ServesAgain_WithoutARestart()
        {

            GiveAKey("cs001", cs001Key);

            var (first, why) = await Connect("cs001", cs001Key);

            Assert.That(first, Is.Not.Null, why);

            Assert.That(Controller.TryUpdateOCPPServerConfiguration(JObject.Parse("""{ "ssh": { "enabled": false } }"""), out var error),
                        Is.True, error);

            Assert.That(await EndsWithin(first!, TimeSpan.FromSeconds(10)), Is.True, "Switching OCPP over SSH off left a station connected.");
            Assert.That(Controller.OCPPOverSSHServed,                       Is.False);

            var (refused, _) = await Connect("cs001", cs001Key);

            Assert.That(refused, Is.Null, "OCPP over SSH is switched off, and a station connected.");

            Assert.That(Controller.TryUpdateOCPPServerConfiguration(JObject.Parse("""{ "ssh": { "enabled": true } }"""), out error),
                        Is.True, error);

            var (second, why2) = await Connect("cs001", cs001Key);

            Assert.Multiple(() => {
                Assert.That(second, Is.Not.Null, why2);
                Assert.That(Controller.OCPPServerConfigurationJSON()["state"]?["waitingForARestart"]?.Count(), Is.Zero,
                            "Switching OCPP over SSH on and off waits for a restart.");
            });

        }

        #endregion

        #region AnAccount_GetsNoOCPP_AndAStation_IsNoAccount()

        [Test]
        public async Task AnAccount_GetsNoOCPP_AndAStation_IsNoAccount()
        {

            var adminKey = SshHostKey.GenerateEd25519();

            var added = await Controller.AuthorizeSSHKey(LocalController.DefaultAdminUser, SshPublicKey.FromHostKey(adminKey).ToAuthorizedKeyLine());

            Assert.That(added.IsAdded, Is.True, added.Reason);

            await using var client = await SshClient.ConnectAsync("127.0.0.1",
                                                                  Controller.SSHPort!.Value.ToUInt16(),
                                                                  SSHAs(LocalController.DefaultAdminUser, adminKey)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(await Refused(client, Subsystem), Is.True, "An account got OCPP over SSH.");

            // And a station may not be given the name of an account: the two
            // would sign in under one name.
            Assert.That(Controller.TryAddStationSSHKey(LocalController.DefaultAdminUser, LineOf(cs001Key), null, null, null, null, out _, out var error, out _),
                        Is.False);

            Assert.That(error, Does.Contain("is an account"));

        }

        #endregion


        #region TheStore_OneKeyIsOneStation_AndAnAuthorityIsNoKey()

        [Test]
        public void TheStore_OneKeyIsOneStation_AndAnAuthorityIsNoKey()
        {

            GiveAKey("cs001", cs001Key);

            Assert.Multiple(() => {

                Assert.That(Controller.StationLogins.TryAddSSHKey("cs002", LineOf(cs001Key), null, null, null, null, out _, out var shared),
                            Is.False, "One key was given to two stations.");
                Assert.That(shared, Does.Contain("cs001"));

                Assert.That(Controller.StationLogins.TryAddSSHKey("cs001", LineOf(cs001Key), null, null, null, null, out _, out var twice),
                            Is.False, "One key was given to one station twice.");
                Assert.That(twice, Does.Contain("already"));

                Assert.That(Controller.StationLogins.TryAddSSHKey("cs003", LineOf(cs002Key, "cert-authority"), null, null, null, null, out _, out var authority),
                            Is.False, "A certificate authority was taken as a station's key.");
                Assert.That(authority, Does.Contain("cert-authority"));

                Assert.That(Controller.StationLogins.TryAddSSHKey("cs003", "not a key at all", null, null, null, null, out _, out _),
                            Is.False);

                Assert.That(Controller.StationLogins.TryRemoveSSHKey("cs001", FingerprintOf(cs002Key), out _),
                            Is.False, "A key the station does not have was taken away from it.");

            });

        }

        #endregion

        #region TheStore_KeepsTheKeys_AcrossAStart()

        [Test]
        public void TheStore_KeepsTheKeys_AcrossAStart()
        {

            Assert.That(Controller.StationLogins.TryAddSSHKey("cs001", LineOf(cs001Key, "from=\"10.10.0.0/16\""), "bay 1", null, "Ladepunkt 1", "tests", out var key, out var error),
                        Is.True, error);

            var again = new ChargingStationLogins(Controller.StationLogins.Path);

            Assert.That(again.TryLoad(out error), Is.True, error);
            Assert.That(again.TryGet("cs001", out var login), Is.True);

            Assert.Multiple(() => {
                Assert.That(login!.Keys.Select(known => known.Fingerprint),  Is.EqualTo(new[] { key!.Fingerprint }));
                Assert.That(login.Keys[0].Label,                             Is.EqualTo("bay 1"));
                Assert.That(login.Keys[0].Line,                              Does.StartWith("from="));
                Assert.That(login.Credentials,                               Does.Contain(AuthMethod.SSHKey));
                Assert.That(again.FindSSHKey("cs001", cs001Key.PublicKeyBlob, DateTimeOffset.UtcNow), Is.Not.Null);
                Assert.That(again.FindSSHKey("cs001", cs002Key.PublicKeyBlob, DateTimeOffset.UtcNow), Is.Null);
            });

        }

        #endregion

        #region TheStore_HoldsAFileWrittenByHand_ToWhatItSays()

        /// <summary>
        /// A key switched off or run out in the file lets nobody in, and a file
        /// that gives one key to two stations is not read at all.
        /// </summary>
        [Test]
        public void TheStore_HoldsAFileWrittenByHand_ToWhatItSays()
        {

            var file = Path.Combine(Directory, "by-hand.json");

            JObject KeyOf(ISshHostKey Key, String? Options = null, Boolean Disabled = false)
            {
                var json = new JObject(new JProperty("line",     LineOf(Key, Options)),
                                       new JProperty("created",  "2026-11-01T00:00:00Z"));
                if (Disabled)
                    json.Add("isDisabled", true);
                return json;
            }

            File.WriteAllText(file, new JObject(
                new JProperty("stations", new JArray(
                    new JObject(new JProperty("id", "cs001"), new JProperty("sshKeys", new JArray(KeyOf(cs001Key, Disabled: true)))),
                    new JObject(new JProperty("id", "cs002"), new JProperty("sshKeys", new JArray(KeyOf(cs002Key, "expiry-time=\"20200101\""))))
                ))
            ).ToString());

            var store = new ChargingStationLogins(file);

            Assert.That(store.TryLoad(out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(store.FindSSHKey("cs001", cs001Key.PublicKeyBlob, DateTimeOffset.UtcNow), Is.Null, "A key switched off let its station in.");
                Assert.That(store.FindSSHKey("cs002", cs002Key.PublicKeyBlob, DateTimeOffset.UtcNow), Is.Null, "A key that ran out let its station in.");
            });

            File.WriteAllText(file, new JObject(
                new JProperty("stations", new JArray(
                    new JObject(new JProperty("id", "cs001"), new JProperty("sshKeys", new JArray(KeyOf(cs001Key)))),
                    new JObject(new JProperty("id", "cs002"), new JProperty("sshKeys", new JArray(KeyOf(cs001Key))))
                ))
            ).ToString());

            Assert.That(new ChargingStationLogins(file).TryLoad(out error), Is.False, "A file giving one key to two stations was read.");
            Assert.That(error, Does.Contain("one key is one charging station"));

        }

        #endregion

        #region WhereTheStationsConnect_IsWhereTheSSHServerListens()

        /// <summary>
        /// The URL the stations are told is the SSH server's own address - not
        /// the station server's, which may listen where SSH does not - unless a
        /// name the stations know the controller by is given.
        /// </summary>
        [Test]
        public void WhereTheStationsConnect_IsWhereTheSSHServerListens()
        {

            Assert.That(Controller.SSHURL,           Is.Not.Null);
            Assert.That(Controller.OCPPServerSSHURL, Is.EqualTo(Controller.SSHURL));

            // Every address for the station server, from its next start: SSH
            // still listens where it did.
            Assert.That(Controller.TryUpdateOCPPServerConfiguration(JObject.Parse("""{ "address": "0.0.0.0" }"""), out var error),
                        Is.True, error);

            Assert.That(Controller.OCPPServerSSHURL, Is.EqualTo(Controller.SSHURL), "the station server's address is not where SSH listens");

            Assert.That(Controller.TryUpdateOCPPServerConfiguration(JObject.Parse("""{ "reachableAs": [ "lc.example.org" ] }"""), out error),
                        Is.True, error);

            var named = $"ssh://lc.example.org:{Controller.SSHPort}";

            Assert.That(Controller.OCPPServerSSHURL,                                                     Is.EqualTo(named));
            Assert.That(Controller.OCPPServerConfigurationJSON()["ssh"]?["state"]?["url"]?.Value<String>(), Is.EqualTo(named));

        }

        #endregion

        #region TheSettings_SayWhatIsWrong_AndWhatWaitsForARestart()

        [Test]
        public void TheSettings_SayWhatIsWrong_AndWhatWaitsForARestart()
        {

            Assert.Multiple(() => {

                Assert.That(LCConfig.OCPPServerConfiguration.TryParse(JObject.Parse("""{ "ssh": { "subprotocols": [ "ocpp3.0" ] } }"""), out _, out var unknown), Is.False);
                Assert.That(unknown, Does.Contain("ocppServer.ssh.subprotocols"));

                Assert.That(LCConfig.OCPPServerConfiguration.TryParse(JObject.Parse("""{ "ssh": { "maxMessageSize": 100 } }"""),       out _, out var tooSmall), Is.False);
                Assert.That(tooSmall, Does.Contain("ocppServer.ssh"));

                Assert.That(LCConfig.OCPPServerConfiguration.TryParse(JObject.Parse("""{ "ssh": true }"""),                            out _, out var notAnObject), Is.False);
                Assert.That(notAnObject, Does.Contain("ocppServer.ssh"));

                Assert.That(LCConfig.OCPPServerConfiguration.TryParse(JObject.Parse("""{ "ssh": { "enabled": true, "subprotocols": [ "ocpp2.1" ], "pingEverySeconds": 60 } }"""),
                                                                           out var parsed, out var error), Is.True, error);
                Assert.That(parsed!.ToJSON()["ssh"]?.ToString(Newtonsoft.Json.Formatting.None),
                            Is.EqualTo("""{"enabled":true,"subprotocols":["ocpp2.1"],"pingEverySeconds":60.0}"""));

                var running = new LCConfig.OCPPServerConfiguration().Effective();

                Assert.That(running.NeedsARestartFor(parsed),             Is.EquivalentTo(new[] { "ssh.subprotocols", "ssh.pingEverySeconds" }));
                Assert.That(running.NeedsARestartFor(new LCConfig.OCPPServerConfiguration(SSH: new LCConfig.OCPPServerSSH(Enabled: true))),
                            Is.Empty, "Switching OCPP over SSH on waits for a restart.");

            });

        }

        #endregion

        #region TheAPI_GivesAndTakesKeys()

        [Test]
        public async Task TheAPI_GivesAndTakesKeys()
        {

            using var http = await SignedIn();

            var root     = "/api/v1/configuration/ocpp-server/stations";

            var given    = await http.PostAsync($"{root}/cs001/sshKeys",
                                                JSONBody(new JProperty("line",   LineOf(cs001Key)),
                                                         new JProperty("label",  "bay 1"),
                                                         new JProperty("note",   "Ladepunkt 1")));

            Assert.That(given.StatusCode, Is.EqualTo(HttpStatusCode.OK), await given.Content.ReadAsStringAsync());

            var stations = await GetJSON(http, root);
            var cs001    = stations["stations"]!.OfType<JObject>().Single(station => station.Value<String>("id") == "cs001");

            Assert.Multiple(() => {
                Assert.That(cs001.Value<Boolean>("hasSSHKey"),                     Is.True);
                Assert.That(cs001["sshKeys"]?[0]?.Value<String>("fingerprint"),     Is.EqualTo(FingerprintOf(cs001Key)));
                Assert.That(cs001["sshKeys"]?[0]?.Value<String>("label"),           Is.EqualTo("bay 1"));
            });

            var bad      = await http.PostAsync($"{root}/cs002/sshKeys", JSONBody(new JProperty("line", "-----BEGIN OPENSSH PRIVATE KEY-----")));

            Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "A private key was taken.");

            // As the page sends it: without "SHA256:", in the URL-safe alphabet.
            var taken    = await http.DeleteAsync($"{root}/cs001/sshKeys/{org.GraphDefined.Vanaheimr.Hermod.HTTP.UserSSHKey.FingerprintInURL(FingerprintOf(cs001Key))}");

            Assert.That(taken.StatusCode, Is.EqualTo(HttpStatusCode.OK), await taken.Content.ReadAsStringAsync());
            Assert.That(Controller.StationLogins.TryGet("cs001", out var login) && !login.HasSSHKey, Is.True);

            var again    = await http.DeleteAsync($"{root}/cs001/sshKeys/{org.GraphDefined.Vanaheimr.Hermod.HTTP.UserSSHKey.FingerprintInURL(FingerprintOf(cs001Key))}");

            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        }

        #endregion

    }

}
