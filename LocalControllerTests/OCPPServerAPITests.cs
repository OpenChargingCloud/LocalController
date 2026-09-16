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

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The routes the charging station server pages sit on.
    /// </summary>
    /// <remarks>
    /// The controller under here is switched on but its charging station server
    /// is not: what is being tested is the layer between the browser and the
    /// stores, and a socket bound to every interface is not needed to test it -
    /// nor wanted on a machine running tests.
    /// </remarks>
    public class OCPPServerAPITests : ALocalControllerTests
    {

        #region (private) Root

        private const String Root = "/api/v1/configuration/ocpp-server";

        #endregion


        #region TheServerSaysWhatItIsAndWhatItIsNotDoing()

        [Test]
        public async Task TheServerSaysWhatItIsAndWhatItIsNotDoing()
        {

            using var http = await SignedIn();

            var server = await GetJSON(http, Root);

            Assert.Multiple(() => {

                Assert.That(server.Value<Boolean>("enabled"),                Is.False,
                            "A controller nobody configured is listening for charging stations.");

                Assert.That(server.Value<Int32>("port"),                     Is.EqualTo(2351));
                Assert.That(server["securityProfiles"]?.Values<Int32>(),     Is.EquivalentTo(new[] { 1, 2, 3 }));
                Assert.That(server["subprotocols"]?.Values<String>(),        Is.EquivalentTo(new[] { "ocpp2.1", "ocpp2.0.1" }));
                Assert.That(server["state"]?.Value<Boolean>("running"),      Is.False);
                Assert.That(server["state"]?.Value<Boolean>("tls"),          Is.False);
                Assert.That(server["state"]?.Value<Boolean>("hasCertificate"), Is.False);
                Assert.That(server["logging"]?.Value<Boolean>("payloads"),   Is.False,
                            "The contents of OCPP messages are being logged by default.");

            });

        }

        #endregion

        #region NobodySignedInSeesNoneOfIt()

        [Test]
        public async Task NobodySignedInSeesNoneOfIt()
        {

            using var http = Anonymous();

            foreach (var path in new[] { Root, $"{Root}/certificates", $"{Root}/trust", $"{Root}/stations" })
            {
                var response = await http.GetAsync(path);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), $"GET {path}");
            }

        }

        #endregion

        #region WhatIsSavedIsWhatComesBack()

        [Test]
        public async Task WhatIsSavedIsWhatComesBack()
        {

            using var http = await SignedIn();

            var response = await http.PutAsync(
                                     Root,
                                     JSONBody(
                                         new JProperty("port",         9500),
                                         new JProperty("reachableAs",  new JArray("lc001.example.org"))
                                     )
                                 );

            Assert.That(response.IsSuccessStatusCode, Is.True, await response.Content.ReadAsStringAsync());

            var saved = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.Multiple(() => {

                Assert.That(saved.Value<Int32>("port"),               Is.EqualTo(9500));
                Assert.That(saved["reachableAs"]?.Values<String>(),   Is.EquivalentTo(new[] { "lc001.example.org" }));

                // The port belongs to the socket, and the socket was opened
                // before this request; saying so is the whole point.
                Assert.That(saved["state"]?["waitingForARestart"]?.Values<String>(), Does.Contain("port"));

                // And what does not belong to the socket is not claimed to be
                // waiting for one.
                Assert.That(saved["state"]?["waitingForARestart"]?.Values<String>(), Does.Not.Contain("reachableAs"));

            });

            // And it is in the file, not only in memory.
            Assert.That(await File.ReadAllTextAsync(Controller.ConfigFile.Path), Does.Contain("9500"));

        }

        #endregion

        #region SomethingTheControllerRefusesChangesNothing()

        [Test]
        public async Task SomethingTheControllerRefusesChangesNothing()
        {

            using var http = await SignedIn();

            var response = await http.PutAsync(
                                     Root,
                                     JSONBody(new JProperty("securityProfiles", new JArray(4)))
                                 );

            Assert.Multiple(() => {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(Controller.OCPPServerSettings.SecurityProfiles, Is.EquivalentTo(new Byte[] { 1, 2, 3 }));
            });

        }

        #endregion

        #region TLSOnlyIsRefusedWhileThereIsNoCertificate()

        /// <summary>
        /// Profiles 2 and 3 both need TLS, and without a certificate this port
        /// does not encrypt - so saving that would be saving a port through
        /// which nothing can come.
        /// </summary>
        [Test]
        public async Task TLSOnlyIsRefusedWhileThereIsNoCertificate()
        {

            using var http = await SignedIn();

            var response = await http.PutAsync(
                                     Root,
                                     JSONBody(new JProperty("securityProfiles", new JArray(2, 3)))
                                 );

            await Assert.MultipleAsync(async () => {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("no server certificate"));
            });

        }

        #endregion

        #region MessageContentsCannotBeSwitchedOnIntoThePast()

        [Test]
        public async Task MessageContentsCannotBeSwitchedOnIntoThePast()
        {

            using var http = await SignedIn();

            var response = await http.PutAsync(
                                     Root,
                                     JSONBody(new JProperty("logging", new JObject(
                                         new JProperty("payloads",       true),
                                         new JProperty("payloadsUntil",  DateTimeOffset.UtcNow.AddHours(-1).ToString("o"))
                                     )))
                                 );

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
                        await response.Content.ReadAsStringAsync());

        }

        #endregion


        #region AKeyIsMadeAndItsRequestCanBeFetched()

        [Test]
        public async Task AKeyIsMadeAndItsRequestCanBeFetched()
        {

            using var http = await SignedIn();

            await http.PutAsync(Root, JSONBody(new JProperty("reachableAs", new JArray("lc001.example.org"))));

            var made = await http.PostAsync(
                                 $"{Root}/certificates",
                                 JSONBody(
                                     new JProperty("subject",    "lc001.example.org"),
                                     new JProperty("algorithm",  "ecdsa-p256")
                                 )
                             );

            Assert.That(made.StatusCode, Is.EqualTo(HttpStatusCode.Created), await made.Content.ReadAsStringAsync());

            var answer = JObject.Parse(await made.Content.ReadAsStringAsync());
            var id     = answer.Value<String>("id")!;

            var csr    = await http.GetAsync($"{Root}/certificates/{id}/csr");

            await Assert.MultipleAsync(async () => {

                Assert.That(answer.Value<String>("csr"), Does.StartWith("-----BEGIN CERTIFICATE REQUEST-----"));

                Assert.That(csr.IsSuccessStatusCode,                     Is.True);
                Assert.That(await csr.Content.ReadAsStringAsync(),        Does.StartWith("-----BEGIN CERTIFICATE REQUEST-----"));

                // And nothing anywhere hands out the private key.
                var listed = await GetJSON(http, $"{Root}/certificates");

                Assert.That(listed.ToString(),                            Does.Not.Contain("PRIVATE KEY"));
                Assert.That(listed.Value<Boolean>("canImportPrivateKeys"), Is.False);

            });

        }

        #endregion

        #region AKeyCannotBeMadeWithoutKnowingWhatWeAreReachedAs()

        [Test]
        public async Task AKeyCannotBeMadeWithoutKnowingWhatWeAreReachedAs()
        {

            using var http = await SignedIn();

            var made = await http.PostAsync(
                                 $"{Root}/certificates",
                                 JSONBody(new JProperty("algorithm", "ecdsa-p256"))
                             );

            await Assert.MultipleAsync(async () => {
                Assert.That(made.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(await made.Content.ReadAsStringAsync(), Does.Contain("reachable as"));
            });

        }

        #endregion

        #region ACertificateForAnotherKeySaysWhoseItIs()

        [Test]
        public async Task ACertificateForAnotherKeySaysWhoseItIs()
        {

            using var http = await SignedIn();

            await http.PutAsync(Root, JSONBody(new JProperty("reachableAs", new JArray("lc001.example.org"))));

            var first  = JObject.Parse(await (await http.PostAsync($"{Root}/certificates",
                             JSONBody(new JProperty("algorithm", "ecdsa-p256")))).Content.ReadAsStringAsync());

            var second = JObject.Parse(await (await http.PostAsync($"{Root}/certificates",
                             JSONBody(new JProperty("algorithm", "ecdsa-p256")))).Content.ReadAsStringAsync());

            using var ca = TestCA.Create("Test CA");

            // A certificate that answers the first request, uploaded to the
            // second row.
            using var certificate = ca.Sign(first.Value<String>("csr")!,
                                            DateTimeOffset.UtcNow.AddDays(-1),
                                            DateTimeOffset.UtcNow.AddYears(1));

            var response = await http.PutAsync(
                                     $"{Root}/certificates/{second.Value<String>("id")}",
                                     JSONBody(new JProperty("pem", ca.ChainPEM(certificate)))
                                 );

            await Assert.MultipleAsync(async () => {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain(first.Value<String>("id")!));
            });

        }

        #endregion

        #region ACertificateIsTakenInAndShowsUpOnThePage()

        [Test]
        public async Task ACertificateIsTakenInAndShowsUpOnThePage()
        {

            using var http = await SignedIn();

            await http.PutAsync(Root, JSONBody(new JProperty("reachableAs", new JArray("lc001.example.org"))));

            var made = JObject.Parse(await (await http.PostAsync($"{Root}/certificates",
                           JSONBody(new JProperty("algorithm", "ecdsa-p256")))).Content.ReadAsStringAsync());

            using var ca          = TestCA.Create("Test CA");
            using var certificate = ca.Sign(made.Value<String>("csr")!,
                                            DateTimeOffset.UtcNow.AddDays(-1),
                                            DateTimeOffset.UtcNow.AddYears(1));

            var response = await http.PutAsync(
                                     $"{Root}/certificates/{made.Value<String>("id")}",
                                     JSONBody(new JProperty("pem", ca.ChainPEM(certificate)))
                                 );

            Assert.That(response.IsSuccessStatusCode, Is.True, await response.Content.ReadAsStringAsync());

            var listed = await GetJSON(http, $"{Root}/certificates");
            var entry  = listed["entries"]?[0];

            Assert.Multiple(() => {
                Assert.That(entry?.Value<Boolean>("hasCertificate"),                 Is.True);
                Assert.That(entry?["certificate"]?.Value<String>("state"),           Is.EqualTo("valid"));
                Assert.That(entry?["certificate"]?["subjectAltNames"]?.Values<String>(),
                            Does.Contain("lc001.example.org"));
            });

        }

        #endregion


        #region AChainIsAcceptedSwitchedOffAndRemoved()

        [Test]
        public async Task AChainIsAcceptedSwitchedOffAndRemoved()
        {

            using var http = await SignedIn();
            using var ca   = TestCA.Create("Some Charging Network");

            var added = await http.PostAsync(
                                  $"{Root}/trust",
                                  JSONBody(
                                      new JProperty("pem",   TestCA.ToPEM(ca.Certificate)),
                                      new JProperty("name",  "Some Charging Network")
                                  )
                              );

            Assert.That(added.StatusCode, Is.EqualTo(HttpStatusCode.Created), await added.Content.ReadAsStringAsync());

            var id = JObject.Parse(await added.Content.ReadAsStringAsync()).Value<String>("id")!;

            var off = await http.PutAsync($"{Root}/trust/{id}", JSONBody(new JProperty("enabled", false)));

            Assert.That(off.IsSuccessStatusCode, Is.True);
            Assert.That(JObject.Parse(await off.Content.ReadAsStringAsync()).Value<Int32>("enabled"), Is.EqualTo(0));

            var gone = await http.DeleteAsync($"{Root}/trust/{id}");

            Assert.Multiple(() => {
                Assert.That(gone.IsSuccessStatusCode,        Is.True);
                Assert.That(Controller.ClientTrust.Entries,  Is.Empty);
            });

        }

        #endregion

        #region SomethingThatIsNotACertificateAuthorityIsRefused()

        [Test]
        public async Task SomethingThatIsNotACertificateAuthorityIsRefused()
        {

            using var http = await SignedIn();

            var response = await http.PostAsync(
                                     $"{Root}/trust",
                                     JSONBody(new JProperty("pem", "trust us, we are a charging network"))
                                 );

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        }

        #endregion


        #region AStationIsAddedWithAPasswordShownOnce()

        [Test]
        public async Task AStationIsAddedWithAPasswordShownOnce()
        {

            using var http = await SignedIn();

            var added = await http.PostAsync(
                                  $"{Root}/stations",
                                  JSONBody(
                                      new JProperty("id",    "cs001"),
                                      new JProperty("note",  "Ladepunkt 1")
                                  )
                              );

            Assert.That(added.IsSuccessStatusCode, Is.True, await added.Content.ReadAsStringAsync());

            var answer   = JObject.Parse(await added.Content.ReadAsStringAsync());
            var password = answer.Value<String>("password");

            await Assert.MultipleAsync(async () => {

                Assert.That(password, Is.Not.Null.And.Not.Empty,
                            "No password came back, so nobody can configure the charging station.");

                Assert.That(Controller.StationLogins.Verify("cs001", password!), Is.True);

                // And a second look is not a second copy of it.
                var listed = await GetJSON(http, $"{Root}/stations");

                Assert.That(listed.ToString(), Does.Not.Contain(password!));
                Assert.That(listed.ToString(), Does.Not.Contain("$pbkdf2"));
                Assert.That(listed["stations"]?[0]?.Value<String>("note"), Is.EqualTo("Ladepunkt 1"));

            });

        }

        #endregion

        #region AStationIsShutOutAndForgotten()

        [Test]
        public async Task AStationIsShutOutAndForgotten()
        {

            using var http = await SignedIn();

            var added    = JObject.Parse(await (await http.PostAsync($"{Root}/stations",
                               JSONBody(new JProperty("id", "cs001")))).Content.ReadAsStringAsync());

            var password = added.Value<String>("password")!;

            await http.PutAsync($"{Root}/stations/cs001", JSONBody(new JProperty("enabled", false)));

            Assert.That(Controller.StationLogins.Verify("cs001", password), Is.False);

            var gone = await http.DeleteAsync($"{Root}/stations/cs001");

            Assert.Multiple(() => {
                Assert.That(gone.IsSuccessStatusCode,        Is.True);
                Assert.That(Controller.StationLogins.Logins, Is.Empty);
            });

        }

        #endregion

        #region AStationThisControllerNeverHeardOfIsANotFound()

        [Test]
        public async Task AStationThisControllerNeverHeardOfIsANotFound()
        {

            using var http = await SignedIn();

            var response = await http.DeleteAsync($"{Root}/stations/cs404");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        }

        #endregion

        #region NobodySignedInChangesNoLoginsAndNoGroups()

        /// <summary>
        /// The write routes are the ones worth trying uninvited, and a refusal
        /// has to come before this controller says anything about the body.
        /// </summary>
        [Test]
        public async Task NobodySignedInChangesNoLoginsAndNoGroups()
        {

            using var http = Anonymous();

            var attempts = new (String What, Task<HttpResponseMessage> Response)[] {
                ("POST groups",           http.PostAsync  ($"{Root}/groups",                   JSONBody(new JProperty("id", "sneaky")))),
                ("PUT groups/{id}",       http.PutAsync   ($"{Root}/groups/default",           JSONBody(new JProperty("enabled", false)))),
                ("DELETE groups/{id}",    http.DeleteAsync($"{Root}/groups/default")),
                ("POST stations",         http.PostAsync  ($"{Root}/stations",                 JSONBody(new JProperty("id", "cs666")))),
                ("PUT stations/../totp",  http.PutAsync   ($"{Root}/stations/cs001/totp",      JSONBody())),
                ("DELETE ../totp",        http.DeleteAsync($"{Root}/stations/cs001/totp")),
                ("DELETE ../password",    http.DeleteAsync($"{Root}/stations/cs001/password"))
            };

            foreach (var (what, response) in attempts)
                Assert.That((await response).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), what);

            Assert.Multiple(() => {
                Assert.That(Controller.StationLogins.Logins,                        Is.Empty);
                Assert.That(Controller.StationLogins.GetGroup("sneaky"),            Is.Null);
                Assert.That(Controller.StationLogins.GetGroup(LoginGroup.DefaultId), Is.Not.Null);
                Assert.That(Controller.StationLogins.GetGroup(LoginGroup.DefaultId)!.Enabled, Is.True);
            });

        }

        #endregion

        #region AGroupIsMadeNarrowedAndRemoved()

        [Test]
        public async Task AGroupIsMadeNarrowedAndRemoved()
        {

            using var http = await SignedIn();

            var made = await http.PostAsync(
                                 $"{Root}/groups",
                                 JSONBody(
                                     new JProperty("id",                "field-test"),
                                     new JProperty("name",              "Field test"),
                                     new JProperty("enabled",           true),
                                     new JProperty("authMethods",       new JArray("basic", "totp")),
                                     new JProperty("securityProfiles",  new JArray(1, 2))
                                 )
                             );

            Assert.That(made.IsSuccessStatusCode, Is.True, await made.Content.ReadAsStringAsync());

            var group = Controller.StationLogins.GetGroup("field-test");

            Assert.That(group, Is.Not.Null);

            Assert.Multiple(() => {
                Assert.That(group!.AuthMethods,       Is.EquivalentTo(new[] { AuthMethod.Basic, AuthMethod.TOTP }));
                Assert.That(group.SecurityProfiles,   Is.EquivalentTo(new Byte[] { 1, 2 }));
                Assert.That(group.Allows(AuthMethod.Certificate), Is.False);
            });

            // Everything is replaced, not merged - which is what makes taking
            // the last method away possible at all.
            var narrowed = await http.PutAsync(
                                     $"{Root}/groups/field-test",
                                     JSONBody(
                                         new JProperty("name",              "Field test"),
                                         new JProperty("enabled",           true),
                                         new JProperty("authMethods",       new JArray("totp")),
                                         new JProperty("securityProfiles",  new JArray(2))
                                     )
                                 );

            Assert.That(narrowed.IsSuccessStatusCode, Is.True, await narrowed.Content.ReadAsStringAsync());

            Assert.Multiple(() => {
                Assert.That(Controller.StationLogins.GetGroup("field-test")!.Allows(AuthMethod.Basic),  Is.False);
                Assert.That(Controller.StationLogins.GetGroup("field-test")!.Allows((Byte) 1),          Is.False);
            });

            var gone = await http.DeleteAsync($"{Root}/groups/field-test");

            Assert.Multiple(() => {
                Assert.That(gone.IsSuccessStatusCode,                   Is.True);
                Assert.That(Controller.StationLogins.GetGroup("field-test"), Is.Null);
            });

        }

        #endregion

        #region AGroupSomebodyIsStillInIsAConflictAndNotASilentMove()

        [Test]
        public async Task AGroupSomebodyIsStillInIsAConflictAndNotASilentMove()
        {

            using var http = await SignedIn();

            await http.PostAsync($"{Root}/groups",
                                 JSONBody(new JProperty("id",           "field-test"),
                                          new JProperty("authMethods",  new JArray("basic")),
                                          new JProperty("securityProfiles", new JArray(1))));

            await http.PostAsync($"{Root}/stations",
                                 JSONBody(new JProperty("id",     "cs001"),
                                          new JProperty("group",  "field-test")));

            var refused = await http.DeleteAsync($"{Root}/groups/field-test");

            Assert.Multiple(() => {
                Assert.That(refused.StatusCode,                              Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(Controller.StationLogins.GetGroup("field-test"),  Is.Not.Null);
                Assert.That(Controller.StationLogins.Logins.Single().GroupId, Is.EqualTo("field-test"));
            });

        }

        #endregion

        #region ASharedSecretIsHandedOutOnceAndNeverListed()

        [Test]
        public async Task ASharedSecretIsHandedOutOnceAndNeverListed()
        {

            using var http = await SignedIn();

            await http.PostAsync($"{Root}/stations", JSONBody(new JProperty("id", "cs001")));

            var given = await http.PutAsync($"{Root}/stations/cs001/totp", JSONBody());

            Assert.That(given.IsSuccessStatusCode, Is.True, await given.Content.ReadAsStringAsync());

            var answer = JObject.Parse(await given.Content.ReadAsStringAsync());
            var secret = answer.Value<String>("sharedSecret");

            await Assert.MultipleAsync(async () => {

                Assert.That(secret, Is.Not.Null.And.Not.Empty,
                            "No shared secret came back, so nobody can configure the charging station.");

                Assert.That(Controller.StationLogins.TryGet("cs001", out var login) && login.HasTOTP, Is.True);

                // The list the page reads must never carry it, however often it
                // is asked - this is the one credential that works as it stands.
                var listed = await GetJSON(http, $"{Root}/stations");

                Assert.That(listed.ToString(), Does.Not.Contain(secret!));
                Assert.That(listed["stations"]?[0]?.Value<Boolean>("hasTOTP"), Is.True);

            });

        }

        #endregion

        #region ATokenIsTakenAwayAgain()

        [Test]
        public async Task ATokenIsTakenAwayAgain()
        {

            using var http = await SignedIn();

            await http.PostAsync($"{Root}/stations", JSONBody(new JProperty("id", "cs001")));
            await http.PutAsync ($"{Root}/stations/cs001/totp", JSONBody());

            var gone = await http.DeleteAsync($"{Root}/stations/cs001/totp");

            Assert.Multiple(() => {
                Assert.That(gone.IsSuccessStatusCode, Is.True);
                Assert.That(Controller.StationLogins.TryGet("cs001", out var login) && login.HasTOTP, Is.False);
                Assert.That(Controller.StationLogins.TryGet("cs001", out var still) && still.HasPassword, Is.True,
                            "Taking the token away took the password with it.");
            });

        }

        #endregion

        #region ASecretThisControllerWouldThrowOnIsABadRequest()

        [Test]
        public async Task ASecretThisControllerWouldThrowOnIsABadRequest()
        {

            using var http = await SignedIn();

            await http.PostAsync($"{Root}/stations", JSONBody(new JProperty("id", "cs001")));

            var refused = await http.PutAsync(
                                    $"{Root}/stations/cs001/totp",
                                    JSONBody(new JProperty("sharedSecret", "short"))
                                );

            Assert.Multiple(() => {
                Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(Controller.StationLogins.TryGet("cs001", out var login) && login.HasTOTP, Is.False);
            });

        }

        #endregion

        #region TheOverallConfigurationMentionsTheStationServer()

        [Test]
        public async Task TheOverallConfigurationMentionsTheStationServer()
        {

            using var http = await SignedIn();

            var configuration = await GetJSON(http, "/api/v1/configuration");

            Assert.Multiple(() => {
                Assert.That(configuration["stationServer"],                          Is.Not.Null);
                Assert.That(configuration["stationServer"]?.Value<Boolean>("enabled"), Is.False);
                Assert.That(configuration["stationServer"]?.Value<String>("url"),      Does.StartWith("ws://"));
            });

        }

        #endregion

    }

}
