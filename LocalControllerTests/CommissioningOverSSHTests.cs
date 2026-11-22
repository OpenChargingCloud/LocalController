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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;
using org.GraphDefined.Vanaheimr.Hermod.Messaging;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Commissioning over SSH, on a local controller: a tool speaking OCPP to
    /// the controller on its SSH server, as its CSMS would - on the subsystem
    /// its charging stations speak OCPP over as well.
    /// </summary>
    /// <remarks>
    /// One subsystem, and who signed in says what comes: an account is a tool,
    /// a WebSocket login with an SSH key a station. What a tool asks is for the
    /// controller itself, and not on its way to the CSMS as what a station
    /// asks is.
    /// </remarks>
    [TestFixture]
    public class CommissioningOverSSHTests : ALocalControllerTests
    {

        #region Data

        private const String Subsystem = "ocpp@open.charging.cloud";

        private readonly ISshHostKey adminKey  = SshHostKey.GenerateEd25519();
        private readonly ISshHostKey cs001Key  = SshHostKey.GenerateEd25519();

        #endregion

        #region (override) Configuration

        /// <summary>
        /// A controller whose station server and SSH server are switched on,
        /// OCPP-over-SSH with them, and commissioning over SSH.
        /// </summary>
        protected override JObject Configuration
        {
            get
            {

                var configuration = TestControllers.Offline;

                configuration["webSocketServer"] = new JObject(
                                                       new JProperty("enabled",               true),
                                                       new JProperty("address",               "127.0.0.1"),
                                                       new JProperty("port",                  TestPorts.Free().ToUInt16()),
                                                       new JProperty("clientAuthentication",  new JArray("httpAuthWithoutTLS")),
                                                       new JProperty("subprotocols",          new JArray("ocpp2.1", "ocpp2.0.1")),
                                                       new JProperty("ssh",                   new JObject(
                                                           new JProperty("enabled",           true)
                                                       ))
                                                   );

                configuration["ssh"]            = new JObject(
                                                       new JProperty("enabled",               true),
                                                       new JProperty("port",                  TestPorts.Free().ToUInt16())
                                                   );

                configuration["commissioning"]  = new JObject(
                                                       new JProperty("enabled",               true)
                                                   );

                return configuration;

            }
        }

        #endregion

        #region (private) helpers

        private SSHClientOptions SSHAs(String Id, ISshHostKey Key)

            => new () {
                   Username       = User_Id.Parse(Id),
                   VerifyHostKey  = blob => Controller.SSHHostKey == $"ssh-ed25519 {SshFingerprint.Sha256(blob)}",
                   Credentials    = [ Key ]
               };

        /// <summary>
        /// A client of the subsystem, signing in as the given login with the
        /// given key: the connection and what it was sent, or why it was not
        /// let in.
        /// </summary>
        private async Task<(SshMessageConnection? Connection, ConcurrentQueue<JArray> Received, String? Why)> Connect(String       Id,
                                                                                                                     ISshHostKey  Key)
        {

            var received = new ConcurrentQueue<JArray>();

            var client   = new SshMessageClient(new SshMessageClientOptions {
                                                    Host          = "127.0.0.1",
                                                    Port          = Controller.SSHPort!.Value,
                                                    Ssh           = SSHAs(Id, Key),
                                                    Subsystem     = Subsystem,
                                                    Subprotocols  = [ "ocpp2.1" ]
                                                });

            // Before the connection is there, so that nothing said on it goes
            // past unheard.
            client.OnTextMessageReceived += (timestamp, sender, connection, text, cancellationToken) => {
                received.Enqueue(JArray.Parse(text));
                return Task.CompletedTask;
            };

            try
            {
                return (await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(15)), received, null);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception e)
            {
                return (null, received, e.Message);
            }

        }

        /// <summary>
        /// GetVariables of the given variables of OCPPServerController, as the
        /// tool asks it: the answer - a CALLRESULT or a CALLERROR - or null
        /// where none came within ten seconds.
        /// </summary>
        private static async Task<JArray?> AskForTheStationPort(SshMessageConnection     Connection,
                                                                ConcurrentQueue<JArray>  Received,
                                                                params String[]          Variables)
        {

            var id = $"tool-{Guid.NewGuid():N}";

            await Connection.SendTextMessage(
                      new JArray(
                          2,
                          id,
                          "GetVariables",
                          new JObject(
                              new JProperty("getVariableData", new JArray(
                                  Variables.Select(variable => new JObject(
                                      new JProperty("component", new JObject(new JProperty("name", "OCPPServerController"))),
                                      new JProperty("variable",  new JObject(new JProperty("name", variable)))
                                  ))
                              ))
                          )
                      ).ToString(Formatting.None)
                  );

            var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

            while (DateTimeOffset.UtcNow < until)
            {

                if (Received.FirstOrDefault(message => message[0].Value<Int32>() is 3 or 4 && message[1].Value<String>() == id) is JArray answer)
                    return answer;

                await Task.Delay(20);

            }

            return null;

        }

        private async Task GiveTheAdministratorAKey()
        {

            var added = await Controller.AuthorizeSSHKey(LocalController.DefaultAdminUser, SshPublicKey.FromHostKey(adminKey).ToAuthorizedKeyLine());

            Assert.That(added.IsAdded, Is.True, added.Reason);

        }

        private void GiveTheStationAKey()
        {

            Assert.That(Controller.TryAddStationSSHKey(User_Id.Parse("cs001"), SshPublicKey.FromHostKey(cs001Key).ToAuthorizedKeyLine(), "test", null, null, "tests", out _, out var error, out _),
                        Is.True, error);

        }

        private static async Task<Boolean> EndsWithin(SshMessageConnection Connection, TimeSpan Within)
        {

            var until = DateTime.UtcNow + Within;

            while (Connection.IsAlive && DateTime.UtcNow < until)
                await Task.Delay(50);

            return !Connection.IsAlive;

        }

        private static async Task<Boolean> Within(Func<Boolean> Condition, TimeSpan Within)
        {

            var until = DateTime.UtcNow + Within;

            while (!Condition() && DateTime.UtcNow < until)
                await Task.Delay(50);

            return Condition();

        }

        #endregion


        #region ATool_IsAnsweredByTheControllerItself_BesideAStation()

        /// <summary>
        /// An account that may run commissioning is a tool: it is answered by
        /// the controller - its request is for the controller, and not sent on
        /// to a CSMS as a station's is - and known by its account's name. A
        /// station with its SSH key, on the same subsystem at the same time, is
        /// a station of the controller and no tool.
        /// </summary>
        [Test]
        public async Task ATool_IsAnsweredByTheControllerItself_BesideAStation()
        {

            await GiveTheAdministratorAKey();
            GiveTheStationAKey();

            Assert.That(Controller.CommissioningServed, Is.True, "Switched on in the file, and not served.");

            var (station, _,        stationWhy) = await Connect("cs001",                          cs001Key);
            var (tool,    received, toolWhy)    = await Connect(LocalController.DefaultAdminUser.ToString(),  adminKey);

            Assert.That(station, Is.Not.Null, stationWhy);
            Assert.That(tool,    Is.Not.Null, toolWhy);

            var answer = await AskForTheStationPort(tool!, received, "Running", "SSHEnabled");

            Assert.That(answer, Is.Not.Null, "The tool was not answered.");

            Assert.Multiple(() => {

                Assert.That(answer![0].Value<Int32>(), Is.EqualTo(3), $"The controller answered with an error: {answer}");

                var results = (answer[2]?["getVariableResult"] as JArray ?? []).OfType<JObject>().
                                  ToDictionary(result => result["variable"]?["name"]?.Value<String>() ?? "",
                                               result => result["attributeValue"]?.Value<String>());

                Assert.That(results.GetValueOrDefault("Running"),     Is.EqualTo("true"), answer.ToString());
                Assert.That(results.GetValueOrDefault("SSHEnabled"),  Is.EqualTo("true"), answer.ToString());

            });

            Assert.That(await Within(() => Controller.CommissioningTools.Any(), TimeSpan.FromSeconds(10)), Is.True);

            Assert.Multiple(() => {

                Assert.That(Controller.CommissioningTools.Select(id => id.ToString()), Is.EqualTo(new[] { LocalController.DefaultAdminUser.ToString() }),
                            "The tool is not known by its account's name.");

                Assert.That(Controller.StationSSHServer!.ConnectedNetworkingNodeIds.Select(id => id.ToString()), Is.EqualTo(new[] { "cs001" }),
                            "The station is not a station of the controller, or the tool is one.");

                Assert.That(Controller.SSHSessions.Any(session => session.Account == LocalController.DefaultAdminUser && session.Label == LocalController.OCPPOverSSHLabel), Is.True,
                            "The SSH server does not say what the tool's session is for.");

            });

            await tool!.   Close(MessageCloseCode.NormalClosure, "done");
            await station!.Close(MessageCloseCode.NormalClosure, "done");

        }

        #endregion

        #region SwitchedOff_EndsTheTool_AndNotTheStation()

        /// <summary>
        /// Commissioning switched off lets go of every tool at once, and of no
        /// station: the subsystem stays for the stations, and no account is
        /// served it until it is switched on again.
        /// </summary>
        [Test]
        public async Task SwitchedOff_EndsTheTool_AndNotTheStation()
        {

            await GiveTheAdministratorAKey();
            GiveTheStationAKey();

            var (station, _, stationWhy) = await Connect("cs001",                         cs001Key);
            var (tool,    _, toolWhy)    = await Connect(LocalController.DefaultAdminUser.ToString(), adminKey);

            Assert.That(station, Is.Not.Null, stationWhy);
            Assert.That(tool,    Is.Not.Null, toolWhy);

            Assert.That(Controller.TryUpdateCommissioningConfiguration(new JObject(new JProperty("enabled", false)), out var error), Is.True, error);

            Assert.That(await EndsWithin(tool!, TimeSpan.FromSeconds(10)), Is.True, "Switched off, and the tool stayed.");

            Assert.Multiple(() => {
                Assert.That(station!.IsAlive,                Is.True,  "Commissioning switched off, and the station was let go of.");
                Assert.That(Controller.CommissioningServed,  Is.False);
                Assert.That(Controller.OCPPOverSSHServed,    Is.True);
            });

            var (again, _, why) = await Connect(LocalController.DefaultAdminUser.ToString(), adminKey);

            Assert.That(again, Is.Null, "A tool was let in with commissioning switched off.");

            Assert.That(Controller.TryUpdateCommissioningConfiguration(new JObject(new JProperty("enabled", true)), out error), Is.True, error);

            var (back, _, backWhy) = await Connect(LocalController.DefaultAdminUser.ToString(), adminKey);

            Assert.That(back, Is.Not.Null, backWhy);

            await back!.   Close(MessageCloseCode.NormalClosure, "done");
            await station!.Close(MessageCloseCode.NormalClosure, "done");

        }

        #endregion

    }

}
