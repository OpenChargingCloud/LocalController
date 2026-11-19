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
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.SSH;

using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.Logins;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The charging stations that may sign in to the station port, and their login groups, as the
    /// CSMS sees them: StationLoginController and StationLoginGroupController of the local
    /// controller's own Device Model, read and written over the controller's real uplink, as the
    /// page "Charging stations" lists them.
    /// </summary>
    /// <remarks>
    /// The controller 'lc004' listens for stations on 127.0.0.1 with security profile 1, lets
    /// cs001 in by a password, and dials a CSMS of the least kind - see KeyPinsOverOCPPTests.CSMS.
    /// </remarks>
    public class StationLoginsOverOCPPTests
    {

        #region Data

        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

        private const String ThePassword      = "a-password-long-enough-for-ocpp";
        private const String cs001Password    = "cs001-password-long-enough";
        private const String Station          = "StationLoginController";
        private const String Group            = "StationLoginGroupController";

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

            directory = TestControllers.TemporaryDirectory("station-logins-ocpp");
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
                                                    new JProperty("ocpp", new JObject(new JProperty("nodeId", "lc004"))),

                                                    new JProperty("webSocketServer", new JObject(
                                                        new JProperty("enabled",           true),
                                                        new JProperty("address",           "127.0.0.1"),
                                                        new JProperty("port",              port),
                                                        new JProperty("clientAuthentication",  new JArray("httpAuthWithoutTLS"))
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

                                Assert.That(lc.TrySetUplinkPassword("lc004", ThePassword, out var loginError), Is.True, loginError);
                                Assert.That(lc.WebSocketLogins.TrySetPassword("cs001", cs001Password, null, "bay 1", out var error), Is.True, error);

                                return lc;

                            });

            var giveUp = DateTimeOffset.UtcNow + Guard;

            while (DateTimeOffset.UtcNow < giveUp && !(controller.CSMSConnected && csms.Connections == 1))
                await Task.Delay(50);

            Assert.Multiple(() => {
                Assert.That(controller.CSMSConnected,      Is.True, Log);
                Assert.That(csms.Connections,              Is.EqualTo(1));
                Assert.That(controller.OCPPServerRunning,  Is.True, "the station port does not run");
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


        #region (private) Get(Entries) / Set(Entries, Model = null)

        /// <summary>
        /// GetVariables of the given variables: "Component[Instance].Variable" to (status, value).
        /// </summary>
        private async Task<Dictionary<String, (String? Status, String? Value)>> Get(params (String Component, String Instance, String Variable)[] Entries)
        {

            var answer = await csms.Ask(
                                   "GetVariables",
                                   new JObject(
                                       new JProperty("getVariableData", new JArray(
                                           Entries.Select(entry => new JObject(
                                               new JProperty("component", new JObject(new JProperty("name", entry.Component), new JProperty("instance", entry.Instance))),
                                               new JProperty("variable",  new JObject(new JProperty("name", entry.Variable)))
                                           ))
                                       ))
                                   )
                               );

            Assert.That(answer[0].Value<Int32>(), Is.EqualTo(3), answer.ToString(Formatting.None));

            return (answer[2]?["getVariableResult"] as JArray ?? []).
                       OfType<JObject>().
                       ToDictionary(result => $"{result["component"]?["name"]}[{result["component"]?["instance"]}].{result["variable"]?["name"]}",
                                    result => (result["attributeStatus"]?.Value<String>(),
                                               result["attributeValue"]?. Value<String>()));

        }

        /// <summary>
        /// SetVariables of the given variables, in one request: "Component[Instance].Variable" to
        /// (status, reason code, additional info).
        /// </summary>
        private async Task<Dictionary<String, (String? Status, String? Reason, String? Info)>> Set(params (String Component, String Instance, String Variable, String Value)[] Entries)
        {

            var answer = await csms.Ask(
                                   "SetVariables",
                                   new JObject(
                                       new JProperty("setVariableData", new JArray(
                                           Entries.Select(entry => new JObject(
                                               new JProperty("attributeValue",  entry.Value),
                                               new JProperty("component",       new JObject(new JProperty("name", entry.Component), new JProperty("instance", entry.Instance))),
                                               new JProperty("variable",        new JObject(new JProperty("name", entry.Variable)))
                                           ))
                                       ))
                                   )
                               );

            Assert.That(answer[0].Value<Int32>(), Is.EqualTo(3), answer.ToString(Formatting.None));

            return (answer[2]?["setVariableResult"] as JArray ?? []).
                       OfType<JObject>().
                       ToDictionary(result => $"{result["component"]?["name"]}[{result["component"]?["instance"]}].{result["variable"]?["name"]}",
                                    result => (result["attributeStatus"]?.Value<String>(),
                                               result["attributeStatusInfo"]?["reasonCode"]?.    Value<String>(),
                                               result["attributeStatusInfo"]?["additionalInfo"]?.Value<String>()));

        }

        #endregion

        #region (private) SignsIn(Id, Password) / Log / APin()

        /// <summary>
        /// Whether a charging station gets in with the given password, under security profile 1.
        /// </summary>
        private async Task<Boolean> SignsIn(String Id, String Password)
        {

            using var client = new ClientWebSocket();

            client.Options.AddSubProtocol("ocpp2.1");
            client.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Id}:{Password}")));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            try
            {
                await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), timeout.Token);
            }
            catch
            {
                return false;
            }

            try
            {
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
            catch
            { }

            return true;

        }

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


        #region Get_TheCSMSReadsAStationAndItsGroup_ButNoSecret()

        /// <summary>
        /// The CSMS reads a station and its group as the page lists them - and that it has a
        /// password, never the password.
        /// </summary>
        [Test]
        public async Task Get_TheCSMSReadsAStationAndItsGroup_ButNoSecret()
        {

            var read = await Get((Station, "cs001", "Group"),       (Station, "cs001", "Enabled"),  (Station, "cs001", "HasPassword"),
                                 (Station, "cs001", "HasTOTP"),     (Station, "cs001", "Note"),     (Station, "cs001", "Listed"),
                                 (Station, "cs001", "Password"),    (Station, "cs001", "TOTPSecret"),
                                 (Group,   "default", "Members"),   (Group,   "default", "Listed"));

            Assert.Multiple(() => {
                Assert.That(read[$"{Station}[cs001].Group"],        Is.EqualTo(("Accepted", "default")));
                Assert.That(read[$"{Station}[cs001].Enabled"],      Is.EqualTo(("Accepted", "true")));
                Assert.That(read[$"{Station}[cs001].HasPassword"],  Is.EqualTo(("Accepted", "true")));
                Assert.That(read[$"{Station}[cs001].HasTOTP"],      Is.EqualTo(("Accepted", "false")));
                Assert.That(read[$"{Station}[cs001].Note"],         Is.EqualTo(("Accepted", "bay 1")));
                Assert.That(read[$"{Station}[cs001].Listed"],       Is.EqualTo(("Accepted", "true")));
                Assert.That(read[$"{Station}[cs001].Password"],     Is.EqualTo(("Rejected", (String?) null)), "a password was read out");
                Assert.That(read[$"{Station}[cs001].TOTPSecret"],   Is.EqualTo(("Rejected", (String?) null)), "a secret was read out");
                Assert.That(read[$"{Group}[default].Members"],      Is.EqualTo(("Accepted", "1")));
                Assert.That(read[$"{Group}[default].Listed"],       Is.EqualTo(("Accepted", "true")));
            });

        }

        #endregion

        #region Set_ANewStationWithAPassword_SignsIn()

        /// <summary>
        /// A station not listed yet is listed by its password: accepted, read back, and it signs in.
        /// </summary>
        [Test]
        public async Task Set_ANewStationWithAPassword_SignsIn()
        {

            const String password = "cs002-password-long-enough";

            var before  = await SignsIn("cs002", password);
            var set     = await Set((Station, "cs002", "Password", password), (Station, "cs002", "Enabled", "true"));
            var read    = await Get((Station, "cs002", "HasPassword"), (Station, "cs002", "Group"));
            var after   = await SignsIn("cs002", password);

            Assert.Multiple(() => {
                Assert.That(before,                                    Is.False, "a station that is not listed got in");
                Assert.That(set[$"{Station}[cs002].Password"].Status,  Is.EqualTo("Accepted"), set[$"{Station}[cs002].Password"].Info);
                Assert.That(set[$"{Station}[cs002].Enabled"].Status,   Is.EqualTo("Accepted"), set[$"{Station}[cs002].Enabled"].Info);
                Assert.That(read[$"{Station}[cs002].HasPassword"],     Is.EqualTo(("Accepted", "true")));
                Assert.That(read[$"{Station}[cs002].Group"],           Is.EqualTo(("Accepted", "default")));
                Assert.That(after,                                     Is.True, "the station listed over OCPP does not get in");
                Assert.That(Log,                                       Does.Not.Contain(password), "the password was logged");
            });

        }

        #endregion

        #region Set_ANewStationWithoutAWayIn_IsUnknown()

        /// <summary>
        /// A station that is not listed and is given no way in stays unknown.
        /// </summary>
        [Test]
        public async Task Set_ANewStationWithoutAWayIn_IsUnknown()
        {

            var set = await Set((Station, "cs003", "Enabled", "true"));

            Assert.Multiple(() => {
                Assert.That(set[$"{Station}[cs003].Enabled"].Status,  Is.EqualTo("UnknownComponent"));
                Assert.That(controller.WebSocketLogins.TryGet("cs003", out _), Is.False);
            });

        }

        #endregion

        #region Set_APassword_TooShort_IsRejected_AndEmpty_TakesItAway()

        /// <summary>
        /// A password shorter than OCPP asks for is rejected, and the old one still lets the station
        /// in; an empty one takes it away, and the station no longer gets in with it.
        /// </summary>
        [Test]
        public async Task Set_APassword_TooShort_IsRejected_AndEmpty_TakesItAway()
        {

            var tooShort    = await Set((Station, "cs001", "Password", "short"));
            var stillIn     = await SignsIn("cs001", cs001Password);

            var takenAway   = await Set((Station, "cs001", "Password", ""));
            var read        = await Get((Station, "cs001", "HasPassword"));
            var noLongerIn  = await SignsIn("cs001", cs001Password);

            Assert.Multiple(() => {
                Assert.That(tooShort[$"{Station}[cs001].Password"].Status,  Is.EqualTo("Rejected"));
                Assert.That(tooShort[$"{Station}[cs001].Password"].Info,    Does.Contain("at least 16"));
                Assert.That(stillIn,                                        Is.True);
                Assert.That(takenAway[$"{Station}[cs001].Password"].Status, Is.EqualTo("Accepted"), takenAway[$"{Station}[cs001].Password"].Info);
                Assert.That(read[$"{Station}[cs001].HasPassword"],          Is.EqualTo(("Accepted", "false")));
                Assert.That(noLongerIn,                                     Is.False, "a password taken away still lets the station in");
            });

        }

        #endregion

        #region Set_TheTokenParameters_OnlyWithANewSecret()

        /// <summary>
        /// The parameters of the one-time token are refused without a new secret - set alone, they
        /// would have a secret made up that no answer can carry back - and taken with one.
        /// </summary>
        [Test]
        public async Task Set_TheTokenParameters_OnlyWithANewSecret()
        {

            var without  = await Set((Station, "cs001", "TOTPLength", "8"));
            var with     = await Set((Station, "cs001", "TOTPSecret", "a-shared-secret-of-enough-length"), (Station, "cs001", "TOTPLength", "8"),
                                     (Station, "cs001", "TOTPHashAlgorithm", "SHA512"));
            var bound    = await Set((Station, "cs001", "TOTPChannelBinding", "true"));
            var read     = await Get((Station, "cs001", "HasTOTP"), (Station, "cs001", "TOTPLength"), (Station, "cs001", "TOTPHashAlgorithm"),
                                     (Station, "cs001", "TOTPChannelBinding"));

            Assert.Multiple(() => {
                Assert.That(without[$"{Station}[cs001].TOTPLength"].Status,      Is.EqualTo("Rejected"));
                Assert.That(without[$"{Station}[cs001].TOTPLength"].Info,        Does.Contain("together with a new TOTPSecret"));
                Assert.That(with[$"{Station}[cs001].TOTPSecret"].Status,         Is.EqualTo("Accepted"), with[$"{Station}[cs001].TOTPSecret"].Info);
                Assert.That(with[$"{Station}[cs001].TOTPLength"].Status,         Is.EqualTo("Accepted"));
                Assert.That(with[$"{Station}[cs001].TOTPHashAlgorithm"].Status,  Is.EqualTo("Accepted"));
                Assert.That(bound[$"{Station}[cs001].TOTPChannelBinding"].Status, Is.EqualTo("Accepted"), bound[$"{Station}[cs001].TOTPChannelBinding"].Info);
                Assert.That(read[$"{Station}[cs001].HasTOTP"],                   Is.EqualTo(("Accepted", "true")));
                Assert.That(read[$"{Station}[cs001].TOTPLength"],                Is.EqualTo(("Accepted", "8")));
                Assert.That(read[$"{Station}[cs001].TOTPHashAlgorithm"],         Is.EqualTo(("Accepted", "SHA512")));
                Assert.That(read[$"{Station}[cs001].TOTPChannelBinding"],        Is.EqualTo(("Accepted", "true")));
                Assert.That(Log,                                                 Does.Not.Contain("a-shared-secret-of-enough-length"), "the secret was logged");
            });

        }

        #endregion

        #region Set_TheKeyPins_ReplaceTheList()

        /// <summary>
        /// KeyPins is the whole list: two set are two, one set after them is one.
        /// </summary>
        [Test]
        public async Task Set_TheKeyPins_ReplaceTheList()
        {

            var first   = APin();
            var second  = APin();

            var two     = await Set((Station, "cs001", "KeyPins", $"{first.ToHex()},{second.ToHex()}"));
            var readTwo = await Get((Station, "cs001", "KeyPins"));
            var one     = await Set((Station, "cs001", "KeyPins", second.ToHex()));
            var readOne = await Get((Station, "cs001", "KeyPins"));

            Assert.Multiple(() => {
                Assert.That(two[$"{Station}[cs001].KeyPins"].Status,         Is.EqualTo("Accepted"), two[$"{Station}[cs001].KeyPins"].Info);
                Assert.That(readTwo[$"{Station}[cs001].KeyPins"].Value?.Split(','), Is.EquivalentTo(new[] { first.ToHex(), second.ToHex() }));
                Assert.That(one[$"{Station}[cs001].KeyPins"].Status,         Is.EqualTo("Accepted"), one[$"{Station}[cs001].KeyPins"].Info);
                Assert.That(readOne[$"{Station}[cs001].KeyPins"].Value,      Is.EqualTo(second.ToHex()));
            });

        }

        #endregion

        #region Set_AnSSHKey_IsAddedAndTakenAway()

        /// <summary>
        /// An SSH key is added by its authorized_keys line and taken away by its fingerprint; SSHKeys
        /// lists the fingerprints.
        /// </summary>
        [Test]
        public async Task Set_AnSSHKey_IsAddedAndTakenAway()
        {

            var key          = SshHostKey.GenerateEd25519();
            var fingerprint  = SshFingerprint.Sha256(key.PublicKeyBlob);

            var added        = await Set((Station, "cs001", "SSHKeyAdd", SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine()));
            var readAdded    = await Get((Station, "cs001", "SSHKeys"), (Station, "cs001", "SSHKeyAdd"));
            var removed      = await Set((Station, "cs001", "SSHKeyRemove", fingerprint));
            var readRemoved  = await Get((Station, "cs001", "SSHKeys"));

            Assert.Multiple(() => {
                Assert.That(added[$"{Station}[cs001].SSHKeyAdd"].Status,        Is.EqualTo("Accepted"), added[$"{Station}[cs001].SSHKeyAdd"].Info);
                Assert.That(readAdded[$"{Station}[cs001].SSHKeys"],             Is.EqualTo(("Accepted", fingerprint)));
                Assert.That(readAdded[$"{Station}[cs001].SSHKeyAdd"].Status,    Is.EqualTo("Rejected"), "a write-only variable was read");
                Assert.That(removed[$"{Station}[cs001].SSHKeyRemove"].Status,   Is.EqualTo("Accepted"), removed[$"{Station}[cs001].SSHKeyRemove"].Info);
                Assert.That(readRemoved[$"{Station}[cs001].SSHKeys"],           Is.EqualTo(("Accepted", "")));
            });

        }

        #endregion

        #region Set_ANewGroupAndANewStationInIt_InOneRequest()

        /// <summary>
        /// A group is made by its ways in and its profiles, and a station not listed yet goes into it
        /// in the same request - the groups of a request come before its stations.
        /// </summary>
        [Test]
        public async Task Set_ANewGroupAndANewStationInIt_InOneRequest()
        {

            const String password = "cs005-password-long-enough";

            var set   = await Set((Station, "cs005", "Password",          password),
                                  (Station, "cs005", "Group",             "field"),
                                  (Group,   "field", "AuthMethods",       "basic"),
                                  (Group,   "field", "SecurityProfiles",  "1"),
                                  (Group,   "field", "Name",              "Field test"));

            var read  = await Get((Station, "cs005", "Group"), (Group, "field", "Name"), (Group, "field", "AuthMethods"), (Group, "field", "Members"));

            Assert.Multiple(() => {
                foreach (var result in set)
                    Assert.That(result.Value.Status, Is.EqualTo("Accepted"), $"{result.Key}: {result.Value.Info}");
                Assert.That(read[$"{Station}[cs005].Group"],       Is.EqualTo(("Accepted", "field")));
                Assert.That(read[$"{Group}[field].Name"],          Is.EqualTo(("Accepted", "Field test")));
                Assert.That(read[$"{Group}[field].AuthMethods"],   Is.EqualTo(("Accepted", "basic")));
                Assert.That(read[$"{Group}[field].Members"],       Is.EqualTo(("Accepted", "1")));
            });

            Assert.That(await SignsIn("cs005", password), Is.True, "the station in the new group does not get in");

        }

        #endregion

        #region Set_ListedFalse_TakesAGroupAwayOnlyWithoutMembers()

        /// <summary>
        /// A group with members stays; moved out of it, it can be taken away - and its instance goes.
        /// </summary>
        [Test]
        public async Task Set_ListedFalse_TakesAGroupAwayOnlyWithoutMembers()
        {

            var made       = await Set((Group, "field", "AuthMethods", "basic"), (Group, "field", "SecurityProfiles", "1"));
            var moved      = await Set((Station, "cs001", "Group", "field"));
            var refused    = await Set((Group, "field", "Listed", "false"));
            var movedBack  = await Set((Station, "cs001", "Group", "default"));
            var removed    = await Set((Group, "field", "Listed", "false"));
            var read       = await Get((Group, "field", "Listed"));

            Assert.Multiple(() => {
                Assert.That(made[$"{Group}[field].AuthMethods"].Status,  Is.EqualTo("Accepted"), made[$"{Group}[field].AuthMethods"].Info);
                Assert.That(moved[$"{Station}[cs001].Group"].Status,     Is.EqualTo("Accepted"), moved[$"{Station}[cs001].Group"].Info);
                Assert.That(refused[$"{Group}[field].Listed"].Status,    Is.EqualTo("Rejected"));
                Assert.That(movedBack[$"{Station}[cs001].Group"].Status, Is.EqualTo("Accepted"));
                Assert.That(removed[$"{Group}[field].Listed"].Status,    Is.EqualTo("Accepted"), removed[$"{Group}[field].Listed"].Info);
                Assert.That(read[$"{Group}[field].Listed"].Status,       Is.EqualTo("UnknownComponent"), "the instance of a group taken away is still there");
            });

        }

        #endregion

        #region Set_ListedFalse_TakesAStationOffTheList()

        /// <summary>
        /// Listed false takes a station off the list: it no longer gets in, and its instance goes.
        /// </summary>
        [Test]
        public async Task Set_ListedFalse_TakesAStationOffTheList()
        {

            var set    = await Set((Station, "cs001", "Listed", "false"));
            var read   = await Get((Station, "cs001", "Listed"));
            var gotIn  = await SignsIn("cs001", cs001Password);

            Assert.Multiple(() => {
                Assert.That(set[$"{Station}[cs001].Listed"].Status,   Is.EqualTo("Accepted"), set[$"{Station}[cs001].Listed"].Info);
                Assert.That(read[$"{Station}[cs001].Listed"].Status,  Is.EqualTo("UnknownComponent"));
                Assert.That(gotIn,                                    Is.False, "a station taken off the list still gets in");
            });

        }

        #endregion

        #region AStationListedOnThePage_IsInTheDeviceModel()

        /// <summary>
        /// A station listed elsewhere - the page, the command line - is an instance the CSMS reads at
        /// once.
        /// </summary>
        [Test]
        public async Task AStationListedOnThePage_IsInTheDeviceModel()
        {

            Assert.That(controller.WebSocketLogins.TrySetPassword("cs009", "cs009-password-long-enough", null, null, out var error), Is.True, error);

            var read = await Get((Station, "cs009", "HasPassword"));

            Assert.That(read[$"{Station}[cs009].HasPassword"], Is.EqualTo(("Accepted", "true")));

        }

        #endregion

    }

}
