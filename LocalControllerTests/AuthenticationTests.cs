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

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.LocalController.Web;

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

        #region TheJSONAPIHasNoSignIn()

        /// <summary>
        /// The one door that can check a password is the HTTPExt API's, and
        /// this API has no second one onto the same credentials.
        /// </summary>
        [Test]
        public async Task TheJSONAPIHasNoSignIn()
        {

            using var http = Anonymous();

            var response = await http.PostAsync(
                                     "/api/v1/auth/login",
                                     LoginBody(LocalController.DefaultAdminUser, Password)
                                 );

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,  Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(response.Content.Headers.ContentType?.MediaType,  Is.EqualTo("application/json"));
            });

        }

        #endregion

        #region EveryRoleHasItsGroup()

        /// <summary>
        /// One user group per role, made at every start - because a role is
        /// that group, and nothing else.
        /// </summary>
        /// <remarks>
        /// This is the failure that does not announce itself. AddUserGroup
        /// answers with a result rather than throwing, and the HTTPExt API's
        /// own floor for a group identification is four characters - so "cpo",
        /// which is three, was refused and simply did not exist. What that
        /// leaves is a role nobody can ever hold: the pages that ask for it
        /// refuse everybody, correct password and all, and there is nothing
        /// anywhere that says why.
        /// </remarks>
        [Test]
        public void EveryRoleHasItsGroup()
        {

            Assert.Multiple(() => {

                foreach (var role in UserRole.All)
                    Assert.That(Controller.ExtAPI.TryGetUserGroup(role.GroupId, out _), Is.True,
                                $"The '{role.Name}' role has no user group, so nobody can ever hold it.");

            });

        }

        #endregion

        #region NoGroupOfTheVehiclesRoles()

        /// <summary>
        /// The groups are the controller's roles and not the vehicle's.
        /// </summary>
        /// <remarks>
        /// The node below makes a group for every role its kind hands it, and
        /// a node handed none knows the vehicle's. A controller that did not
        /// hand its own over would have a "driver" and a "service" group
        /// nobody here has a use for - and no "cpo", which the test above
        /// catches from the other side.
        /// </remarks>
        [Test]
        public void NoGroupOfTheVehiclesRoles()
        {

            Assert.Multiple(() => {

                foreach (var vehicles in new[] { "driver", "service" })
                    Assert.That(Controller.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(vehicles), out _), Is.False,
                                $"A local controller has the vehicle's '{vehicles}' group.");

            });

        }

        #endregion

        #region AWrongPasswordIsRefused()

        [Test]
        public async Task AWrongPasswordIsRefused()
        {

            using var http = Anonymous();

            var response = await http.PostAsync(
                                     SignInPath,
                                     LoginBody(LocalController.DefaultAdminUser, "not the password")
                                 );

            Assert.Multiple(() => {

                Assert.That(response.IsSuccessStatusCode, Is.False);

                // And nothing was let in on the strength of it: the cookie is
                // what a sign-in hands out, and a refusal that still handed one
                // out would pass the status check above and open the door.
                Assert.That(http.GetAsync("/api/v1/status").Result.StatusCode,
                            Is.EqualTo(HttpStatusCode.Unauthorized));

            });

        }

        #endregion

        #region AWrongUsernameIsRefused()

        [Test]
        public async Task AWrongUsernameIsRefused()
        {

            using var http = Anonymous();

            var response = await http.PostAsync(
                                     SignInPath,
                                     LoginBody("somebody-else", Password)
                                 );

            Assert.Multiple(() => {

                Assert.That(response.IsSuccessStatusCode, Is.False);

                Assert.That(http.GetAsync("/api/v1/status").Result.StatusCode,
                            Is.EqualTo(HttpStatusCode.Unauthorized));

            });

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

            using var http = await SignedIn();

            var me = await GetJSON(http, "/api/v1/auth/me");

            Assert.That(me.Value<String>("username"), Is.EqualTo(LocalController.DefaultAdminUser));

        }

        #endregion

        #region TheAccountSurvivesARestart()

        /// <summary>
        /// The accounts are read back at the next start, so the password
        /// somebody wrote down still works - and no second root is made up
        /// beside the first.
        /// </summary>
        /// <remarks>
        /// The one that would not announce itself: without LoadDatabase the
        /// store is empty at every start, the first start happens forever, and
        /// the password on the console changes while the one in somebody's
        /// notebook stops working.
        /// </remarks>
        [Test]
        public async Task TheAccountSurvivesARestart()
        {

            // Stopped rather than disposed: TearDown disposes this one, and the
            // accounts are written with File.AppendAllText, which holds no
            // handle for the second controller to trip over.
            await Controller.Stop();

            var again = TestControllers.New(Directory, Configuration, Clock);

            try
            {

                await again.Start();

                Assert.That(again.GeneratedPassword, Is.Null,
                            "A second password was made up, so the accounts of the first start were not read back.");

                using var http      = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) {
                                          BaseAddress = new Uri(again.WebInterfaceURL.ToString())
                                      };

                var       signIn    = await http.PostAsync(SignInPath, LoginBody(LocalController.DefaultAdminUser, Password));

                Assert.That(signIn.IsSuccessStatusCode, Is.True,
                            "The password from the first start no longer opens the controller.");

                var me = await GetJSON(http, "/api/v1/auth/me");

                Assert.Multiple(() => {
                    Assert.That(again.ExtAPI.Users.Count(),          Is.EqualTo(1));
                    Assert.That(me["roles"]?.Values<String>(),       Is.EquivalentTo(new[] { "systemadmin" }));
                });

            }
            finally
            {
                await again.DisposeAsync();
            }

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
                Assert.That(logout.StatusCode,               Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(Controller.ExtAPI.Sessions.Count(), Is.EqualTo(0),
                            "The session was only forgotten by this browser, not ended where it lives.");
            });

            Assert.That((await http.GetAsync("/api/v1/auth/me")).StatusCode,
                        Is.EqualTo(HttpStatusCode.Unauthorized),
                        "The cookie still opened the controller after signing out.");

        }

        #endregion

        #region SigningOutExpiresTheCookieIn1970()

        /// <summary>
        /// The browser is told to drop the cookie, rather than merely not
        /// being given a new one.
        /// </summary>
        /// <remarks>
        /// The HTTPExt API sets its cookies inside its own handlers and has
        /// nothing to hand one out, so the expiry is written on this side -
        /// which is why it is worth a test on this side.
        /// </remarks>
        [Test]
        public async Task SigningOutExpiresTheCookieIn1970()
        {

            using var http = await SignedIn();

            var logout  = await http.PostAsync("/api/v1/auth/logout", null);

            var cookie  = logout.Headers.TryGetValues("Set-Cookie", out var values)
                              ? values.FirstOrDefault(value => value.StartsWith(Controller.ExtAPI.SessionCookieName.ToString(), StringComparison.Ordinal))
                              : null;

            Assert.That(cookie, Is.Not.Null, "Signing out did not tell the browser to drop the cookie.");

            Assert.Multiple(() => {
                Assert.That(cookie, Does.Contain("Expires=Thu, 01 Jan 1970"));
                Assert.That(cookie, Does.Contain("Path=/"));
                Assert.That(cookie, Does.Contain("HttpOnly"));
            });

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
