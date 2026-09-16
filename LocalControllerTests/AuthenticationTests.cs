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

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Who may ask this local controller anything, and what happens to
    /// everybody else.
    /// </summary>
    public class AuthenticationTests : ALocalControllerTests
    {

        #region TheAPIRefusesWithoutASession()

        [Test]
        public async Task TheAPIRefusesWithoutASession()
        {

            using var http = Anonymous();

            var response = await http.GetAsync("/api/v1/status");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        }

        #endregion

        #region AnUnknownAPIPathAnswersJSONAndNotTheStub()

        /// <summary>
        /// The JSON API lives in its own HTTPAPI so that a mistyped API path
        /// never falls through to the single-page-application stub - a browser
        /// that asked for JSON and got HTML with status 200 has no way to tell
        /// what went wrong.
        /// </summary>
        [Test]
        public async Task AnUnknownAPIPathAnswersJSONAndNotTheStub()
        {

            using var http = Anonymous();

            var response = await http.GetAsync("/api/v1/nonsense");

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,                              Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(response.Content.Headers.ContentType?.MediaType,  Is.EqualTo("application/json"));
            });

        }

        #endregion

        #region AWrongPasswordIsRefused()

        [Test]
        public async Task AWrongPasswordIsRefused()
        {

            using var http = Anonymous();

            var response = await http.PostAsync(
                                     "/api/v1/auth/login",
                                     JSONBody(
                                         new JProperty("username", Controller.Sessions.Username),
                                         new JProperty("password", "not the password")
                                     )
                                 );

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        }

        #endregion

        #region AWrongUsernameIsRefused()

        [Test]
        public async Task AWrongUsernameIsRefused()
        {

            using var http = Anonymous();

            var response = await http.PostAsync(
                                     "/api/v1/auth/login",
                                     JSONBody(
                                         new JProperty("username", "somebody-else"),
                                         new JProperty("password", Password)
                                     )
                                 );

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        }

        #endregion

        #region TheGeneratedPasswordSignsIn()

        /// <summary>
        /// The password this controller made up at its first start, shown once
        /// on the console and kept nowhere but in its hash, opens the web
        /// interface.
        /// </summary>
        [Test]
        public async Task TheGeneratedPasswordSignsIn()
        {

            using var http = Anonymous();

            var response = await http.PostAsync(
                                     "/api/v1/auth/login",
                                     JSONBody(
                                         new JProperty("username", Controller.Sessions.Username),
                                         new JProperty("password", Password)
                                     )
                                 );

            var me = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.Multiple(() => {
                Assert.That(response.IsSuccessStatusCode,   Is.True);
                Assert.That(me.Value<String>("username"),   Is.EqualTo(Controller.Sessions.Username));
            });

        }

        #endregion

        #region TheSessionSaysWhatItMayDo()

        /// <summary>
        /// A first start signs in as the system administrator, because there is
        /// nobody else yet to hand the rest to. What the browser is told is a
        /// copy of what the controller enforces and not the enforcement itself;
        /// this is the copy.
        /// </summary>
        [Test]
        public async Task TheSessionSaysWhatItMayDo()
        {

            using var http = await SignedIn();

            var me = await GetJSON(http, "/api/v1/auth/me");

            var roles        = me["roles"]?.      Values<String>().ToArray() ?? [];
            var permissions  = me["permissions"]?.Values<String>().ToArray() ?? [];

            Assert.Multiple(() => {
                Assert.That(roles,       Is.EquivalentTo(new[] { "systemadmin" }));
                Assert.That(permissions, Is.EquivalentTo(new[] { "readConfiguration",
                                                                 "changeNetworkSettings",
                                                                 "runDiagnostics",
                                                                 "changeStationSettings",
                                                                 "manageCertificates" }));
            });

        }

        #endregion

        #region SigningOutEndsTheSession()

        [Test]
        public async Task SigningOutEndsTheSession()
        {

            using var http = await SignedIn();

            Assert.That((await http.GetAsync("/api/v1/auth/me")).IsSuccessStatusCode, Is.True,
                        "The session was not live before it was ended.");

            var logout = await http.PostAsync("/api/v1/auth/logout", null);

            Assert.Multiple(() => {
                Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(Controller.Sessions.Count, Is.EqualTo(0));
            });

            Assert.That((await http.GetAsync("/api/v1/auth/me")).StatusCode,
                        Is.EqualTo(HttpStatusCode.Unauthorized),
                        "The cookie still opened the controller after signing out.");

        }

        #endregion

        #region ACrossSiteChangeIsRefused()

        /// <summary>
        /// The session cookie is SameSite=strict, so a cross-site request would
        /// arrive without a session anyway. This is the second lock on the same
        /// door: browsers say where a request came from, and a state-changing
        /// request from anywhere but this origin is refused before it is read.
        /// </summary>
        [Test]
        public async Task ACrossSiteChangeIsRefused()
        {

            using var http = await SignedIn();

            var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/configuration/dns") {
                              Content = JSONBody(new JProperty("useCache", false))
                          };

            request.Headers.Add("Sec-Fetch-Site", "cross-site");

            var response = await http.SendAsync(request);

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,          Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(Controller.DNSClient.UseCache, Is.True,
                            "The refused request changed something anyway.");
            });

        }

        #endregion

        #region ASameOriginChangeIsNotRefused()

        /// <summary>
        /// The other half of the test above: the header that the controller
        /// looks at is the one a browser sets for its own page, and that one
        /// has to go through - or the lock is on the wrong door.
        /// </summary>
        [Test]
        public async Task ASameOriginChangeIsNotRefused()
        {

            using var http = await SignedIn();

            var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/configuration/dns") {
                              Content = JSONBody(new JProperty("useCache", false))
                          };

            request.Headers.Add("Sec-Fetch-Site", "same-origin");

            var response = await http.SendAsync(request);

            Assert.Multiple(() => {
                Assert.That(response.IsSuccessStatusCode,   Is.True);
                Assert.That(Controller.DNSClient.UseCache,  Is.False);
            });

        }

        #endregion

    }

}
