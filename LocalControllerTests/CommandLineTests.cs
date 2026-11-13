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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.CLI;
using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.LocalController.CommandLine;
using cloud.charging.open.LocalController.Configuration;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The local controller's own command line: handed to whoever signs in over
    /// SSH by the controller itself, whichever program runs it, with stationKeys
    /// and openADR beside the node's commands - and each of them asking what
    /// the web interface asks of the same account.
    /// </summary>
    public class CommandLineTests
    {

        #region Data

        private const String  VENName  = "lc-paradies";
        private const String  Secret   = "sQ3-the-secret-the-grid-operator-gave";

        private String            directory   = default!;
        private LocalController?  controller;
        private FakeVTN?          vtn;
        private readonly List<String> said    = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("cli");
            Directory.CreateDirectory(directory);
            lock (said)
                said.Clear();
        }

        [TearDown]
        public async Task TakeItAwayAgain()
        {

            if (controller is not null)
                await controller.DisposeAsync();

            controller = null;

            if (vtn is not null)
                await vtn.DisposeAsync();

            vtn = null;

            TestControllers.Remove(directory);

        }

        #endregion


        #region (private) AController(Configuration = null) / AStartedController(Configuration = null)

        /// <summary>
        /// A local controller off the network: made, and not started.
        /// </summary>
        private LocalController AController(JObject? Configuration = null)
        {

            controller = TestControllers.New(directory, Configuration ?? TestControllers.Offline);

            controller.Log.OnLogged += entry => { lock (said) said.Add(entry.Message); };

            return controller;

        }

        /// <summary>
        /// A local controller that is started, and so has its accounts.
        /// </summary>
        private async Task<LocalController> AStartedController(JObject? Configuration = null)

            => await TestPorts.StartedOnFreshPorts(() => AController(Configuration));

        #endregion

        #region (private) OverSSHAs(Name, Role)

        /// <summary>
        /// The controller's command line as an account of the given name sees
        /// it over SSH: made for the purpose and put in the group of the given
        /// role.
        /// </summary>
        private async Task<NodeCLI> OverSSHAs(String  Name,
                                              String  Role)
        {

            Assert.That(controller!.ExtAPI.TryGetOrganization(Organization_Id.Parse("LocalController"), out var organization) &&
                        organization is Organization, Is.True, "the controller's organization is not there");

            var account = await controller.ExtAPI.CreateUser(
                                    User_Id.Parse(Name),
                                    I18NString.Create(Languages.en, Name),
                                    SimpleEMailAddress.Parse($"{Name}@localhost"),
                                    User2OrganizationEdgeLabel.IsMember,
                                    (Organization) organization!,
                                    SkipDefaultNotifications:  true,
                                    SkipNewUserEMail:          true,
                                    SkipNewUserNotifications:  true,
                                    AcceptedEULA:              DateTimeOffset.UtcNow.AddSeconds(-1),
                                    IsAuthenticated:           true
                                );

            Assert.That(account,                                                             Is.Not.Null, $"the account '{Name}' was not made");
            Assert.That(controller.ExtAPI.TryGetUser(User_Id.Parse(Name), out var stored),     Is.True);
            Assert.That(controller.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(Role), out var group), Is.True, $"the controller has no group '{Role}'");
            Assert.That((await controller.ExtAPI.AddUserToUserGroup((User) stored!, User2UserGroupEdgeLabel.IsMember, (UserGroup) group!)).IsSuccess,
                        Is.True, $"'{Name}' could not be put in '{Role}'");

            var session = new SshSessionInfo("42",
                                             Name,
                                             "publickey",
                                             null,
                                             IPSocket.Parse("192.0.2.7:50000"),
                                             IPSocket.Parse("10.10.0.1:22"),
                                             null,
                                             SshSessionRestrictions.None);

            return controller.CommandLines!(new VT100Terminal((bytes, cancellationToken) => ValueTask.CompletedTask),
                                            new CLICaller(stored!, session));

        }

        #endregion

        #region (private static) LineOf(Key) / FingerprintOf(Key)

        private static String LineOf(ISshHostKey Key)

            => SshPublicKey.FromHostKey(Key).ToAuthorizedKeyLine();

        private static String FingerprintOf(ISshHostKey Key)

            => SshFingerprint.Sha256(Key.PublicKeyBlob);

        #endregion


        #region OverSSH_TheControllersCommandLineIsServed()

        /// <summary>
        /// The controller sets what somebody signed in over SSH gets by itself:
        /// its own command line, with stationKeys and openADR found in it - not
        /// only where a program sets it.
        /// </summary>
        [Test]
        public async Task OverSSH_TheControllersCommandLineIsServed()
        {

            var lc = AController();

            Assert.That(lc.CommandLines, Is.Not.Null, "the controller hands no command line to SSH");

            await using var terminal = new VT100Terminal((bytes, cancellationToken) => ValueTask.CompletedTask);
            using       var cli      = lc.CommandLines!(terminal, CLICaller.Console);

            var keys    = await cli.Execute(StationKeysCommand.CommandName);
            var openADR = await cli.Execute(OpenADRCommand.CommandName);

            Assert.Multiple(() => {
                Assert.That(cli,                    Is.InstanceOf<ControllerCLI>());
                Assert.That(((ControllerCLI) cli).Controller, Is.SameAs(lc));
                Assert.That(keys,                   Has.None.StartsWith("Unknown command"));
                Assert.That(keys,                   Has.Some.EqualTo("No charging station of this local controller has an SSH key."));
                Assert.That(openADR,                Has.None.StartsWith("Unknown command"));
                Assert.That(openADR.First(),        Does.StartWith("This local controller asks no OpenADR VTN."));
            });

        }

        #endregion

        #region StationKeys_AreGivenListedAndTakenAway_AtTheConsole()

        /// <summary>
        /// A key given to a station that is not listed yet lists it, a second
        /// key is given beside the first, a key is listed with who gave it, and
        /// one is taken away by the beginning of its fingerprint.
        /// </summary>
        [Test]
        public async Task StationKeys_AreGivenListedAndTakenAway_AtTheConsole()
        {

            var lc      = AController();
            using var cli = new ControllerCLI(lc);

            var first   = SshHostKey.GenerateEd25519();
            var second  = SshHostKey.GenerateEd25519();

            var added   = await cli.Execute($"stationKeys cs001 add {LineOf(first)}");
            var again   = await cli.Execute($"stationKeys cs001 add {LineOf(second)}");
            var listed  = await cli.Execute("stationKeys cs001");
            var every   = await cli.Execute("stationKeys");

            Assert.Multiple(() => {

                Assert.That(added.First(),   Is.EqualTo($"'cs001' is a charging station of this local controller now, in the group 'default', and signs in over SSH with the key {FingerprintOf(first)}."));
                Assert.That(again.First(),   Is.EqualTo($"'cs001' signs in over SSH with the key {FingerprintOf(second)} now."));
                Assert.That(added,           Has.Some.EqualTo("OCPP over SSH is switched off: no charging station signs in over SSH."));

                Assert.That(listed,          Has.Some.StartsWith(FingerprintOf(first)). And.Some.Contains(" by the console"));
                Assert.That(listed,          Has.Some.StartsWith(FingerprintOf(second)));
                Assert.That(every.First(),   Is.EqualTo("cs001:"));
                Assert.That(every,           Has.Some.StartsWith("  " + FingerprintOf(first)));

                lock (said)
                    Assert.That(said,        Has.Some.EqualTo($"Somebody at the command line gave the charging station 'cs001' the SSH key {FingerprintOf(first)}."));

            });

            var removed = await cli.Execute($"stationKeys cs001 remove {FingerprintOf(first)["SHA256:".Length..][..10]}");

            Assert.Multiple(() => {
                Assert.That(removed,                                                Is.EqualTo(new[] { $"The key {FingerprintOf(first)} lets 'cs001' in no more." }));
                Assert.That(lc.StationLogins.TryGet("cs001", out var login),         Is.True);
                Assert.That(login!.Keys.Select(key => key.Fingerprint),             Is.EqualTo(new[] { FingerprintOf(second) }));
            });

        }

        #endregion

        #region StationKeys_SayWhatIsWrong()

        /// <summary>
        /// An unknown station, a key it does not have, an empty line and a verb
        /// that is none are each answered with what is wrong - and nothing changes.
        /// </summary>
        [Test]
        public async Task StationKeys_SayWhatIsWrong()
        {

            var lc        = AController();
            using var cli = new ControllerCLI(lc);

            var key       = SshHostKey.GenerateEd25519();
            await cli.Execute($"stationKeys cs001 add {LineOf(key)}");

            var unknown        = await cli.Execute("stationKeys cs404");
            var unknownRemove  = await cli.Execute("stationKeys cs404 remove SHA256:AAAA");
            var noSuchKey      = await cli.Execute("stationKeys cs001 remove SHA256:AAAA");
            var noFingerprint  = await cli.Execute("stationKeys cs001 remove SHA256:");
            var notAKey        = await cli.Execute("stationKeys cs001 add not-a-key");
            var noVerb         = await cli.Execute("stationKeys cs001 rotate it");

            Assert.Multiple(() => {
                Assert.That(unknown,                Is.EqualTo(new[] { "'cs404' is no charging station of this local controller." }));
                Assert.That(unknownRemove,          Is.EqualTo(new[] { "'cs404' is no charging station of this local controller." }));
                Assert.That(noSuchKey,              Is.EqualTo(new[] { "'cs001' has no key SHA256:AAAA." }));
                Assert.That(noFingerprint,          Is.EqualTo(new[] { "'cs001' has no key SHA256:." }));
                Assert.That(notAKey.Single(),       Does.StartWith("The key was not given: "));
                Assert.That(noVerb.Single(),        Does.StartWith("Usage: stationKeys "));
            });

            Assert.That(lc.StationLogins.TryGet("cs001", out var login) && login.Keys.Count() == 1, Is.True, "a key changed");

        }

        #endregion

        #region StationKeys_OverSSH_AskWhatTheWebInterfaceAsks()

        /// <summary>
        /// Over SSH a viewer may list the stations' keys and may not give one; a
        /// CPO may, and the log names who did it.
        /// </summary>
        [Test]
        public async Task StationKeys_OverSSH_AskWhatTheWebInterfaceAsks()
        {

            await AStartedController();

            var key        = SshHostKey.GenerateEd25519();

            using var viewer = await OverSSHAs("vera", "viewer");
            using var cpo    = await OverSSHAs("cleo", "cpo");

            var looked     = await viewer.Execute("stationKeys");
            var refused    = await viewer.Execute($"stationKeys cs001 add {LineOf(key)}");
            var given      = await cpo.   Execute($"stationKeys cs001 add {LineOf(key)}");

            Assert.Multiple(() => {

                Assert.That(looked,          Has.Some.EqualTo("No charging station of this local controller has an SSH key."));
                Assert.That(refused.Single(), Does.StartWith("'vera' may not do that here: it needs the ").And.Contain("cpo"));
                Assert.That(given.First(),    Does.StartWith("'cs001' is a charging station of this local controller now"));
                Assert.That(controller!.StationLogins.TryGet("cs001", out var login) && login.Keys.Single().CreatedBy == "cleo", Is.True);

                lock (said)
                    Assert.That(said,         Has.Some.EqualTo($"'cleo' at the command line over SSH gave the charging station 'cs001' the SSH key {FingerprintOf(key)}."));

            });

        }

        #endregion


        #region CSMSKeys_AreSetAndShown_AtTheConsole()

        /// <summary>
        /// How the controller believes its CSMS, at the console: by its certificate to begin with, then
        /// key-pinned by trust on first use, its probation, a key pin set and a signer of pin sets - all
        /// written down, and shown - and what is no key pin, no setting or no key refused.
        /// </summary>
        [Test]
        public async Task CSMSKeys_AreSetAndShown_AtTheConsole()
        {

            var lc          = AController();
            using var cli   = new ControllerCLI(lc);

            Assert.That(lc.TryUpdateCSMSConfiguration(new JObject(new JProperty("url", "wss://csms.test/lc001")), out var urlError), Is.True, urlError);

            using var key   = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var pin         = KeyPin.FromKey(key).ToHex();
            var point       = key.ExportParameters(false).Q;
            var signer      = Convert.ToBase64String([ 4, .. point.X!, .. point.Y! ]);

            var before      = await cli.Execute("csmsKeys");
            var tofu        = await cli.Execute("csmsKeys tofu on");
            var probation   = await cli.Execute("csmsKeys probation 120");
            var pinned      = await cli.Execute($"csmsKeys pins {pin}");
            var signed      = await cli.Execute($"csmsKeys signer add {signer} The CSMS of the site");
            var notAPin     = await cli.Execute("csmsKeys pins 1234");
            var notTrust    = await cli.Execute("csmsKeys tofu maybe");
            var notAKey     = await cli.Execute("csmsKeys signer add nokey Somebody");
            var unknown     = await cli.Execute("csmsKeys rotate");
            var written     = File.ReadAllText(lc.CSMSPins.Path);

            Assert.Multiple(() => {

                Assert.That(before.First(),        Is.EqualTo("The CSMS is believed by its certificate, as a CA vouches for it."));
                Assert.That(tofu.First(),          Is.EqualTo("The CSMS is believed by the key pin of its key, and by nothing else of its certificate."));
                Assert.That(probation,             Has.Some.Contains("probation 120 s"));
                Assert.That(pinned,                Has.Some.StartWith($"key pins          {pin}"));
                Assert.That(signed,                Has.Some.Contains(signer).And.Contains("The CSMS of the site"));

                Assert.That(notAPin.Single(),      Does.StartWith("Nothing was changed: ").And.Contain("is no key pin"));
                Assert.That(notTrust.Single(),     Does.StartWith("Nothing was changed: ").And.Contain("trust on first use"));
                Assert.That(notAKey.Single(),      Does.StartWith("Nothing was changed: "));
                Assert.That(unknown.Single(),      Does.StartWith("Usage: csmsKeys "));

                Assert.That(written,               Does.Contain(pin).And.Contain(signer).And.Contain("\"trustOnFirstUse\": \"Key\"").And.Contain("\"serverKeyPinProbationTime\": 120"));

            });

        }

        #endregion

        #region ServerKeys_AreMadeShownAndSwitched_AtTheConsole()

        /// <summary>
        /// The server key the stations pin, at the console: none to begin with,
        /// then made and shown with its pin, a next one announced beside it,
        /// and switched to - and a second server key in the place of the first
        /// refused.
        /// </summary>
        [Test]
        public async Task ServerKeys_AreMadeShownAndSwitched_AtTheConsole()
        {

            var lc        = AController();
            using var cli = new ControllerCLI(lc);

            var none      = await cli.Execute("serverKeys");
            var made      = await cli.Execute("serverKeys create");
            var current   = lc.ServerCertificates.CurrentKey!;
            var listed    = await cli.Execute("serverKeys list");
            var again     = await cli.Execute("serverKeys create");
            var announced = await cli.Execute("serverKeys next");
            var next      = lc.ServerCertificates.NextKey!;
            var both      = await cli.Execute("serverKeys");
            var switched  = await cli.Execute("serverKeys switch");
            var unknown   = await cli.Execute("serverKeys rotate");

            Assert.Multiple(() => {

                Assert.That(none.First(),       Does.StartWith("No key is presented to the charging stations."));
                Assert.That(made.First(),       Is.EqualTo($"key pin       {current.KeyPin}  {current.Id}"));
                Assert.That(listed.First(),     Does.StartWith($"key pin       {current.KeyPin}  {current.Id}").And.EndWith("in its carrier certificate"));
                Assert.That(again.Single(),     Does.StartWith("No key was made: ").And.Contain("Announce the new key as the next one"));
                Assert.That(announced.First(),  Is.EqualTo($"next key pin  {next.KeyPin}  {next.Id}"));
                Assert.That(both,               Has.Some.StartWith($"key pin       {current.KeyPin}"));
                Assert.That(both,               Has.Some.StartWith($"next key pin  {next.KeyPin}"));
                Assert.That(switched.First(),   Does.Contain($"'{next.Id}'").And.Contain($"{next.KeyPin}"));
                Assert.That(lc.ServerCertificates.CurrentKey?.Id, Is.EqualTo(next.Id));
                Assert.That(unknown.Single(),   Does.StartWith("Usage: serverKeys "));

                lock (said)
                    Assert.That(said,           Has.Some.EqualTo($"Somebody at the command line had this local controller generate the server key '{current.Id}' with the key pin {current.KeyPin}."));

            });

        }

        #endregion

        #region StationPins_AreGivenListedAndTakenAway_AtTheConsole()

        /// <summary>
        /// A key pin given to a station that is not listed yet lists it, is
        /// listed with who gave it, is refused for a second station, and is
        /// taken away in another notation than it was given in.
        /// </summary>
        [Test]
        public async Task StationPins_AreGivenListedAndTakenAway_AtTheConsole()
        {

            var lc        = AController();
            using var cli = new ControllerCLI(lc);

            using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var pin       = KeyPin.FromKey(key);

            var added     = await cli.Execute($"stationPins cs001 add {pin.ToHexWithColons()} bay 1");
            var listed    = await cli.Execute("stationPins cs001");
            var every     = await cli.Execute("stationPins");
            var twice     = await cli.Execute($"stationPins cs002 add {pin}");
            var notAPin   = await cli.Execute("stationPins cs002 add SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU");
            var removed   = await cli.Execute($"stationPins cs001 remove {pin.ToCurl()}");
            var gone      = await cli.Execute($"stationPins cs001 remove {pin}");

            Assert.Multiple(() => {

                Assert.That(added.First(),     Is.EqualTo($"'cs001' is a charging station of this local controller now, in the group 'default', and is let in by the key pin {pin}."));
                Assert.That(listed.First(),    Does.StartWith(pin.ToHex()).And.Contain(" by the console").And.EndWith("bay 1"));
                Assert.That(every.First(),     Is.EqualTo("cs001:"));
                Assert.That(twice.Single(),    Does.StartWith("The key pin was not given: ").And.Contain("already lets the charging station 'cs001' in"));
                Assert.That(notAPin.Single(),  Does.StartWith("The key pin was not given: "));
                Assert.That(removed,           Is.EqualTo(new[] { $"The key pin {pin} lets 'cs001' in no more." }));
                Assert.That(gone,              Is.EqualTo(new[] { $"'cs001' has no key pin {pin}." }));
                Assert.That(lc.StationLogins.TryGet("cs002", out _), Is.False);

                lock (said)
                    Assert.That(said,          Has.Some.EqualTo($"Somebody at the command line gave the charging station 'cs001' the key pin {pin}."));

            });

        }

        #endregion

        #region KeyPins_OverSSH_AskWhatTheWebInterfaceAsks()

        /// <summary>
        /// Over SSH a CPO may read the server key and give a station a key pin,
        /// as the web interface lets one - and may not make a server key, which
        /// is the certificates' business and not the site's.
        /// </summary>
        [Test]
        public async Task KeyPins_OverSSH_AskWhatTheWebInterfaceAsks()
        {

            await AStartedController();

            using var cpo = await OverSSHAs("cleo", "cpo");

            var looked    = await cpo.Execute("serverKeys");
            var refused   = await cpo.Execute("serverKeys create");
            var given     = await cpo.Execute($"stationPins cs001 add {new String('a', 64)}");

            Assert.Multiple(() => {
                Assert.That(looked.First(),    Does.StartWith("No key is presented"));
                Assert.That(refused.Single(),  Does.StartWith("'cleo' may not do that here: it needs the "));
                Assert.That(controller!.ServerCertificates.Entries, Is.Empty);
                Assert.That(given.First(),     Does.StartWith("'cs001' is a charging station of this local controller now"));
                Assert.That(controller.StationLogins.TryGet("cs001", out var login) && login.Pins.Single().AddedBy == "cleo", Is.True);
            });

        }

        #endregion


        #region OpenADR_TakesInTheCredentialsAtTheConsole_AndAsks()

        /// <summary>
        /// What the grid operator's "localControllers add" printed, from a file:
        /// the controller signs in at the VTN it names, reads its limits, and
        /// says so - and openADR, openADR limits and openADR off say what holds.
        /// </summary>
        [Test]
        public async Task OpenADR_TakesInTheCredentialsAtTheConsole_AndAsks()
        {

            vtn = await FakeVTN.Started(VENName, Secret);
            vtn.Send("IMPORT_CAPACITY_LIMIT", 44, DateTimeOffset.UtcNow.AddMinutes(-5), TimeSpan.FromHours(1), "Engpass Nord");

            var lc        = await AStartedController();
            using var cli = new ControllerCLI(lc);

            var file      = Path.Combine(directory, "lc-paradies.txt");
            File.WriteAllText(file, $"token URL      {vtn.VTNURL}auth/token\nclient ID      {VENName}\nclient secret  {Secret}\n");

            var imported  = await cli.Execute($"openADR import {file}");
            var shown     = await cli.Execute("openADR");
            var limits    = await cli.Execute("openADR limits");

            Assert.Multiple(() => {

                Assert.That(imported,  Is.EqualTo(new[] {
                                           $"This local controller signs in at the OpenADR VTN at {vtn.VTNURL} as '{VENName}' now; the secret is in {lc.OpenADRLogin.Path}.",
                                           "The VTN answered: 1 limit(s) not over; now: draw at most 44 kW.",
                                           $"'{file}' still holds the secret: delete it."
                                       }));

                Assert.That(lc.OpenADRSettings.VTNURL?.ToString(), Is.EqualTo(vtn.VTNURL));
                Assert.That(lc.OpenADRSettings.ClientID,           Is.EqualTo(VENName));
                Assert.That(lc.OpenADRLogin.HasSecret,             Is.True);
                Assert.That(vtn.TokenRequests,                     Is.GreaterThanOrEqualTo(1));

                Assert.That(shown.First(),  Does.StartWith($"OpenADR VTN     {vtn.VTNURL}"));
                Assert.That(shown,          Has.Some.EqualTo($"  client ID     {VENName}"));
                Assert.That(shown,          Has.Some.StartsWith("  signed in     yes, until "));
                Assert.That(shown,          Has.None.Contains(Secret));

                Assert.That(limits.Length,  Is.EqualTo(2));
                Assert.That(limits[0],      Does.Contain("IMPORT_CAPACITY_LIMIT").And.Contain("44 kW").And.Contain("'Engpass Nord'"));
                Assert.That(limits[1],      Is.EqualTo("now: draw at most 44 kW."));

                lock (said)
                    Assert.That(said,       Has.None.Contains(Secret));

            });

            var off = await cli.Execute("openADR off");

            Assert.Multiple(() => {
                Assert.That(off,                    Is.EqualTo(new[] { "This local controller asks no OpenADR VTN any more." }));
                Assert.That(lc.OpenADREnabled,      Is.False);
            });

        }

        #endregion

        #region OpenADR_Import_IsTypedAtTheConsoleAlone()

        /// <summary>
        /// A file of this machine is no business of an account signed in over
        /// SSH: even a CPO, who may change the VTN, is told to type it at the
        /// console - and the file is not read, so its name tells nothing.
        /// </summary>
        [Test]
        public async Task OpenADR_Import_IsTypedAtTheConsoleAlone()
        {

            await AStartedController();

            using var cpo    = await OverSSHAs("cleo", "cpo");
            using var viewer = await OverSSHAs("vera", "viewer");

            var file         = Path.Combine(directory, "there-is-no-such-file.txt");

            var fromCPO      = await cpo.   Execute($"openADR import {file}");
            var fromViewer   = await viewer.Execute($"openADR import {file}");
            var viewerLooks  = await viewer.Execute("openADR");

            Assert.Multiple(() => {
                Assert.That(fromCPO,              Is.EqualTo(new[] { "'openADR import' reads a file of this machine, and is typed at its console alone." }));
                Assert.That(fromViewer.Single(),  Does.StartWith("'vera' may not do that here: it needs the "));
                Assert.That(viewerLooks.First(),  Does.StartWith("This local controller asks no OpenADR VTN."));
                Assert.That(controller!.OpenADRSettings.VTNURL, Is.Null);
            });

        }

        #endregion

        #region OpenADR_Credentials_AreReadAsTheGridOperatorPrintedThem()

        /// <summary>
        /// The lines "localControllers add" prints, and JSON with either URL and
        /// either spelling of the client's fields; the VTN is what the token URL
        /// is "auth/token" below, and what is missing is named.
        /// </summary>
        [TestCase("token URL      https://grid.example/openadr3/auth/token\nclient ID      lc-1\nclient secret  s3cret\n",
                  "https://grid.example/openadr3/", "lc-1", "s3cret", "")]
        [TestCase("VTN URL https://grid.example/openadr3/\r\nclient ID lc-1\r\nclient secret s3cret",
                  "https://grid.example/openadr3/", "lc-1", "s3cret", "")]
        [TestCase("""{ "vtnURL": "https://grid.example/openadr3/", "clientID": "lc-1", "clientSecret": "s3cret" }""",
                  "https://grid.example/openadr3/", "lc-1", "s3cret", "")]
        [TestCase("""{ "tokenURL": "https://grid.example/openadr3/auth/token/", "client_id": "lc-1", "client_secret": "s3cret" }""",
                  "https://grid.example/openadr3/", "lc-1", "s3cret", "")]
        [TestCase("token URL https://grid.example/oauth\nclient ID lc-1\nclient secret s3cret",
                  "", "lc-1", "s3cret", "VTN URL or token URL ending in auth/token")]
        [TestCase("token URL https://grid.example/openadr3/auth/token\nclient secret s3cret",
                  "https://grid.example/openadr3/", "", "s3cret", "client ID")]
        [TestCase("""{ "vtnURL": "https://grid.example/openadr3/", "clientID": "lc-1" }""",
                  "https://grid.example/openadr3/", "lc-1", "", "client secret")]
        [TestCase("{ this is no JSON", "", "", "", "VTN URL or token URL ending in auth/token")]
        public void OpenADR_Credentials_AreReadAsTheGridOperatorPrintedThem(String  Text,
                                                                           String  VTNURL,
                                                                           String  ClientId,
                                                                           String  ClientSecret,
                                                                           String  Missing)
        {

            var read = OpenADRCommand.TryCredentials(Text, out var vtnURL, out var clientId, out var clientSecret, out var missing);

            Assert.Multiple(() => {
                Assert.That(read,          Is.EqualTo(Missing.Length == 0));
                Assert.That(vtnURL,        Is.EqualTo(VTNURL));
                Assert.That(clientId,      Is.EqualTo(ClientId));
                Assert.That(clientSecret,  Is.EqualTo(ClientSecret));
                Assert.That(missing,       Is.EqualTo(Missing));
            });

        }

        #endregion

    }

}
