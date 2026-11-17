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
using System.Net.Sockets;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The port the charging stations connect to as the CSMS sees it: OCPPServerController of the
    /// local controller's own Device Model, read and written over the controller's real uplink, as
    /// the page "Charging stations" reads and writes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The controller 'lc003' listens for stations on 127.0.0.1 with security profile 1 alone and
    /// has no server certificate, and dials a CSMS of the least kind - see KeyPinsOverOCPPTests.CSMS.
    /// </para>
    /// <para>
    /// What a CSMS may set is everything the page may set (Achim, 2026-11-17): it may switch off
    /// the port, and what waits for a restart is written down and answered "RebootRequired".
    /// </para>
    /// </remarks>
    public class StationServerOverOCPPTests
    {

        #region Data

        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

        private const String ThePassword = "a-password-long-enough-for-ocpp";

        private String                          directory   = default!;
        private KeyPinsOverOCPPTests.CSMS       csms        = default!;
        private LocalController                 controller  = default!;
        private UInt16                          port;

        private readonly ConcurrentQueue<String> said = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task StartAControllerAndACSMS()
        {

            directory = TestControllers.TemporaryDirectory("station-server-ocpp");
            Directory.CreateDirectory(directory);

            said.Clear();

            var csmsPort  = TestPorts.Free();

            csms          = new KeyPinsOverOCPPTests.CSMS(csmsPort);

            controller    = await TestPorts.StartedOnFreshPorts(() => {

                                port      = TestPorts.Free();

                                var lc    = TestControllers.New(
                                                directory,
                                                new JObject(

                                                    new JProperty("nts",  new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                                    new JProperty("ocpp", new JObject(new JProperty("nodeId", "lc003"))),

                                                    new JProperty("ocppServer", new JObject(
                                                        new JProperty("enabled",           true),
                                                        new JProperty("address",           "127.0.0.1"),
                                                        new JProperty("port",              port),
                                                        new JProperty("securityProfiles",  new JArray(1))
                                                    )),

                                                    new JProperty("csms", new JObject(
                                                        new JProperty("enabled",                true),
                                                        new JProperty("url",                    $"ws://127.0.0.1:{csmsPort}"),
                                                        new JProperty("securityProfile",        1),
                                                        new JProperty("networkingMode",         "standard"),
                                                        new JProperty("reconnectInitialDelay",  1),
                                                        new JProperty("reconnectMaxDelay",      2)
                                                    ))

                                                )
                                            );

                                lc.Log.OnLogged += entry => said.Enqueue(entry.Message);

                                Assert.That(lc.TrySetUplinkPassword("lc003", ThePassword, out var loginError), Is.True, loginError);

                                return lc;

                            });

            var giveUp = DateTimeOffset.UtcNow + Guard;

            while (DateTimeOffset.UtcNow < giveUp && !(controller.CSMSConnected && csms.Connections == 1))
                await Task.Delay(50);

            Assert.Multiple(() => {
                Assert.That(controller.CSMSConnected,      Is.True, Log);
                Assert.That(csms.Connections,              Is.EqualTo(1));
                Assert.That(controller.OCPPServerRunning,  Is.True, "the station port does not run");
                Assert.That(controller.OCPPServerTLS,      Is.False);
            });

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


        #region (private) Get(Variables) / Set(Values, Model = null)

        /// <summary>
        /// GetVariables of the given variables of OCPPServerController, from the controller itself:
        /// variable name to (status, value).
        /// </summary>
        private async Task<Dictionary<String, (String? Status, String? Value)>> Get(params String[] Variables)
        {

            var answer = await csms.Ask(
                                   "GetVariables",
                                   new JObject(
                                       new JProperty("getVariableData", new JArray(
                                           Variables.Select(variable => new JObject(
                                               new JProperty("component", new JObject(new JProperty("name", "OCPPServerController"))),
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
        /// SetVariables of the given variables of OCPPServerController to the given values, in one
        /// request: variable name to (status, reason code, additional info).
        /// </summary>
        private async Task<Dictionary<String, (String? Status, String? Reason, String? Info)>> Set(IEnumerable<(String Variable, String Value)>  Values,
                                                                                                  String?                                       Model   = null)
        {

            var payload = new JObject(
                              new JProperty("setVariableData", new JArray(
                                  Values.Select(value => new JObject(
                                      new JProperty("attributeValue",  value.Value),
                                      new JProperty("component",       new JObject(new JProperty("name", "OCPPServerController"))),
                                      new JProperty("variable",        new JObject(new JProperty("name", value.Variable)))
                                  ))
                              ))
                          );

            if (Model is not null)
                payload.Add("dataConsistencyModel", Model);

            var answer = await csms.Ask("SetVariables", payload);

            Assert.That(answer[0].Value<Int32>(), Is.EqualTo(3), answer.ToString(Formatting.None));

            return (answer[2]?["setVariableResult"] as JArray ?? []).
                       OfType<JObject>().
                       ToDictionary(result => result["variable"]?["name"]?.Value<String>() ?? "",
                                    result => (result["attributeStatus"]?.Value<String>(),
                                               result["attributeStatusInfo"]?["reasonCode"]?.    Value<String>(),
                                               result["attributeStatusInfo"]?["additionalInfo"]?.Value<String>()));

        }

        private Task<Dictionary<String, (String? Status, String? Reason, String? Info)>> Set(String Variable, String Value, String? Model = null)
            => Set([ (Variable, Value) ], Model);

        #endregion

        #region (private) InTheFile / Log / Listens(Port)

        /// <summary>
        /// The section "ocppServer" as the configuration file has it now.
        /// </summary>
        private JObject InTheFile
            => JObject.Parse(File.ReadAllText(controller.ConfigFile.Path))["ocppServer"] as JObject ?? [];

        /// <summary>
        /// What the controller said, for the message of an assertion.
        /// </summary>
        private String Log
            => String.Join(Environment.NewLine, said);

        /// <summary>
        /// Whether something listens on the given TCP port of 127.0.0.1.
        /// </summary>
        private static async Task<Boolean> Listens(UInt16 Port)
        {

            using var client = new TcpClient();

            try
            {
                await client.ConnectAsync("127.0.0.1", Port).WaitAsync(TimeSpan.FromSeconds(5));
                return true;
            }
            catch
            {
                return false;
            }

        }

        #endregion


        #region GetVariables_TheCSMSReadsTheStationPortAsItRuns()

        /// <summary>
        /// The CSMS reads the station port as the file and the page have it, and what it is doing.
        /// </summary>
        [Test]
        public async Task GetVariables_TheCSMSReadsTheStationPortAsItRuns()
        {

            var read = await Get("Enabled", "Address", "Port", "SecurityProfiles", "HTTP3", "MinimumTLSVersion",
                                 "SSHEnabled", "LogMessages", "Running", "TLS", "URL", "WaitingForARestart");

            Assert.Multiple(() => {
                Assert.That(read["Enabled"],            Is.EqualTo(("Accepted", "true")));
                Assert.That(read["Address"],            Is.EqualTo(("Accepted", "127.0.0.1")));
                Assert.That(read["Port"],               Is.EqualTo(("Accepted", port.ToString())));
                Assert.That(read["SecurityProfiles"],   Is.EqualTo(("Accepted", "1")));
                Assert.That(read["HTTP3"],              Is.EqualTo(("Accepted", "false")));
                Assert.That(read["MinimumTLSVersion"],  Is.EqualTo(("Accepted", "1.2")));
                Assert.That(read["SSHEnabled"],         Is.EqualTo(("Accepted", "false")));
                Assert.That(read["LogMessages"],        Is.EqualTo(("Accepted", "false")));
                Assert.That(read["Running"],            Is.EqualTo(("Accepted", "true")));
                Assert.That(read["TLS"],                Is.EqualTo(("Accepted", "false")));
                Assert.That(read["URL"].Value,          Does.StartWith("ws://").And.Contain($":{port}"));
                Assert.That(read["WaitingForARestart"], Is.EqualTo(("Accepted", "")));
            });

        }

        #endregion

        #region SetVariables_WhatTakesEffectAtOnce_IsAcceptedAndWrittenDown()

        /// <summary>
        /// Security profiles, the names the port is reached under and the logging take effect at
        /// once: accepted, written to the file, and read back as set - by the CSMS and by the page.
        /// </summary>
        [Test]
        public async Task SetVariables_WhatTakesEffectAtOnce_IsAcceptedAndWrittenDown()
        {

            var set   = await Set([ ("SecurityProfiles", "1,2"), ("ReachableAs", "lc.example,192.0.2.1"), ("LogMessages", "true") ]);
            var read  = await Get("SecurityProfiles", "ReachableAs", "LogMessages", "WaitingForARestart");
            var page  = controller.OCPPServerConfigurationJSON();

            Assert.Multiple(() => {

                Assert.That(set["SecurityProfiles"].Status,  Is.EqualTo("Accepted"), set["SecurityProfiles"].Info);
                Assert.That(set["ReachableAs"].Status,       Is.EqualTo("Accepted"), set["ReachableAs"].Info);
                Assert.That(set["LogMessages"].Status,       Is.EqualTo("Accepted"), set["LogMessages"].Info);

                Assert.That(read["SecurityProfiles"].Value,  Is.EqualTo("1,2"));
                Assert.That(read["ReachableAs"].Value,       Is.EqualTo("lc.example,192.0.2.1"));
                Assert.That(read["LogMessages"].Value,       Is.EqualTo("true"));
                Assert.That(read["WaitingForARestart"].Value, Is.EqualTo(""));

                Assert.That(InTheFile["securityProfiles"]?.Values<Int32>(),  Is.EqualTo(new[] { 1, 2 }));
                Assert.That(InTheFile["reachableAs"]?.Values<String>(),      Is.EqualTo(new[] { "lc.example", "192.0.2.1" }));
                Assert.That(InTheFile["logging"]?["messages"]?.Value<Boolean>(), Is.True);

                Assert.That(page["securityProfiles"]?.Values<Int32>(),       Is.EqualTo(new[] { 1, 2 }), "the page reads something else");
                Assert.That(Log, Does.Contain("Over OCPP, OCPPServerController.SecurityProfiles, OCPPServerController.ReachableAs, OCPPServerController.LogMessages were set."));

            });

        }

        #endregion

        #region SetVariables_WhatWaitsForARestart_IsRebootRequired()

        /// <summary>
        /// A new port and a new number of connections are written down and wait for the next start:
        /// "RebootRequired", WaitingForARestart names them, and the port still listens where it did.
        /// </summary>
        [Test]
        public async Task SetVariables_WhatWaitsForARestart_IsRebootRequired()
        {

            var newPort  = TestPorts.Free();

            var set      = await Set([ ("Port", newPort.ToString()), ("MaxConnections", "100") ]);
            var read     = await Get("Port", "MaxConnections", "WaitingForARestart");
            var listens  = await Listens(port);

            Assert.Multiple(() => {

                Assert.That(set["Port"].Status,            Is.EqualTo("RebootRequired"), set["Port"].Info);
                Assert.That(set["MaxConnections"].Status,  Is.EqualTo("RebootRequired"), set["MaxConnections"].Info);

                Assert.That(read["Port"].Value,            Is.EqualTo(newPort.ToString()));
                Assert.That(read["MaxConnections"].Value,  Is.EqualTo("100"));
                Assert.That(read["WaitingForARestart"].Value?.Split(','), Is.EquivalentTo(new[] { "Port", "MaxConnections" }));

                Assert.That(InTheFile["port"]?.Value<Int32>(),            Is.EqualTo(newPort));
                Assert.That(InTheFile["maxConnections"]?.Value<Int32>(),  Is.EqualTo(100));

                Assert.That(listens,                       Is.True, "the port stopped listening before a restart");

            });

        }

        #endregion

        #region SetVariables_BackToWhatItRunsWith_IsAccepted()

        /// <summary>
        /// A port set back to the one the server listens on waits for nothing: accepted, and nothing
        /// waits for a restart any more.
        /// </summary>
        [Test]
        public async Task SetVariables_BackToWhatItRunsWith_IsAccepted()
        {

            var away  = await Set("Port", TestPorts.Free().ToString());
            var back  = await Set("Port", port.ToString());
            var read  = await Get("WaitingForARestart");

            Assert.Multiple(() => {
                Assert.That(away["Port"].Status,               Is.EqualTo("RebootRequired"), away["Port"].Info);
                Assert.That(back["Port"].Status,               Is.EqualTo("Accepted"),       back["Port"].Info);
                Assert.That(read["WaitingForARestart"].Value,  Is.EqualTo(""));
            });

        }

        #endregion

        #region SetVariables_TLSProfilesAloneWithoutAKey_AreRejected()

        /// <summary>
        /// Security profiles 2 and 3 alone on a port without a server certificate or key would be a
        /// port no station can use: rejected as the page refuses it, and the file keeps profile 1.
        /// </summary>
        [Test]
        public async Task SetVariables_TLSProfilesAloneWithoutAKey_AreRejected()
        {

            var set = await Set("SecurityProfiles", "2,3");

            Assert.Multiple(() => {
                Assert.That(set["SecurityProfiles"].Status,  Is.EqualTo("Rejected"));
                Assert.That(set["SecurityProfiles"].Reason,  Is.EqualTo("InvalidValue"));
                Assert.That(set["SecurityProfiles"].Info,    Does.Contain("Security profiles 2 and 3 both need TLS"));
                Assert.That(InTheFile["securityProfiles"]?.Values<Int32>(), Is.EqualTo(new[] { 1 }));
            });

        }

        #endregion

        #region SetVariables_OneRefusal_KeepsTheOthersOutOnlyWhereTheRequestSaysSo(Model)

        /// <summary>
        /// A request of independent entries - the default - takes what can be had where one of them
        /// cannot; one that is ACID takes none of them.
        /// </summary>
        [TestCase(null)]
        [TestCase("ACID")]
        public async Task SetVariables_OneRefusal_KeepsTheOthersOutOnlyWhereTheRequestSaysSo(String? Model)
        {

            var set = await Set([ ("SecurityProfiles", "3"), ("LogMessages", "true") ], Model);

            Assert.Multiple(() => {

                Assert.That(set["SecurityProfiles"].Status,  Is.EqualTo("Rejected"));

                if (Model is null)
                {
                    Assert.That(set["LogMessages"].Status,                        Is.EqualTo("Accepted"), set["LogMessages"].Info);
                    Assert.That(InTheFile["logging"]?["messages"]?.Value<Boolean>(), Is.True);
                }
                else
                {
                    Assert.That(set["LogMessages"].Status,                        Is.EqualTo("Rejected"));
                    Assert.That(InTheFile["logging"]?["messages"]?.Value<Boolean>(), Is.Not.True);
                }

            });

        }

        #endregion

        #region SetVariables_AValueThatIsNone_IsRejected()

        /// <summary>
        /// A value outside a variable's limits or values, and a variable that is only read, are
        /// rejected before anything is written.
        /// </summary>
        [Test]
        public async Task SetVariables_AValueThatIsNone_IsRejected()
        {

            var set = await Set([ ("Port", "70000"), ("SecurityProfiles", "1,4"), ("Running", "false"), ("Address", "") ]);

            Assert.Multiple(() => {
                Assert.That(set["Port"].Status,              Is.EqualTo("Rejected"));
                Assert.That(set["SecurityProfiles"].Status,  Is.EqualTo("Rejected"));
                Assert.That(set["Running"].Status,           Is.EqualTo("Rejected"));
                Assert.That(set["Address"].Status,           Is.EqualTo("Rejected"));
                Assert.That(InTheFile["port"]?.Value<Int32>(), Is.EqualTo(port));
                Assert.That(controller.OCPPServerRunning,    Is.True);
            });

        }

        #endregion

        #region SetVariables_ContentsLoggedOnlyWithAWindow()

        /// <summary>
        /// The contents of messages are logged only for a window: switched on without its end it is
        /// rejected, with it accepted.
        /// </summary>
        [Test]
        public async Task SetVariables_ContentsLoggedOnlyWithAWindow()
        {

            var without  = await Set("LogPayloads", "true");
            var until    = DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var with     = await Set([ ("LogPayloads", "true"), ("LogPayloadsUntil", until) ]);
            var read     = await Get("LogPayloads", "LogPayloadsUntil");

            Assert.Multiple(() => {
                Assert.That(without["LogPayloads"].Status,       Is.EqualTo("Rejected"));
                Assert.That(without["LogPayloads"].Info,         Does.Contain("payloadsUntil"));
                Assert.That(with["LogPayloads"].Status,          Is.EqualTo("Accepted"), with["LogPayloads"].Info);
                Assert.That(with["LogPayloadsUntil"].Status,     Is.EqualTo("Accepted"), with["LogPayloadsUntil"].Info);
                Assert.That(read["LogPayloads"].Value,           Is.EqualTo("true"));
                Assert.That(DateTimeOffset.Parse(read["LogPayloadsUntil"].Value ?? ""), Is.EqualTo(DateTimeOffset.Parse(until)));
            });

        }

        #endregion

        #region SwitchingOff_WaitsForTheAnswer()

        /// <summary>
        /// What switches the port off waits for what it is given - the answer to the request that
        /// asked for it, which a connection of this port would take with it: written down at once,
        /// listening until then, and no longer after.
        /// </summary>
        [Test]
        public async Task SwitchingOff_WaitsForTheAnswer()
        {

            var answered  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var taken     = controller.TryUpdateOCPPServerConfiguration(new JObject(new JProperty("enabled", false)),
                                                                        out var error,
                                                                        out _,
                                                                        answered.Task);

            await Task.Delay(500);

            var runningBefore  = controller.OCPPServerRunning;
            var listensBefore  = await Listens(port);
            var fileBefore     = InTheFile["enabled"]?.Value<Boolean>();

            answered.SetResult();

            var giveUp = DateTimeOffset.UtcNow + Guard;

            while (DateTimeOffset.UtcNow < giveUp && controller.OCPPServerRunning)
                await Task.Delay(50);

            var listensAfter = await Listens(port);

            Assert.Multiple(() => {
                Assert.That(taken,                         Is.True,  error);
                Assert.That(fileBefore,                    Is.False, "not written down at once");
                Assert.That(runningBefore,                 Is.True,  "switched off before the answer was out");
                Assert.That(listensBefore,                 Is.True,  "switched off before the answer was out");
                Assert.That(controller.OCPPServerRunning,  Is.False, Log);
                Assert.That(listensAfter,                  Is.False, "still listening after the answer was out");
            });

        }

        #endregion

        #region SetVariables_TheCSMSSwitchesThePortOffAndOnAgain()

        /// <summary>
        /// The CSMS may switch the station port off: it is answered first - the port still runs
        /// while the change is said to be set, which is while the request is being answered - then
        /// the port stops listening, and the file says so. Switched on again, it listens again.
        /// </summary>
        [Test]
        public async Task SetVariables_TheCSMSSwitchesThePortOffAndOnAgain()
        {

            Boolean? runningWhenSet = null;

            controller.Log.OnLogged += entry => {
                if (entry.Message.Contains("OCPPServerController.Enabled was set"))
                    runningWhenSet ??= controller.OCPPServerRunning;
            };

            var off = await Set("Enabled", "false");

            Assert.That(off["Enabled"].Status, Is.EqualTo("Accepted"), off["Enabled"].Info);

            var giveUp = DateTimeOffset.UtcNow + Guard;

            while (DateTimeOffset.UtcNow < giveUp && controller.OCPPServerRunning)
                await Task.Delay(50);

            var listensOff = await Listens(port);

            Assert.Multiple(() => {
                Assert.That(runningWhenSet,                          Is.True,  "switched off before the answer was out");
                Assert.That(controller.OCPPServerRunning,           Is.False, Log);
                Assert.That(listensOff,                              Is.False, "the port still listens");
                Assert.That(InTheFile["enabled"]?.Value<Boolean>(),  Is.False);
                Assert.That(controller.CSMSConnected,                Is.True,  "the line up to the CSMS went with the port");
            });

            var on = await Set("Enabled", "true");

            Assert.That(on["Enabled"].Status, Is.EqualTo("Accepted"), on["Enabled"].Info);

            giveUp = DateTimeOffset.UtcNow + Guard;

            while (DateTimeOffset.UtcNow < giveUp && !controller.OCPPServerRunning)
                await Task.Delay(50);

            var listensOn = await Listens(port);

            Assert.Multiple(() => {
                Assert.That(controller.OCPPServerRunning,  Is.True, Log);
                Assert.That(listensOn,                     Is.True, "the port does not listen again");
            });

        }

        #endregion

    }

}
