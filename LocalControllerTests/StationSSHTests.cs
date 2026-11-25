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

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using org.GraphDefined.Vanaheimr.Illias;

using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.LocalController.OCPP;
using cloud.charging.open.protocols.OCPP.Node.Serving;
using cloud.charging.open.protocols.WWCP.Node.Logins;

using LCConfig = cloud.charging.open.protocols.OCPP.Node.Serving;
using IPPort = org.GraphDefined.Vanaheimr.Hermod.IPPort;

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
        /// and OCPP-over-SSH with them.
        /// </summary>
        protected override JObject Configuration
        {
            get
            {

                var configuration = TestControllers.Offline;

                configuration["webSocketServer"] = new JObject(
                                                  new JProperty("enabled",           true),
                                                  new JProperty("address",           "127.0.0.1"),
                                                  new JProperty("port",              TestPorts.Free().ToUInt16()),
                                                  new JProperty("clientAuthentication",  new JArray("httpAuthWithoutTLS")),
                                                  new JProperty("subprotocols",      new JArray("ocpp2.1", "ocpp2.0.1")),
                                                  new JProperty("ssh",               new JObject(
                                                      new JProperty("enabled",       true)
                                                  ))
                                              );

                configuration["ssh"]        = new JObject(
                                                  new JProperty("enabled",           true),
                                                  new JProperty("port",              TestPorts.Free().ToUInt16())
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

        private void GiveAKey(User_Id Id, ISshHostKey Key, String? Group = null)
        {

            Assert.That(Controller.TryAddStationSSHKey(Id, LineOf(Key), "test", Group, null, "tests", out _, out var error, out _),
                        Is.True, error);

        }

        private SSHClientOptions SSHAs(String Id, ISshHostKey Key)

            => new () {
                   Username       = User_Id.Parse(Id),
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
                                                  Port          = Controller.SSHPort!.Value,
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


        #region AKeyTheCSMSInstalls_LetsTheStationIn_UntilTheCSMSDeletesIt()

        [Test]
        public async Task AKeyTheCSMSInstalls_LetsTheStationIn_UntilTheCSMSDeletesIt()
        {

            // As the CSMS asks it, over the controller's uplink.
            async Task<JObject> Ask(String Action, JObject Payload)
            {

                var method    = Controller.Node.OCPP.IN.GetType().GetMethod($"Receive_{Action}")!;
                var task      = (Task) method.Invoke(Controller.Node.OCPP.IN, [
                                    DateTimeOffset.UtcNow,
                                    null!,
                                    SourceRouting.To(Controller.Node.Id),
                                    NetworkPath.From(NetworkingNode_Id.Parse("csms01")),
                                    EventTracking_Id.New,
                                    cloud.charging.open.protocols.WWCP.Request_Id.NewRandom(),
                                    Payload,
                                    CancellationToken.None
                                ])!;

                await task;

                var response  = task.GetType().GetProperty("Result")!.GetValue(task)!;
                var message   = response.GetType().GetProperty("JSONResponseMessage")!.GetValue(response)!;

                return (JObject) message.GetType().GetProperty("Payload")!.GetValue(message)!;

            }

            var installed = await Ask("InstallSSHKey", new JObject(new JProperty("networkingNodeId", "cs001"),
                                                                   new JProperty("purpose",          "OCPP"),
                                                                   new JProperty("publicKey",        LineOf(cs001Key))));

            var nts       = await Ask("InstallSSHKey", new JObject(new JProperty("networkingNodeId", "cs001"),
                                                                   new JProperty("purpose",          "NTS"),
                                                                   new JProperty("publicKey",        LineOf(cs002Key))));

            var taken     = await Ask("InstallSSHKey", new JObject(new JProperty("networkingNodeId", "cs002"),
                                                                   new JProperty("purpose",          "OCPP"),
                                                                   new JProperty("publicKey",        LineOf(cs001Key))));

            Assert.Multiple(() => {
                Assert.That(installed.Value<String>("status"),                        Is.EqualTo("Accepted"), installed.ToString());
                Assert.That(installed["hostKeys"]!.Values<String>(),                  Is.EqualTo(new[] { Controller.SSHHostKey!.Split(' ')[1] }));
                Assert.That(nts["statusInfo"]!.Value<String>("reasonCode"),           Is.EqualTo("NotSupported"));
                Assert.That(taken["statusInfo"]!.Value<String>("reasonCode"),         Is.EqualTo("KeyOfOtherStation"));
            });

            var (connection, why) = await Connect("cs001", cs001Key);
            Assert.That(connection, Is.Not.Null, why);
            await connection!.Close(org.GraphDefined.Vanaheimr.Hermod.Messaging.MessageCloseCode.NormalClosure, "done");

            var listed    = await Ask("GetInstalledSSHKeys", new JObject(new JProperty("networkingNodeId", "cs001")));
            var deleted   = await Ask("DeleteSSHKey",        new JObject(new JProperty("networkingNodeId", "cs001"), new JProperty("keyFingerprint", FingerprintOf(cs001Key))));
            var again     = await Ask("DeleteSSHKey",        new JObject(new JProperty("networkingNodeId", "cs001"), new JProperty("keyFingerprint", FingerprintOf(cs001Key))));

            Assert.Multiple(() => {
                Assert.That(listed["keys"]!.Select(key => key.Value<String>("keyFingerprint")), Is.EqualTo(new[] { FingerprintOf(cs001Key) }));
                Assert.That(deleted.Value<String>("status"),                                     Is.EqualTo("Accepted"));
                Assert.That(again["statusInfo"]!.Value<String>("reasonCode"),                    Is.EqualTo("NotFound"));
            });

            var (refused, _) = await Connect("cs001", cs001Key);
            Assert.That(refused, Is.Null, "A key the CSMS deleted still let the station in.");

        }

        #endregion

        #region AStationWithItsKey_ConnectsOverSSH_AndIsAStationOfTheController()

        [Test]
        public async Task AStationWithItsKey_ConnectsOverSSH_AndIsAStationOfTheController()
        {

            GiveAKey(User_Id.Parse("cs001"), cs001Key);

            Assert.That(Controller.OCPPOverSSHServed, Is.True, "OCPP-over-SSH is switched on, and not served.");

            var (connection, why) = await Connect("cs001", cs001Key);

            Assert.That(connection, Is.Not.Null, why);

            Assert.Multiple(() => {
                Assert.That(connection!.Subprotocol,  Is.EqualTo("ocpp2.1"));
                Assert.That(connection.IsAlive,       Is.True);
            });

            Assert.That(await RegisteredWithin("cs001", TimeSpan.FromSeconds(10)), Is.True,
                        "The station connected, and the controller does not know it as one of its own.");

            // And the node's SSH server says who it is and with what.
            Assert.That(Controller.SSHSessions.Any(session => session.Account == User_Id.Parse("cs001") && session.Label == LocalController.OCPPOverSSHLabel),
                        Is.True);

            await connection!.Close(org.GraphDefined.Vanaheimr.Hermod.Messaging.MessageCloseCode.NormalClosure, "done");

        }

        #endregion

        #region AStrangersKey_OrAnotherStationsKey_IsTurnedAway()

        [Test]
        public async Task AStrangersKey_OrAnotherStationsKey_IsTurnedAway()
        {

            GiveAKey(User_Id.Parse("cs001"), cs001Key);
            GiveAKey(User_Id.Parse("cs002"), cs002Key);

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

            GiveAKey(User_Id.Parse("cs001"), cs001Key);

            await using var client = await SSHClient.Connect("127.0.0.1",
                                                                  Controller.SSHPort!.Value,
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

        private static async Task<Boolean> Asks(SSHClient Client, String Type, Byte[] Payload)
        {
            var channel = await Client.Multiplexer.OpenChannelAsync("session");
            return await channel.SendRequestAsync(Type, true, Payload);
        }

        private static async Task<Boolean> Refused(SSHClient Client, String Subsystem)
        {
            try
            {
                var channel = await Client.OpenSubsystem(Subsystem).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                await channel.CloseAsync();
                return false;
            }
            catch (SshWireException)
            {
                return true;
            }
        }

        #endregion

        #region AStation_IsGivenItsTimeAndItsNames_WhereItsGroupGivesThem_AndTheyAreServed()

        /// <summary>
        /// The connection is the station's, and OCPP one channel of it: NTS key
        /// establishment and DNS are further channels - a grant of their own,
        /// which the station's login group makes, and only of what the node
        /// serves. A key admitted for OCPP has neither by itself, and a
        /// subsystem the node serves to its accounts is no station's, whatever
        /// a group says.
        /// </summary>
        [Test]
        public async Task AStation_IsGivenItsTimeAndItsNames_WhereItsGroupGivesThem_AndTheyAreServed()
        {

            const String Other = "other@test.open.charging.cloud";

            SshSubsystemHandler serving = async (context, token) => {
                await Task.Delay(Timeout.Infinite, context.Closed).ContinueWith(_ => { }, TaskScheduler.Default);
            };

            async Task<SSHClient> SignedIn(String Id, ISshHostKey Key)
                => await SSHClient.Connect("127.0.0.1",
                                                Controller.SSHPort!.Value,
                                                SSHAs(Id, Key)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Boolean Group(String Id, IEnumerable<String>? Channels, out String? Error)
                => Controller.WebSocketLogins.TryAddOrUpdateGroup(Id, Id, true, [ AuthMethod.SSHKey ], [ WebSocketClientAuthentication.HTTPAuthWithoutTLS ], null, Channels, out Error, out _);

            Assert.That(Group("with-time",  [ LoginSSHSignIns.NTSKE ],                         out var error), Is.True, error);
            Assert.That(Group("with-both",  [ LoginSSHSignIns.NTSKE, LoginSSHSignIns.DNS ],  out error),     Is.True, error);
            Assert.That(Group("ocpp-only",  [],                                                  out error),     Is.True, error);

            // Nothing but its time and its names is a group's to give.
            Assert.That(Group("with-more",  [ Other ],  out var refused), Is.False, "A group gave its stations a subsystem that is no station's.");
            Assert.That(Group("with-sftp",  [ "sftp" ], out _),           Is.False, "A group gave its stations SFTP.");
            Assert.That(refused,                                          Does.Contain(Other));

            GiveAKey(User_Id.Parse("cs001"), cs001Key,    "with-time");
            GiveAKey(User_Id.Parse("cs002"), cs002Key,    "with-both");
            GiveAKey(User_Id.Parse("cs003"), strangerKey, "ocpp-only");

            // Given, but served by nobody yet.
            await using (var early = await SignedIn("cs002", cs002Key))
            {
                Assert.That(await Refused(early, LoginSSHSignIns.NTSKE),  Is.True,  "A station got NTS-KE that nobody serves.");
                Assert.That(await Refused(early, LoginSSHSignIns.DNS),    Is.True,  "A station got DNS that nobody serves.");
                Assert.That(await Refused(early, Subsystem),                Is.False, "A station did not get OCPP.");
            }

            Controller.AddSSHSubsystem(new SSHSubsystem(LoginSSHSignIns.NTSKE,  "NTS-KE",  serving));
            Controller.AddSSHSubsystem(new SSHSubsystem(LoginSSHSignIns.DNS,    "DNS",     serving));
            Controller.AddSSHSubsystem(new SSHSubsystem(Other,                    "Other",   serving));

            await using (var withTime = await SignedIn("cs001", cs001Key))
            {
                Assert.That(await Refused(withTime, Subsystem),                Is.False, "A station did not get OCPP.");
                Assert.That(await Refused(withTime, LoginSSHSignIns.NTSKE),  Is.False, "A station did not get NTS-KE, which its group gives it and the node serves.");
                Assert.That(await Refused(withTime, LoginSSHSignIns.DNS),    Is.True,  "A station got DNS, which its group does not give it.");
                Assert.That(await Refused(withTime, Other),                    Is.True,  "A station got a subsystem that is no station's.");
            }

            await using (var withBoth = await SignedIn("cs002", cs002Key))
            {
                Assert.That(await Refused(withBoth, Subsystem),                Is.False, "A station did not get OCPP.");
                Assert.That(await Refused(withBoth, LoginSSHSignIns.NTSKE),  Is.False, "A station did not get NTS-KE, which its group gives it and the node serves.");
                Assert.That(await Refused(withBoth, LoginSSHSignIns.DNS),    Is.False, "A station did not get DNS, which its group gives it and the node serves.");
                Assert.That(await Refused(withBoth, Other),                    Is.True,  "A station got a subsystem that is no station's.");
                Assert.That(await Refused(withBoth, "sftp"),                   Is.True,  "A station got SFTP.");
                Assert.That(await Asks(withBoth, "shell", []),                 Is.False, "A station got the command line.");
            }

            // A key admitted for OCPP has neither by itself.
            await using (var ocppOnly = await SignedIn("cs003", strangerKey))
            {
                Assert.That(await Refused(ocppOnly, Subsystem),                Is.False, "A station did not get OCPP.");
                Assert.That(await Refused(ocppOnly, LoginSSHSignIns.NTSKE),  Is.True,  "A station got NTS-KE with its key for OCPP alone.");
                Assert.That(await Refused(ocppOnly, LoginSSHSignIns.DNS),    Is.True,  "A station got DNS with its key for OCPP alone.");
            }

            // Taken away again: the next connection has it no more. And a
            // change that says nothing of it leaves it as it is.
            Assert.That(Controller.WebSocketLogins.TryAddOrUpdateGroup("with-both", "with-both", true, [ AuthMethod.SSHKey ], [ WebSocketClientAuthentication.HTTPAuthWithoutTLS ], null, out error), Is.True, error);
            Assert.That(Controller.WebSocketLogins.GetGroup("with-both")?.Channels, Is.EquivalentTo(new[] { LoginSSHSignIns.NTSKE, LoginSSHSignIns.DNS }));

            // A channel that lasts when the grant is taken away ends with it.
            await using var lasting = await SignedIn("cs002", cs002Key);
            var channel = await lasting.OpenSubsystem(LoginSSHSignIns.NTSKE).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(Group("with-both", [], out error), Is.True, error);

            var ended = false;

            try
            {
                var buffer = new Byte[16];
                ended = await Task.Run(async () => await channel.Input.ReadAsync(buffer, CancellationToken.None) == 0).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                ended = false;
            }
            catch (Exception)
            {
                ended = true;
            }

            Assert.That(ended, Is.True, "A channel of NTS-KE lasted after its group gave it no more.");

            await using (var noMore = await SignedIn("cs002", cs002Key))
            {
                Assert.That(await Refused(noMore, Subsystem),                Is.False, "A station did not get OCPP.");
                Assert.That(await Refused(noMore, LoginSSHSignIns.NTSKE),  Is.True,  "A station kept NTS-KE its group gives no more.");
                Assert.That(await Refused(noMore, LoginSSHSignIns.DNS),    Is.True,  "A station kept DNS its group gives no more.");
            }

        }

        #endregion

        #region AStation_ResolvesNamesAndGetsItsTime_FromTheController_OverItsSSHConnection()

        /// <summary>
        /// "ssh.dns" and "ssh.nts" switched on: a station whose group gives it the
        /// channels resolves a name the controller passes on to its own name server,
        /// and gets NTS keys over SSH for NTP with NTS from the controller's time
        /// server - over the connection it signed in with. A station whose group
        /// gives it OCPP alone has neither, and switched off again, nobody has.
        /// </summary>
        [Test]
        public async Task AStation_ResolvesNamesAndGetsItsTime_FromTheController_OverItsSSHConnection()
        {

            // The name server the controller asks: it knows the CSMS.
            var nameServer = new DNSServer(
                                 new AuthoritativeDNSRequestHandler(
                                     new InMemoryDNSZone().Add(new A(DomainName.Parse("csms.example.test."),
                                                                     DNSQueryClasses.IN,
                                                                     TimeSpan.FromMinutes(5),
                                                                     IPv4Address.Parse("127.0.0.42")))
                                 ),
                                 new DNSServerOptions {
                                     UDPUnicastSocket    = new IPSocket(IPv4Address.Localhost, IPPort.Zero),
                                     EnableUDPMulticast  = false,
                                     EnableTCPUnicast    = false
                                 }
                             );

            await nameServer.Start();

            try
            {

                Assert.That(Controller.TryUpdateDNSConfiguration(
                                JObject.Parse($$"""{ "servers": [ { "address": "127.0.0.1", "port": {{nameServer.ActiveUDPUnicastSocket!.Value.Port}} } ], "useCache": false }"""),
                                out var dnsError),
                            Is.True, dnsError);

                IPPort ntpPort;

                using (var probe = new System.Net.Sockets.UdpClient(new IPEndPoint(System.Net.IPAddress.Loopback, 0)))
                    ntpPort = IPPort.Parse(((IPEndPoint) probe.Client.LocalEndPoint!).Port);

                var on = await Controller.TryUpdateSSHConfiguration(
                                   JObject.Parse($$"""{ "dns": { "enabled": true }, "nts": { "enabled": true, "ntpPort": {{ntpPort}}, "ntpHost": "127.0.0.1" } }"""));

                Assert.That(on.IsChanged, Is.True, on.Error);
                Assert.That(on.Notice,    Is.Null, "something was not served as it says");

                Assert.That(Controller.WebSocketLogins.TryAddOrUpdateGroup("with-both", "with-both", true, [ AuthMethod.SSHKey ], [ WebSocketClientAuthentication.HTTPAuthWithoutTLS ], null,
                                                                           [ LoginSSHSignIns.NTSKE, LoginSSHSignIns.DNS ], out var error, out _), Is.True, error);
                Assert.That(Controller.WebSocketLogins.TryAddOrUpdateGroup("ocpp-only", "ocpp-only", true, [ AuthMethod.SSHKey ], [ WebSocketClientAuthentication.HTTPAuthWithoutTLS ], null,
                                                                           [], out error, out _), Is.True, error);

                GiveAKey(User_Id.Parse("cs002"), cs002Key,    "with-both");
                GiveAKey(User_Id.Parse("cs003"), strangerKey, "ocpp-only");

                await using (var cs002 = await SSHClient.Connect("127.0.0.1", Controller.SSHPort!.Value, SSHAs("cs002", cs002Key)).AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
                {

                    await using var dns = new DNSSSHClient(_ => ValueTask.FromResult(cs002), QueryTimeout: TimeSpan.FromSeconds(10));

                    var csms    = await dns.Query<A>(DomainName.Parse("csms.example.test."), ForceUpdate: true);

                    var nts     = new NTSClient(DomainName.Localhost, NTP_Port: ntpPort, IPVersionPreference: IPVersionPreference.IPv4Only);
                    var keys    = await nts.GetNTSKERecordsOverSSH(cs002);

                    Assert.That(keys.Success, Is.True, keys.ErrorMessage);

                    var time    = await nts.QueryTime(NTSKEResponse: keys.Response!, Timeout: TimeSpan.FromSeconds(10));

                    Assert.Multiple(() => {
                        Assert.That(csms.ResponseCode,                          Is.EqualTo(DNSResponseCodes.NoError));
                        Assert.That(csms.FilteredAnswers.Single().IPv4Address,  Is.EqualTo(IPv4Address.Parse("127.0.0.42")));
                        Assert.That(csms.AuthoritativeAnswer,                   Is.False, "the controller answered a name of somebody else's with authority");
                        Assert.That(time.Success,                               Is.True,  time.ErrorMessage);
                        Assert.That(keys.Response!.NTPv4Ports,                  Is.EqualTo(new[] { ntpPort }));
                    });

                }

                // OCPP alone, as its group gives it.
                await using (var cs003 = await SSHClient.Connect("127.0.0.1", Controller.SSHPort!.Value, SSHAs("cs003", strangerKey)).AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
                {
                    Assert.That(await Refused(cs003, LoginSSHSignIns.DNS),    Is.True,  "A station got DNS its group does not give it.");
                    Assert.That(await Refused(cs003, LoginSSHSignIns.NTSKE),  Is.True,  "A station got NTS-KE its group does not give it.");
                    Assert.That(await Refused(cs003, Subsystem),              Is.False, "A station did not get OCPP.");
                }

                // Switched off: served to nobody, whatever a group gives.
                var off = await Controller.TryUpdateSSHConfiguration(JObject.Parse("""{ "dns": { "enabled": false }, "nts": { "enabled": false } }"""));

                Assert.That(off.IsChanged, Is.True, off.Error);

                await using (var cs002 = await SSHClient.Connect("127.0.0.1", Controller.SSHPort!.Value, SSHAs("cs002", cs002Key)).AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
                {
                    Assert.That(await Refused(cs002, LoginSSHSignIns.DNS),    Is.True,  "A station got DNS that is switched off.");
                    Assert.That(await Refused(cs002, LoginSSHSignIns.NTSKE),  Is.True,  "A station got NTS-KE that is switched off.");
                    Assert.That(await Refused(cs002, Subsystem),              Is.False, "A station did not get OCPP.");
                }

            }
            finally
            {
                await nameServer.Stop();
            }

        }

        #endregion

        #region TheGroup_Decides_WhetherAnSSHKeyLetsAStationIn()

        [Test]
        public async Task TheGroup_Decides_WhetherAnSSHKeyLetsAStationIn()
        {

            Assert.That(Controller.WebSocketLogins.TryAddOrUpdateGroup("field-test", "Field test", true, [ AuthMethod.Basic ], [ WebSocketClientAuthentication.HTTPAuthWithoutTLS, WebSocketClientAuthentication.HTTPAuthOverTLS ], null, out var error),
                        Is.True, error);

            GiveAKey(User_Id.Parse("cs001"), cs001Key, "field-test");

            var (before, _) = await Connect("cs001", cs001Key);

            Assert.That(before, Is.Null, "A group that accepts no SSH keys let a station in with one.");

            Assert.That(Controller.WebSocketLogins.TryAddOrUpdateGroup("field-test", "Field test", true, [ AuthMethod.Basic, AuthMethod.SSHKey ], [ WebSocketClientAuthentication.HTTPAuthWithoutTLS ], null, out error),
                        Is.True, error);

            var (after, why) = await Connect("cs001", cs001Key);

            Assert.That(after, Is.Not.Null,
                        $"A group that accepts SSH keys - and only security profile 1, which SSH is not - did not let the station in: {why}");

            Assert.That(Controller.WebSocketLogins.TryAddOrUpdateGroup("field-test", "Field test", false, [ AuthMethod.Basic, AuthMethod.SSHKey ], [ WebSocketClientAuthentication.HTTPAuthWithoutTLS ], null, out error),
                        Is.True, error);

            Assert.That(await EndsWithin(after!, TimeSpan.FromSeconds(10)), Is.True, "A station of a group switched off stayed connected.");

            var (off, _) = await Connect("cs001", cs001Key);

            Assert.That(off, Is.Null, "A group switched off let a station in.");

        }

        #endregion

        #region AStationWithATOTPSecret_SignsInWithAOneTimePasswordOfItsSession()

        private const String TheSecret      = "the-secret-cs001-shares-with-the-controller";
        private const String AnotherSecret  = "a-secret-nobody-here-has-ever-heard-about";

        private void GiveASecret(User_Id Id, String Secret, String? Group = null)
        {

            Assert.That(Controller.WebSocketLogins.TrySetTOTPAuthentication(Id, Secret, null, null, null, null, Group, I18NString.Create("tests"), out var error),
                        Is.True, error);

        }

        /// <summary>
        /// A station connecting over SSH with no key at all, and a one-time
        /// password of the given secret that is bound to the session: the
        /// connection, or why it was not let in.
        /// </summary>
        private async Task<(SshMessageConnection? Connection, String? Why)> ConnectWithTotp(String Id, String Secret)
        {

            var config = new TOTPSettings(Secret,
                                          TOTPSettings.DefaultValidityTime,
                                          TOTPSettings.DefaultLength,
                                          TOTPSettings.DefaultAlphabet,
                                          org.GraphDefined.Vanaheimr.Hermod.TOTPHashAlgorithm.SHA256).ToTOTPConfig();

            var client = new SshMessageClient(new SshMessageClientOptions {
                                                  Host          = "127.0.0.1",
                                                  Port          = Controller.SSHPort!.Value,
                                                  Ssh           = new SSHClientOptions {
                                                                      Username          = User_Id.Parse(Id),
                                                                      VerifyHostKey     = blob => Controller.SSHHostKey == $"ssh-ed25519 {SshFingerprint.Sha256(blob)}",
                                                                      Credentials       = [],
                                                                      SessionBoundTOTP  = sessionId => SshSessionBoundTotp.Generate(config, sessionId)
                                                                  },
                                                  Subsystem     = Subsystem,
                                                  Subprotocols  = [ "ocpp2.1" ]
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

        /// <summary>
        /// totp@open.charging.cloud at the controller's SSH server: the token
        /// of the secret a station has here, bound to the session it is sent
        /// in, lets the station in with no key - and it is a station of the
        /// controller then, as with a key. Another secret does not, and
        /// neither does the right one for a station that has none.
        /// </summary>
        [Test]
        public async Task AStationWithATOTPSecret_SignsInWithAOneTimePasswordOfItsSession()
        {

            GiveASecret(User_Id.Parse("cs001"), TheSecret);
            GiveAKey   (User_Id.Parse("cs002"), cs002Key);

            var (wrongSecret, _)  = await ConnectWithTotp("cs001", AnotherSecret);
            var (noSecret,    _)  = await ConnectWithTotp("cs002", TheSecret);
            var (nobody,      _)  = await ConnectWithTotp("cs003", TheSecret);

            var (cs001, why)      = await ConnectWithTotp("cs001", TheSecret);

            Assert.Multiple(() => {
                Assert.That(wrongSecret,  Is.Null,      "A one-time password of another secret let the station in.");
                Assert.That(noSecret,     Is.Null,      "A one-time password let a station in that has no TOTP secret here.");
                Assert.That(nobody,       Is.Null,      "A one-time password let a station in that is not listed.");
                Assert.That(cs001,        Is.Not.Null,  $"The one-time password of its session did not let the station in: {why}");
            });

            Assert.That(await RegisteredWithin("cs001", TimeSpan.FromSeconds(10)), Is.True,
                        "A station that signed in with a one-time password is no station of the controller.");

        }

        #endregion

        #region TheGroup_Decides_WhetherAOneTimePasswordLetsAStationIn()

        /// <summary>
        /// The group has to accept one-time passwords, as over the WebSocket
        /// port - accepting SSH keys is not it - and a group switched off ends
        /// the session.
        /// </summary>
        [Test]
        public async Task TheGroup_Decides_WhetherAOneTimePasswordLetsAStationIn()
        {

            Assert.That(Controller.WebSocketLogins.TryAddOrUpdateGroup("field-test", "Field test", true, [ AuthMethod.Basic, AuthMethod.SSHKey ], [ WebSocketClientAuthentication.HTTPAuthWithoutTLS, WebSocketClientAuthentication.HTTPAuthOverTLS ], null, out var error),
                        Is.True, error);

            GiveASecret(User_Id.Parse("cs001"), TheSecret, "field-test");

            var (before, _) = await ConnectWithTotp("cs001", TheSecret);

            Assert.That(before, Is.Null, "A group that accepts no one-time passwords let a station in with one.");

            Assert.That(Controller.WebSocketLogins.TryAddOrUpdateGroup("field-test", "Field test", true, [ AuthMethod.TOTP ], [ WebSocketClientAuthentication.HTTPAuthWithoutTLS ], null, out error),
                        Is.True, error);

            var (after, why) = await ConnectWithTotp("cs001", TheSecret);

            Assert.That(after, Is.Not.Null,
                        $"A group that accepts one-time passwords - and no SSH keys - did not let the station in: {why}");

            Assert.That(Controller.WebSocketLogins.TryAddOrUpdateGroup("field-test", "Field test", false, [ AuthMethod.TOTP ], [ WebSocketClientAuthentication.HTTPAuthWithoutTLS ], null, out error),
                        Is.True, error);

            Assert.That(await EndsWithin(after!, TimeSpan.FromSeconds(10)), Is.True, "A station of a group switched off stayed connected.");

        }

        #endregion

        #region AStationWhoseSecretIsChangedOrTakenAway_IsSignedOutAtOnce()

        /// <summary>
        /// A session that came with a one-time password is held to the
        /// secret it came with, as one that came with a key is to its key.
        /// </summary>
        [Test]
        public async Task AStationWhoseSecretIsChangedOrTakenAway_IsSignedOutAtOnce()
        {

            // Longer than the test, so that only the store's own news can do it.
            Controller.SSHSessionRecheckEvery = TimeSpan.FromMinutes(10);

            GiveASecret(User_Id.Parse("cs001"), TheSecret);
            GiveASecret(User_Id.Parse("cs002"), TheSecret);

            var (cs001, why1) = await ConnectWithTotp("cs001", TheSecret);
            var (cs002, why2) = await ConnectWithTotp("cs002", TheSecret);

            Assert.That(cs001, Is.Not.Null, why1);
            Assert.That(cs002, Is.Not.Null, why2);

            GiveASecret(User_Id.Parse("cs001"), AnotherSecret);

            Assert.That(await EndsWithin(cs001!, TimeSpan.FromSeconds(10)), Is.True, "A station whose TOTP secret was changed stayed connected over SSH.");
            Assert.That(cs002!.IsAlive, Is.True, "Changing one station's secret ended another's session.");

            var (old,  _)    = await ConnectWithTotp("cs001", TheSecret);
            var (anew, why)  = await ConnectWithTotp("cs001", AnotherSecret);

            Assert.Multiple(() => {
                Assert.That(old,   Is.Null,      "The secret a station had before still let it in.");
                Assert.That(anew,  Is.Not.Null,  $"The secret a station has now did not let it in: {why}");
            });

            Assert.That(Controller.WebSocketLogins.TryClearTOTP(User_Id.Parse("cs002"), out var error), Is.True, error);
            Assert.That(await EndsWithin(cs002, TimeSpan.FromSeconds(10)), Is.True, "A station whose TOTP secret was taken away stayed connected over SSH.");

            var (again, _) = await ConnectWithTotp("cs002", TheSecret);

            Assert.That(again, Is.Null, "A secret taken away still let its station in.");

        }

        #endregion


        #region AStationWithAPassword_SignsInWithIt()

        private const String ThePassword      = "the-password-cs001-has-at-the-controller";
        private const String AnotherPassword  = "a-password-nobody-here-has-ever-heard-of";

        private void GiveAPassword(User_Id Id, String Password, String? Group = null)
        {

            Assert.That(Controller.WebSocketLogins.TrySetPasswordAuthentication(Id, Password, Group, I18NString.Create("tests"), out var error),
                        Is.True, error);

        }

        /// <summary>
        /// A station connecting over SSH with no key at all, and the given
        /// password: the connection, or why it was not let in.
        /// </summary>
        private async Task<(SshMessageConnection? Connection, String? Why)> ConnectWithPassword(String Id, String Password)
        {

            var client = new SshMessageClient(new SshMessageClientOptions {
                                                  Host          = "127.0.0.1",
                                                  Port          = Controller.SSHPort!.Value,
                                                  Ssh           = new SSHClientOptions {
                                                                      Username       = User_Id.Parse(Id),
                                                                      VerifyHostKey  = blob => Controller.SSHHostKey == $"ssh-ed25519 {SshFingerprint.Sha256(blob)}",
                                                                      Credentials    = [],
                                                                      Password       = Password
                                                                  },
                                                  Subsystem     = Subsystem,
                                                  Subprotocols  = [ "ocpp2.1" ]
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

        /// <summary>
        /// "password" at the controller's SSH server: the password a station
        /// has here for HTTP Basic lets it in with no key - and it is a station
        /// of the controller then, as with a key. Another password does not,
        /// and neither does one for a station that has none, nor the secret of
        /// a station's one-time passwords sent as a password.
        /// </summary>
        [Test]
        public async Task AStationWithAPassword_SignsInWithIt()
        {

            GiveAPassword(User_Id.Parse("cs001"), ThePassword);
            GiveASecret  (User_Id.Parse("cs002"), TheSecret);

            var (wrongPassword, _)  = await ConnectWithPassword("cs001", AnotherPassword);
            var (noPassword,    _)  = await ConnectWithPassword("cs002", TheSecret);
            var (nobody,        _)  = await ConnectWithPassword("cs003", ThePassword);

            var (cs001, why)        = await ConnectWithPassword("cs001", ThePassword);

            Assert.Multiple(() => {
                Assert.That(wrongPassword,  Is.Null,      "Another password let the station in.");
                Assert.That(noPassword,     Is.Null,      "A password let a station in that has no password here.");
                Assert.That(nobody,         Is.Null,      "A password let a station in that is not listed.");
                Assert.That(cs001,          Is.Not.Null,  $"Its password did not let the station in: {why}");
            });

            Assert.That(await RegisteredWithin("cs001", TimeSpan.FromSeconds(10)), Is.True,
                        "A station that signed in with its password is no station of the controller.");

        }

        #endregion

        #region TheGroup_Decides_WhetherAPasswordLetsAStationIn()

        /// <summary>
        /// The group has to accept passwords, as over the WebSocket port -
        /// accepting SSH keys or one-time passwords is not it - and a group
        /// switched off ends the session.
        /// </summary>
        [Test]
        public async Task TheGroup_Decides_WhetherAPasswordLetsAStationIn()
        {

            Assert.That(Controller.WebSocketLogins.TryAddOrUpdateGroup("field-test", "Field test", true, [ AuthMethod.TOTP, AuthMethod.SSHKey ], [ WebSocketClientAuthentication.HTTPAuthWithoutTLS, WebSocketClientAuthentication.HTTPAuthOverTLS ], null, out var error),
                        Is.True, error);

            GiveAPassword(User_Id.Parse("cs001"), ThePassword, "field-test");

            var (before, _) = await ConnectWithPassword("cs001", ThePassword);

            Assert.That(before, Is.Null, "A group that accepts no passwords let a station in with one.");

            Assert.That(Controller.WebSocketLogins.TryAddOrUpdateGroup("field-test", "Field test", true, [ AuthMethod.Basic ], [ WebSocketClientAuthentication.HTTPAuthWithoutTLS ], null, out error),
                        Is.True, error);

            var (after, why) = await ConnectWithPassword("cs001", ThePassword);

            Assert.That(after, Is.Not.Null,
                        $"A group that accepts passwords - and no SSH keys - did not let the station in: {why}");

            Assert.That(Controller.WebSocketLogins.TryAddOrUpdateGroup("field-test", "Field test", false, [ AuthMethod.Basic ], [ WebSocketClientAuthentication.HTTPAuthWithoutTLS ], null, out error),
                        Is.True, error);

            Assert.That(await EndsWithin(after!, TimeSpan.FromSeconds(10)), Is.True, "A station of a group switched off stayed connected.");

        }

        #endregion

        #region AStationWhosePasswordIsChangedOrTakenAway_IsSignedOutAtOnce()

        /// <summary>
        /// A session that came with a password is held to the password it
        /// came with, as one that came with a key is to its key.
        /// </summary>
        [Test]
        public async Task AStationWhosePasswordIsChangedOrTakenAway_IsSignedOutAtOnce()
        {

            // Longer than the test, so that only the store's own news can do it.
            Controller.SSHSessionRecheckEvery = TimeSpan.FromMinutes(10);

            GiveAPassword(User_Id.Parse("cs001"), ThePassword);
            GiveAPassword(User_Id.Parse("cs002"), ThePassword);

            var (cs001, why1) = await ConnectWithPassword("cs001", ThePassword);
            var (cs002, why2) = await ConnectWithPassword("cs002", ThePassword);

            Assert.That(cs001, Is.Not.Null, why1);
            Assert.That(cs002, Is.Not.Null, why2);

            GiveAPassword(User_Id.Parse("cs001"), AnotherPassword);

            Assert.That(await EndsWithin(cs001!, TimeSpan.FromSeconds(10)), Is.True, "A station whose password was changed stayed connected over SSH.");
            Assert.That(cs002!.IsAlive, Is.True, "Changing one station's password ended another's session.");

            var (old,  _)    = await ConnectWithPassword("cs001", ThePassword);
            var (anew, why)  = await ConnectWithPassword("cs001", AnotherPassword);

            Assert.Multiple(() => {
                Assert.That(old,   Is.Null,      "The password a station had before still let it in.");
                Assert.That(anew,  Is.Not.Null,  $"The password a station has now did not let it in: {why}");
            });

            Assert.That(Controller.WebSocketLogins.TryClearPassword(User_Id.Parse("cs002"), out var error), Is.True, error);
            Assert.That(await EndsWithin(cs002, TimeSpan.FromSeconds(10)), Is.True, "A station whose password was taken away stayed connected over SSH.");

            var (again, _) = await ConnectWithPassword("cs002", ThePassword);

            Assert.That(again, Is.Null, "A password taken away still let its station in.");

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

            Assert.That(Controller.TryAddStationSSHKey(User_Id.Parse("cs001"), LineOf(cs001Key, "from=\"10.10.0.0/16\""), null, null, null, null, out _, out var error, out _),
                        Is.True, error);
            Assert.That(Controller.TryAddStationSSHKey(User_Id.Parse("cs002"), LineOf(cs002Key, "from=\"127.0.0.0/8\""), null, null, null, null, out _, out error, out _),
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

            GiveAKey(User_Id.Parse("cs001"), cs001Key);

            Assert.That(Controller.TryUpdateOCPPServerConfiguration(JObject.Parse("""{ "ssh": { "enabled": false } }"""), out var error),
                        Is.True, error);

            var signedIn = true;

            try
            {
                await using var client = await SSHClient.Connect("127.0.0.1",
                                                                      Controller.SSHPort!.Value,
                                                                      SSHAs("cs001", cs001Key)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                var channel = await client.Multiplexer.OpenChannelAsync("session").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                await channel.CloseAsync();
            }
            catch (Exception exception) when (exception is not TimeoutException)
            {
                signedIn = false;
            }

            Assert.That(signedIn, Is.False, "A station signed in to the SSH server while OCPP-over-SSH is switched off.");

        }

        #endregion

        #region AStationSwitchedOff_OrItsKeyTakenAway_IsSignedOutAtOnce()

        [Test]
        public async Task AStationSwitchedOff_OrItsKeyTakenAway_IsSignedOutAtOnce()
        {

            // Longer than the test, so that only the store's own news can do it.
            Controller.SSHSessionRecheckEvery = TimeSpan.FromMinutes(10);

            GiveAKey(User_Id.Parse("cs001"), cs001Key);
            GiveAKey(User_Id.Parse("cs002"), cs002Key);

            var (cs001, why1) = await Connect("cs001", cs001Key);
            var (cs002, why2) = await Connect("cs002", cs002Key);

            Assert.That(cs001, Is.Not.Null, why1);
            Assert.That(cs002, Is.Not.Null, why2);

            Assert.That(Controller.WebSocketLogins.TrySetEnabled(User_Id.Parse("cs001"), false, out var error), Is.True, error);
            Assert.That(await EndsWithin(cs001!, TimeSpan.FromSeconds(10)), Is.True, "A station switched off stayed connected over SSH.");
            Assert.That(cs002!.IsAlive, Is.True, "Switching one station off ended another's session.");

            Assert.That(Controller.WebSocketLogins.TryRemoveSSHKey(User_Id.Parse("cs002"), FingerprintOf(cs002Key), out error), Is.True, error);
            Assert.That(await EndsWithin(cs002, TimeSpan.FromSeconds(10)), Is.True, "A station whose key was taken away stayed connected over SSH.");

            var (again, _) = await Connect("cs002", cs002Key);

            Assert.That(again, Is.Null, "A key taken away still let its station in.");

        }

        #endregion

        #region OCPPOverSSH_SwitchedOff_EndsItsSessions_AndOn_ServesAgain_WithoutARestart()

        [Test]
        public async Task OCPPOverSSH_SwitchedOff_EndsItsSessions_AndOn_ServesAgain_WithoutARestart()
        {

            GiveAKey(User_Id.Parse("cs001"), cs001Key);

            var (first, why) = await Connect("cs001", cs001Key);

            Assert.That(first, Is.Not.Null, why);

            Assert.That(Controller.TryUpdateOCPPServerConfiguration(JObject.Parse("""{ "ssh": { "enabled": false } }"""), out var error),
                        Is.True, error);

            Assert.That(await EndsWithin(first!, TimeSpan.FromSeconds(10)), Is.True, "Switching OCPP-over-SSH off left a station connected.");
            Assert.That(Controller.OCPPOverSSHServed,                       Is.False);

            var (refused, _) = await Connect("cs001", cs001Key);

            Assert.That(refused, Is.Null, "OCPP-over-SSH is switched off, and a station connected.");

            Assert.That(Controller.TryUpdateOCPPServerConfiguration(JObject.Parse("""{ "ssh": { "enabled": true } }"""), out error),
                        Is.True, error);

            var (second, why2) = await Connect("cs001", cs001Key);

            Assert.Multiple(() => {
                Assert.That(second, Is.Not.Null, why2);
                Assert.That(Controller.OCPPServerConfigurationJSON()["state"]?["waitingForARestart"]?.Count(), Is.Zero,
                            "Switching OCPP-over-SSH on and off waits for a restart.");
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

            await using var client = await SSHClient.Connect("127.0.0.1",
                                                                  Controller.SSHPort!.Value,
                                                                  SSHAs(LocalController.DefaultAdminUser.ToString(), adminKey)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(await Refused(client, Subsystem), Is.True, "An account got OCPP-over-SSH.");

            // And a station may not be given the name of an account: the two
            // would sign in under one name.
            Assert.That(Controller.TryAddStationSSHKey(User_Id.Parse(LocalController.DefaultAdminUser.ToString()), LineOf(cs001Key), null, null, null, null, out _, out var error, out _),
                        Is.False);

            Assert.That(error, Does.Contain("is an account"));

        }

        #endregion


        #region TheStore_OneKeyIsOneStation_AndAnAuthorityIsNoKey()

        [Test]
        public void TheStore_OneKeyIsOneStation_AndAnAuthorityIsNoKey()
        {

            GiveAKey(User_Id.Parse("cs001"), cs001Key);

            Assert.Multiple(() => {

                Assert.That(Controller.WebSocketLogins.TryAddSSHKey(User_Id.Parse("cs002"), LineOf(cs001Key), null, null, null, null, out _, out var shared),
                            Is.False, "One key was given to two stations.");
                Assert.That(shared, Does.Contain("cs001"));

                Assert.That(Controller.WebSocketLogins.TryAddSSHKey(User_Id.Parse("cs001"), LineOf(cs001Key), null, null, null, null, out _, out var twice),
                            Is.False, "One key was given to one station twice.");
                Assert.That(twice, Does.Contain("already"));

                Assert.That(Controller.WebSocketLogins.TryAddSSHKey(User_Id.Parse("cs003"), LineOf(cs002Key, "cert-authority"), null, null, null, null, out _, out var authority),
                            Is.False, "A certificate authority was taken as a station's key.");
                Assert.That(authority, Does.Contain("cert-authority"));

                Assert.That(Controller.WebSocketLogins.TryAddSSHKey(User_Id.Parse("cs003"), "not a key at all", null, null, null, null, out _, out _),
                            Is.False);

                Assert.That(Controller.WebSocketLogins.TryRemoveSSHKey(User_Id.Parse("cs001"), FingerprintOf(cs002Key), out _),
                            Is.False, "A key the station does not have was taken away from it.");

            });

        }

        #endregion

        #region TheStore_KeepsTheKeys_AcrossAStart()

        [Test]
        public void TheStore_KeepsTheKeys_AcrossAStart()
        {

            Assert.That(Controller.WebSocketLogins.TryAddSSHKey(User_Id.Parse("cs001"), LineOf(cs001Key, "from=\"10.10.0.0/16\""), "bay 1", null, I18NString.Create("Ladepunkt 1"), "tests", out var key, out var error),
                        Is.True, error);

            var again = new WebSocketLogins(Controller.WebSocketLogins.Path);

            Assert.That(again.TryLoad(out error), Is.True, error);
            Assert.That(again.TryGet(User_Id.Parse("cs001"), out var login), Is.True);

            Assert.Multiple(() => {
                Assert.That(login!.Keys.Select(known => known.Fingerprint),  Is.EqualTo(new[] { key!.Fingerprint }));
                Assert.That(login.Keys[0].Label,                             Is.EqualTo("bay 1"));
                Assert.That(login.Keys[0].Line,                              Does.StartWith("from="));
                Assert.That(login.Credentials,                               Does.Contain(AuthMethod.SSHKey));
                Assert.That(again.FindSSHKey(User_Id.Parse("cs001"), cs001Key.PublicKeyBlob, DateTimeOffset.UtcNow), Is.Not.Null);
                Assert.That(again.FindSSHKey(User_Id.Parse("cs001"), cs002Key.PublicKeyBlob, DateTimeOffset.UtcNow), Is.Null);
            });

        }

        #endregion

        #region TheStore_HoldsAFileWrittenByHand_ToWhatItSays()

        /// <summary>
        /// A key switched off or run out in the file lets nobody in, nor does
        /// the key of a station the file switches off, and a file that gives
        /// one key to two stations is not read at all.
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

            var cs003Key = SshHostKey.GenerateEd25519();

            File.WriteAllText(file, new JObject(
                new JProperty("logins", new JArray(
                    new JObject(new JProperty("login", "cs001"), new JProperty("sshKeys", new JArray(KeyOf(cs001Key, Disabled: true)))),
                    new JObject(new JProperty("login", "cs002"), new JProperty("sshKeys", new JArray(KeyOf(cs002Key, "expiry-time=\"20200101\"")))),
                    new JObject(new JProperty("login", "cs003"), new JProperty("enabled", false), new JProperty("sshKeys", new JArray(KeyOf(cs003Key)))),
                    new JObject(new JProperty("login", "cs004"), new JProperty("sshKeys", new JArray(KeyOf(strangerKey))))
                ))
            ).ToString());

            var store = new WebSocketLogins(file);

            Assert.That(store.TryLoad(out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(store.FindSSHKey(User_Id.Parse("cs001"), cs001Key.PublicKeyBlob,    DateTimeOffset.UtcNow), Is.Null,     "A key switched off let its station in.");
                Assert.That(store.FindSSHKey(User_Id.Parse("cs002"), cs002Key.PublicKeyBlob,    DateTimeOffset.UtcNow), Is.Null,     "A key that ran out let its station in.");
                Assert.That(store.FindSSHKey(User_Id.Parse("cs003"), cs003Key.PublicKeyBlob,    DateTimeOffset.UtcNow), Is.Null,     "A station switched off was let in by its key.");
                Assert.That(store.FindSSHKey(User_Id.Parse("cs004"), strangerKey.PublicKeyBlob, DateTimeOffset.UtcNow), Is.Not.Null, "A key as written by hand let nobody in.");
            });

            File.WriteAllText(file, new JObject(
                new JProperty("logins", new JArray(
                    new JObject(new JProperty("login", "cs001"), new JProperty("sshKeys", new JArray(KeyOf(cs001Key)))),
                    new JObject(new JProperty("login", "cs002"), new JProperty("sshKeys", new JArray(KeyOf(cs001Key))))
                ))
            ).ToString());

            Assert.That(new WebSocketLogins(file).TryLoad(out error), Is.False, "A file giving one key to two stations was read.");
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

        #region TheLargestMessage_IsWhatAWebSocketTakes()

        /// <summary>
        /// With nothing configured, OCPP-over-SSH takes a message as large as
        /// OCPP over a WebSocket does: 64 MiB. At 16 MiB, a message between
        /// the two got in over a WebSocket and not out over SSH.
        /// </summary>
        [Test]
        public void TheLargestMessage_IsWhatAWebSocketTakes()
        {

            Assert.Multiple(() => {

                Assert.That((Controller.OCPPServerConfigurationJSON()["ssh"]?["maxMessageSize"]?.Value<UInt64>()),
                            Is.EqualTo(org.GraphDefined.Vanaheimr.Hermod.WebSocket.WebSocketFrame.DefaultMaxPayloadSize));

                Assert.That((Controller.OCPPServerConfigurationJSON()["ssh"]?["maxMessageSize"]?.Value<UInt64>()),
                            Is.EqualTo(64UL * 1024 * 1024));

            });

        }

        #endregion

        #region TheSettings_SayWhatIsWrong_AndWhatWaitsForARestart()

        [Test]
        public void TheSettings_SayWhatIsWrong_AndWhatWaitsForARestart()
        {

            Assert.Multiple(() => {

                Assert.That(LCConfig.WebSocketServerConfiguration.TryParse(JObject.Parse("""{ "ssh": { "subprotocols": [ "ocpp3.0" ] } }"""), out _, out var unknown), Is.False);
                Assert.That(unknown, Does.Contain("webSocketServer.ssh.subprotocols"));

                Assert.That(LCConfig.WebSocketServerConfiguration.TryParse(JObject.Parse("""{ "ssh": { "maxMessageSize": 100 } }"""),       out _, out var tooSmall), Is.False);
                Assert.That(tooSmall, Does.Contain("webSocketServer.ssh"));

                Assert.That(LCConfig.WebSocketServerConfiguration.TryParse(JObject.Parse("""{ "ssh": true }"""),                            out _, out var notAnObject), Is.False);
                Assert.That(notAnObject, Does.Contain("webSocketServer.ssh"));

                Assert.That(LCConfig.WebSocketServerConfiguration.TryParse(JObject.Parse("""{ "ssh": { "enabled": true, "subprotocols": [ "ocpp2.1" ], "pingEverySeconds": 60 } }"""),
                                                                           out var parsed, out var error), Is.True, error);
                Assert.That(parsed!.ToJSON()["ssh"]?.ToString(Newtonsoft.Json.Formatting.None),
                            Is.EqualTo("""{"enabled":true,"subprotocols":["ocpp2.1"],"pingEverySeconds":60.0}"""));

                var running = new LCConfig.WebSocketServerConfiguration().Effective();

                Assert.That(running.NeedsARestartFor(parsed),             Is.EquivalentTo(new[] { "ssh.subprotocols", "ssh.pingEverySeconds" }));
                Assert.That(running.NeedsARestartFor(new LCConfig.WebSocketServerConfiguration(SSH: new LCConfig.WebSocketServerSSH(Enabled: true))),
                            Is.Empty, "Switching OCPP-over-SSH on waits for a restart.");

            });

        }

        #endregion

        #region TheAPI_GivesAndTakesKeys()

        [Test]
        public async Task TheAPI_GivesAndTakesKeys()
        {

            using var http = await SignedIn();

            var root     = "/api/v1/configuration/ocpp-server/logins";

            var given    = await http.PostAsync($"{root}/cs001/sshKeys",
                                                JSONBody(new JProperty("line",   LineOf(cs001Key)),
                                                         new JProperty("label",  "bay 1"),
                                                         new JProperty("description",   new JObject(new JProperty("en", "Ladepunkt 1")))));

            Assert.That(given.StatusCode, Is.EqualTo(HttpStatusCode.OK), await given.Content.ReadAsStringAsync());

            var stations = await GetJSON(http, root);
            var cs001    = stations["logins"]!.OfType<JObject>().Single(station => station.Value<String>("login") == "cs001");

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
            Assert.That(Controller.WebSocketLogins.TryGet(User_Id.Parse("cs001"), out var login) && !login.HasSSHKey, Is.True);

            var again    = await http.DeleteAsync($"{root}/cs001/sshKeys/{org.GraphDefined.Vanaheimr.Hermod.HTTP.UserSSHKey.FingerprintInURL(FingerprintOf(cs001Key))}");

            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        }

        #endregion

    }

}
