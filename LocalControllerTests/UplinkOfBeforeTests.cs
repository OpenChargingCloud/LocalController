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

using System.Security.Cryptography;

using NUnit.Framework;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.OCPP.Node.Connections;
using cloud.charging.open.protocols.OCPP.Node.KeyPins;

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// A controller configured before its lines up were connections of the
    /// store: one line in the section "csms", its credentials in
    /// csms-credentials.json, how it believes its CSMS in csms-keypins.json.
    /// Started once with the store, it has them as a connection - and its
    /// files of before put away.
    /// </summary>
    [TestFixture]
    public class UplinkOfBeforeTests
    {

        #region Data

        private const String  ThePassword      = "a-password-the-csms-issued";
        private const String  TheSharedSecret  = "aSharedSecretOfTheCSMS-0123456789";

        private String  directory   = "";

        private String  CredentialsFile  => Path.Combine(directory, CSMSCredentials.DefaultFileName);
        private String  KeyPinsFile      => Path.Combine(directory, CSMSKeyPins.    DefaultFileName);

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void SetUp()
        {
            directory = TestControllers.TemporaryDirectory("before");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown()
        {
            TestControllers.Remove(directory);
        }

        #endregion

        #region (private) AControllerOfBefore(URL, HTTPVersion, SecurityProfile)

        /// <summary>
        /// A controller made in the directory, whose configuration file says
        /// its line up as it was said before; not started.
        /// </summary>
        private LocalController AControllerOfBefore(String?  URL,
                                                    String?  HTTPVersion       = null,
                                                    Byte?    SecurityProfile   = null)
        {

            var csms = new JObject(new JProperty("enabled", URL is not null));

            // As a file of before had to say it: a line without TLS is
            // security profile 1, or the controller did not start.
            if (SecurityProfile is null && URL?.StartsWith("ws://", StringComparison.Ordinal) == true)
                SecurityProfile = 1;

            if (URL             is not null)  csms.Add("url",              URL);
            if (HTTPVersion     is not null)  csms.Add("httpVersion",      HTTPVersion);
            if (SecurityProfile is not null)  csms.Add("securityProfile",  SecurityProfile.Value);

            return TestControllers.New(
                       directory,
                       new JObject(
                           new JProperty("nts",  new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                           new JProperty("ocpp", new JObject(new JProperty("nodeId", "lc002"))),
                           new JProperty("csms", csms)
                       )
                   );

        }

        #endregion


        #region TheLineOfBefore_IsAConnection_ThatSignsInWithItsPassword()

        [Test]
        public async Task TheLineOfBefore_IsAConnection_ThatSignsInWithItsPassword()
        {

            Assert.That(new CSMSCredentials(CredentialsFile).TrySetPassword("lc002", ThePassword, out var error), Is.True, error);

            await using var controller = AControllerOfBefore("ws://127.0.0.1:9/lc002", HTTPVersion: "1.1");

            var line         = controller.Connections.Connections.Single();
            var credentials  = controller.Connections.Authentications.Single();

            Assert.Multiple(() => {

                Assert.That(line.URL.ToString(),            Is.EqualTo("ws://127.0.0.1:9/lc002"));
                Assert.That(line.HTTPVersion,               Is.EqualTo(WebSocketHTTPVersion.HTTP1_1));
                Assert.That(line.AutoConnect,               Is.True);
                Assert.That(line.ConnectionType,            Is.EqualTo(ConnectionType.CSMS));
                Assert.That(line.AuthenticationId,          Is.EqualTo(credentials.Id));

                Assert.That(credentials.Kind,               Is.EqualTo(AuthenticationKind.Basic));
                Assert.That(credentials.Login,              Is.EqualTo("lc002"));
                Assert.That(credentials.Password,           Is.EqualTo(ThePassword));

                Assert.That(controller.CSMSEnabled,         Is.True, "whether it reports to a CSMS at all is the section's still");
                Assert.That(File.Exists(CredentialsFile),   Is.False, "the secret is kept twice");

                Assert.That(controller.Log.Recent(200).Select(line => line.Message),
                            Has.Some.Contains("is the connection").And.Contains("no longer in the section 'csms'"));

            });

        }

        #endregion

        #region OneThatHadAPasswordAndAToken_SignsInWithThePassword_AndKeepsTheToken()

        [Test]
        public async Task OneThatHadAPasswordAndAToken_SignsInWithThePassword_AndKeepsTheToken()
        {

            var before = new CSMSCredentials(CredentialsFile);

            Assert.That(before.TrySetPassword("lc002", ThePassword, out var passwordError), Is.True, passwordError);
            Assert.That(before.TrySetTOTP    ("lc002", TheSharedSecret, TimeSpan.FromSeconds(60), 10, null, null, out var tokenError, out _, TLSChannelBinding: true),
                        Is.True, tokenError);

            await using var controller = AControllerOfBefore("wss://csms.example/lc002", HTTPVersion: "3");

            var line      = controller.Connections.Connections.Single();
            var password  = controller.Connections.Authentications.Single(one => one.Kind == AuthenticationKind.Basic);
            var token     = controller.Connections.Authentications.Single(one => one.Kind == AuthenticationKind.TOTP);

            Assert.Multiple(() => {

                Assert.That(line.AuthenticationId,     Is.EqualTo(password.Id), "a connection names one set of credentials: the password");
                Assert.That(line.HTTPVersion,          Is.EqualTo(WebSocketHTTPVersion.HTTP3));

                Assert.That(token.Login,               Is.EqualTo("lc002"));
                Assert.That(token.SharedSecret,        Is.EqualTo(TheSharedSecret));
                Assert.That(token.Validity,            Is.EqualTo(TimeSpan.FromSeconds(60)));
                Assert.That(token.Length,              Is.EqualTo(10));
                Assert.That(token.TLSChannelBinding,   Is.True, "that the token is bound to the line was lost");

                Assert.That(controller.Log.Recent(200).Select(line => line.Message),
                            Has.Some.Contains("had a password and a one-time token").And.Contains("to be chosen for the connection instead"));

            });

        }

        #endregion

        #region OneThatHadATokenAlone_SignsInWithIt()

        [Test]
        public async Task OneThatHadATokenAlone_SignsInWithIt()
        {

            Assert.That(new CSMSCredentials(CredentialsFile).TrySetTOTP("lc002", TheSharedSecret, null, null, null, null, out var error), Is.True, error);

            await using var controller = AControllerOfBefore("ws://127.0.0.1:9/lc002");

            var token = controller.Connections.Authentications.Single();

            Assert.Multiple(() => {
                Assert.That(token.Kind,                                                   Is.EqualTo(AuthenticationKind.TOTP));
                Assert.That(controller.Connections.Connections.Single().AuthenticationId,  Is.EqualTo(token.Id));
            });

        }

        #endregion

        #region WhomItBelieved_ComesWithTheLine()

        /// <summary>
        /// The key pins, their version, how they are trusted and who may
        /// sign a pin set come with the line: a pin set of an older version
        /// than the one it had is not taken afterwards.
        /// </summary>
        [Test]
        public async Task WhomItBelieved_ComesWithTheLine()
        {

            const String url = "wss://csms.example/lc002";

            using var key     = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var another = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var signs   = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var pin     = KeyPin.FromKey(key);
            var point   = signs.ExportParameters(false).Q;
            var signer  = Convert.ToBase64String([ 4, .. point.X!, .. point.Y! ]);

            var before  = new CSMSKeyPins(KeyPinsFile, () => url, DialAgain: () => { });

            Assert.That(before.TrySetTrust(new ServerKeyPinSettings("Prefer", "Key", 120), out var trustError),  Is.True, trustError);
            Assert.That(before.TrySetPins(pin.ToHex(), out var pinsError),                                        Is.True, pinsError);
            Assert.That(before.TryAddPinSetSigner(signer, "The CSMS of the site", out var signerError),           Is.True, signerError);

            // As a signed pin set left it: with its version.
            var written = JObject.Parse(File.ReadAllText(KeyPinsFile));
            written["serverKeyPinsVersion"] = 7;
            File.WriteAllText(KeyPinsFile, written.ToString());

            await using var controller = AControllerOfBefore(url);

            var line        = controller.Connections.Connections.Single();
            var olderTaken  = controller.Connections.TryTakeSignedServerKeyPins(line.Id, KeyPin.FromKey(another).ToHex(), 6, out _, out _, out var olderError);

            Assert.Multiple(() => {

                Assert.That(line.ServerKeyPins,                             Is.EqualTo(new[] { pin }));
                Assert.That(line.ServerKeyPinsVersion,                      Is.EqualTo(7));
                Assert.That(line.RawPublicKeys,                             Is.EqualTo(RawPublicKeyMode.Prefer));
                Assert.That(line.TrustOnFirstUse,                           Is.EqualTo(KeyTrustOnFirstUse.Key));
                Assert.That(line.ServerKeyPinProbationTime,                 Is.EqualTo(TimeSpan.FromSeconds(120)));

                Assert.That(controller.Connections.PinSetSigners.Single().Description,  Is.EqualTo("The CSMS of the site"));

                Assert.That(olderTaken,                                     Is.False, "a pin set older than the one of before was taken");
                Assert.That(olderError,                                     Is.Not.Null);

                Assert.That(File.Exists(KeyPinsFile),                       Is.False, "the file of before would be read again");
                Assert.That(File.Exists(KeyPinsFile + ".takenOver"),        Is.True,  "the file of before is gone, and nobody can look");

            });

        }

        #endregion

        #region TheSecurityProfileTheFileSaid_IsNoLongerRead_AndSaidWhereItIsAnother()

        [Test]
        public async Task TheSecurityProfileTheFileSaid_IsNoLongerRead_AndSaidWhereItIsAnother()
        {

            // Profile 3, and no certificate chosen to hold up: the line is
            // what its address says, security profile 2.
            await using var controller = AControllerOfBefore("wss://csms.example/lc002", SecurityProfile: 3);

            Assert.That(controller.Log.Recent(200).Select(line => line.Message),
                        Has.Some.Contains("says security profile 3").And.Contains("which is security profile 2 by its address").
                                 And.Contains("'csms.securityProfile' is no longer read"));

        }

        #endregion

        #region StartedAgain_NothingIsTakenOverTwice()

        [Test]
        public async Task StartedAgain_NothingIsTakenOverTwice()
        {

            Assert.That(new CSMSCredentials(CredentialsFile).TrySetPassword("lc002", ThePassword, out var error), Is.True, error);

            String id;

            await using (var first = AControllerOfBefore("ws://127.0.0.1:9/lc002"))
                id = first.Connections.Connections.Single().Id;

            // The section still says the address, as nobody took it out of
            // the file - and credentials of before appear again, as from a
            // backup put back.
            Assert.That(new CSMSCredentials(CredentialsFile).TrySetPassword("lc002", "another-password-of-before", out error), Is.True, error);

            await using var again = AControllerOfBefore("ws://127.0.0.1:9/lc002");

            Assert.Multiple(() => {
                Assert.That(again.Connections.Connections.Single().Id,                Is.EqualTo(id));
                Assert.That(again.Connections.Authentications.Single().Password,      Is.EqualTo(ThePassword), "what the store holds was overwritten by a file of before");
            });

        }

        #endregion

        #region CredentialsWithoutALine_AreKept_ForTheConnectionToCome()

        [Test]
        public async Task CredentialsWithoutALine_AreKept_ForTheConnectionToCome()
        {

            Assert.That(new CSMSCredentials(CredentialsFile).TrySetPassword("lc002", ThePassword, out var error), Is.True, error);

            await using var controller = AControllerOfBefore(null);

            Assert.Multiple(() => {
                Assert.That(controller.Connections.Connections,                    Is.Empty);
                Assert.That(controller.Connections.Authentications.Single().Login,  Is.EqualTo("lc002"));
                Assert.That(File.Exists(CredentialsFile),                          Is.False);
            });

        }

        #endregion

    }

}
