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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// A charging station actually signing in with a one-time token, and a
    /// login group actually turning one away.
    /// </summary>
    /// <remarks>
    /// Over a real socket, because that is the only thing that shows the whole
    /// path: the Authorization header as a station would send it, Hermod's
    /// parser, the validator, the group and the store. Every piece of it is
    /// testable on its own and each of those tests can pass while the thing as
    /// a whole turns everybody away.
    /// </remarks>
    public class StationAuthMethodTests : ALocalControllerTests
    {

        #region Data

        private const String ThePassword  = "a-password-long-enough-for-ocpp";
        private const String TheSecret    = "a-shared-secret-long-enough";

        #endregion

        #region (override) Configuration

        protected override JObject Configuration

            => new (

                   new JProperty("nts",  new JObject(new JProperty("enabled", false))),

                   new JProperty("ocppServer", new JObject(
                       new JProperty("enabled",           true),
                       new JProperty("address",           "127.0.0.1"),
                       new JProperty("port",              TestControllers.FreePort()),
                       new JProperty("securityProfiles",  new JArray(1)),
                       new JProperty("subprotocols",      new JArray("ocpp2.1"))
                   ))

               );

        #endregion

        #region (private) Connect(Authorization) / BasicAuth(...) / TOTPAuth(...)

        /// <summary>
        /// One upgrade request with whatever Authorization header the test
        /// wants to try, and whether it got in.
        /// </summary>
        private async Task<(Boolean Connected, String? Why)> Connect(String? Authorization)
        {

            using var client = new ClientWebSocket();

            client.Options.AddSubProtocol("ocpp2.1");

            if (Authorization is not null)
                client.Options.SetRequestHeader("Authorization", Authorization);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            try
            {
                await client.ConnectAsync(
                          new Uri($"ws://127.0.0.1:{Controller.OCPPServerSettings.TCPPort}"),
                          timeout.Token
                      );
            }
            catch (Exception e)
            {
                return (false, e.Message);
            }

            // Whether the charging station got in was decided by the upgrade,
            // and it has just succeeded. How the socket is taken down again is
            // a different question: a station that says nothing afterwards can
            // be dropped by the server before the closing handshake finishes,
            // and reporting that as a refusal would fail a test for the one
            // thing it is not about.
            try
            {
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
            catch (Exception)
            { }

            return (true, null);

        }

        private static String BasicAuth(String Id, String Password)

            => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Id}:{Password}"));

        /// <summary>
        /// The header a charging station sends, built the way Hermod builds it
        /// on the other side rather than by hand - what is being tested is the
        /// controller, not the test's idea of the format.
        /// </summary>
        private static String TOTPAuth(String              Id,
                                       String              SharedSecret   = TheSecret,
                                       TOTPHTTPHeaderType  Type           = TOTPHTTPHeaderType.RAW,
                                       DateTimeOffset?     When           = null)
        {

            var (token, _, _) = TOTPGenerator.GenerateTOTP(
                                    SharedSecret,
                                    TOTPSettings.DefaultValidityTime,
                                    TOTPSettings.DefaultLength,
                                    TOTPSettings.DefaultAlphabet,
                                    When ?? DateTimeOffset.UtcNow,
                                    null,
                                    TOTPHashAlgorithm.SHA256
                                );

            return HTTPTOTPAuthentication.Create(Id, token, Type).HTTPText;

        }

        /// <summary>
        /// A charging station that may be let in with a one-time token, with
        /// everything at the default both ends agree on.
        /// </summary>
        private void GiveATokenTo(String Id, String? Group = null)
        {

            if (!Controller.StationLogins.TrySetTOTP(Id, TheSecret, null, null, null, null, Group, null,
                                                     out _, out var error))
            {
                throw new InvalidOperationException($"The test's own TOTP login was refused: {error}");
            }

        }

        #endregion


        #region AStationWithAOneTimeTokenGetsIn()

        [Test]
        public async Task AStationWithAOneTimeTokenGetsIn()
        {

            GiveATokenTo("cs001");

            var (connected, why) = await Connect(TOTPAuth("cs001"));

            Assert.That(connected, Is.True, why);

        }

        #endregion

        #region AWrongTokenIsTurnedAway()

        [Test]
        public async Task AWrongTokenIsTurnedAway()
        {

            GiveATokenTo("cs001");

            var (connected, _) = await Connect(TOTPAuth("cs001", SharedSecret: "a-completely-different-secret"));

            Assert.That(connected, Is.False);

        }

        #endregion

        #region ATokenFromAnHourAgoIsTurnedAway()

        [Test]
        public async Task ATokenFromAnHourAgoIsTurnedAway()
        {

            GiveATokenTo("cs001");

            var (connected, _) = await Connect(TOTPAuth("cs001", When: DateTimeOffset.UtcNow.AddHours(-1)));

            Assert.That(connected, Is.False,
                        "A token from an hour ago was accepted, which would make the whole exercise pointless.");

        }

        #endregion

        #region AStationNobodyGaveATokenToIsTurnedAway()

        [Test]
        public async Task AStationNobodyGaveATokenToIsTurnedAway()
        {

            var (connected, _) = await Connect(TOTPAuth("cs404"));

            Assert.That(connected, Is.False);

        }

        #endregion

        #region AStationWithOnlyAPasswordCannotUseAToken()

        [Test]
        public async Task AStationWithOnlyAPasswordCannotUseAToken()
        {

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out _);

            var (connected, _) = await Connect(TOTPAuth("cs001"));

            Assert.That(connected, Is.False);

        }

        #endregion

        #region ATLSBoundTokenIsTurnedAwayBecauseItCannotBeChecked()

        /// <summary>
        /// Hermod's default is the bound kind, which needs TLS exporter
        /// material - and .NET does not offer that on an SslStream. A station
        /// using the default has to be refused, and the refusal has to say what
        /// to change rather than look like a wrong token.
        /// </summary>
        [Test]
        public async Task ATLSBoundTokenIsTurnedAwayBecauseItCannotBeChecked()
        {

            GiveATokenTo("cs001");

            var (connected, _) = await Connect(TOTPAuth("cs001", Type: TOTPHTTPHeaderType.TLSChannelBinding));

            Assert.That(connected, Is.False);

        }

        #endregion


        #region ALoginMayCarryBothAndEitherOneGetsItIn()

        /// <summary>
        /// A station being moved from a password to a token has to be able to
        /// use either while it is being moved.
        /// </summary>
        [Test]
        public async Task ALoginMayCarryBothAndEitherOneGetsItIn()
        {

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out _);
            GiveATokenTo("cs001");

            var withAPassword = await Connect(BasicAuth("cs001", ThePassword));
            var withAToken    = await Connect(TOTPAuth("cs001"));

            Assert.Multiple(() => {
                Assert.That(withAPassword.Connected,  Is.True, withAPassword.Why);
                Assert.That(withAToken.Connected,     Is.True, withAToken.Why);
            });

        }

        #endregion

        #region TakingThePasswordAwayLeavesTheTokenWorking()

        [Test]
        public async Task TakingThePasswordAwayLeavesTheTokenWorking()
        {

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out _);
            GiveATokenTo("cs001");

            Assert.That(Controller.StationLogins.TryClearPassword("cs001", out var error), Is.True, error);

            var withAPassword = await Connect(BasicAuth("cs001", ThePassword));
            var withAToken    = await Connect(TOTPAuth("cs001"));

            Assert.Multiple(() => {
                Assert.That(withAPassword.Connected,  Is.False,
                            "The password was taken away and still let the charging station in.");
                Assert.That(withAToken.Connected,     Is.True, withAToken.Why);
            });

        }

        #endregion


        #region AGroupThatDoesNotAcceptTokensTurnsOneAway()

        [Test]
        public async Task AGroupThatDoesNotAcceptTokensTurnsOneAway()
        {

            Controller.StationLogins.TryAddOrUpdateGroup(
                "passwords-only", "Passwords only", true, [AuthMethod.Basic], [1, 2, 3], null, out _);

            GiveATokenTo("cs001", "passwords-only");

            var (connected, _) = await Connect(TOTPAuth("cs001"));

            Assert.That(connected, Is.False,
                        "The group does not accept one-time tokens and a correct one still got in.");

        }

        #endregion

        #region AGroupThatDoesNotAcceptPasswordsTurnsOneAway()

        [Test]
        public async Task AGroupThatDoesNotAcceptPasswordsTurnsOneAway()
        {

            Controller.StationLogins.TryAddOrUpdateGroup(
                "tokens-only", "Tokens only", true, [AuthMethod.TOTP], [1, 2, 3], null, out _);

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, "tokens-only", null, out _, out _);

            var (connected, _) = await Connect(BasicAuth("cs001", ThePassword));

            Assert.That(connected, Is.False,
                        "The group does not accept passwords and the right password still got in.");

        }

        #endregion

        #region AGroupThatIsSwitchedOffTurnsItsMembersAway()

        /// <summary>
        /// The one move that stops a whole site.
        /// </summary>
        [Test]
        public async Task AGroupThatIsSwitchedOffTurnsItsMembersAway()
        {

            Controller.StationLogins.TryAddOrUpdateGroup(
                "field", "Field test", true, [AuthMethod.Basic, AuthMethod.TOTP], [1, 2, 3], null, out _);

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, "field", null, out _, out _);
            GiveATokenTo("cs002", "field");

            var beforehand = await Connect(BasicAuth("cs001", ThePassword));

            Controller.StationLogins.TryAddOrUpdateGroup(
                "field", "Field test", false, [AuthMethod.Basic, AuthMethod.TOTP], [1, 2, 3], null, out _);

            var withAPassword = await Connect(BasicAuth("cs001", ThePassword));
            var withAToken    = await Connect(TOTPAuth("cs002"));

            Assert.Multiple(() => {
                Assert.That(beforehand.Connected,     Is.True, beforehand.Why);
                Assert.That(withAPassword.Connected,  Is.False, "A switched-off group still let a password in.");
                Assert.That(withAToken.Connected,     Is.False, "A switched-off group still let a token in.");
            });

        }

        #endregion

        #region AGroupThatDoesNotAcceptThisProfileTurnsItsMembersAway()

        /// <summary>
        /// This port is unencrypted, so everything on it is security profile 1.
        /// A group that only ever comes in over TLS has no business here.
        /// </summary>
        [Test]
        public async Task AGroupThatDoesNotAcceptThisProfileTurnsItsMembersAway()
        {

            Controller.StationLogins.TryAddOrUpdateGroup(
                "encrypted-only", "Encrypted only", true, [AuthMethod.Basic, AuthMethod.TOTP], [2, 3], null, out _);

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, "encrypted-only", null, out _, out _);

            var (connected, _) = await Connect(BasicAuth("cs001", ThePassword));

            Assert.That(connected, Is.False,
                        "A group that does not accept security profile 1 was let in on an unencrypted port.");

        }

        #endregion

        #region MovingALoginIntoAnotherGroupTakesEffectOnTheNextConnection()

        /// <summary>
        /// Not at the next start: a station that has just been shut out must be
        /// shut out now.
        /// </summary>
        [Test]
        public async Task MovingALoginIntoAnotherGroupTakesEffectOnTheNextConnection()
        {

            Controller.StationLogins.TryAddOrUpdateGroup(
                "tokens-only", "Tokens only", true, [AuthMethod.TOTP], [1, 2, 3], null, out _);

            Controller.StationLogins.TrySetPassword("cs001", ThePassword, null, null, out _, out _);

            var beforehand = await Connect(BasicAuth("cs001", ThePassword));

            Assert.That(Controller.StationLogins.TrySetGroup("cs001", "tokens-only", out var error), Is.True, error);

            var afterwards = await Connect(BasicAuth("cs001", ThePassword));

            Assert.Multiple(() => {
                Assert.That(beforehand.Connected,  Is.True, beforehand.Why);
                Assert.That(afterwards.Connected,  Is.False, "The move did not take effect until the next start.");
            });

        }

        #endregion

    }

}
