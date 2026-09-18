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
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// One local controller, listening, for the length of one test.
    /// </summary>
    /// <remarks>
    /// A whole controller per test rather than one for the fixture, because
    /// half of what is worth testing here changes the controller: a test that
    /// repoints the name servers must not decide what the next test reads. One
    /// costs a few hundred milliseconds, which is cheaper than the morning
    /// spent on a test that only fails when it runs second.
    ///
    /// Each one gets a directory of its own for the two files it writes, and a
    /// port the operating system has just confirmed is free - so a developer
    /// with a controller running on 2350 can still run the tests.
    ///
    /// **Nothing here reaches the network.** The configuration written before
    /// the controller is built switches the time client off, which is what
    /// stops the clock check from ever being scheduled; the DNS client is only
    /// ever asked what it is configured as, never to resolve anything. Tests
    /// that want a name looked up or a time server asked belong somewhere that
    /// is allowed to be offline and fail.
    /// </remarks>
    public abstract class ALocalControllerTests
    {

        #region Properties

        /// <summary>
        /// The controller under test, listening, from SetUp until TearDown.
        /// </summary>
        protected LocalController  Controller   { get; private set; } = default!;

        /// <summary>
        /// The password this controller made up for itself at its first start.
        /// </summary>
        protected String           Password     { get; private set; } = default!;

        /// <summary>
        /// Where it lives: "http://127.0.0.1:&lt;port&gt;/".
        /// </summary>
        protected String           BaseURL      { get; private set; } = default!;

        /// <summary>
        /// The directory holding its accounts and its configuration, removed
        /// again in TearDown.
        /// </summary>
        protected String           Directory    { get; private set; } = default!;

        #endregion

        #region What this controller is made of

        /// <summary>
        /// What its configuration file says before it is built.
        /// </summary>
        /// <remarks>
        /// Overridden by a fixture that needs a controller with something on
        /// it. The time client stays switched off in all of them, which is what
        /// keeps a test run off the network.
        /// </remarks>
        protected virtual JObject Configuration
            => TestControllers.Offline;

        /// <summary>
        /// Where it reads the time, or null for the system clock.
        /// </summary>
        /// <remarks>
        /// Overridden by a fixture that has to decide what time it is. A
        /// session expires twelve hours after it was last used, and a test that
        /// waited for that would be a test nobody runs.
        /// </remarks>
        protected virtual TimeProvider? Clock
            => null;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task StartTheController()
        {

            Directory   = TestControllers.TemporaryDirectory("tests");

            Controller  = TestControllers.New(Directory, Configuration, Clock);

            BaseURL     = Controller.WebInterfaceURL.ToString();

            await Controller.Start();

            // After Start(), because that is what makes the account. Null would
            // mean accounts were already there, and the directory is new.
            Password    = Controller.GeneratedPassword
                              ?? throw new InvalidOperationException("The controller did not make up a password for its first start!");

        }

        [TearDown]
        public async Task StopTheController()
        {

            if (Controller is not null)
                await Controller.DisposeAsync();

            TestControllers.Remove(Directory);

        }

        #endregion


        #region (protected) Anonymous()

        /// <summary>
        /// A browser that has not signed in.
        /// </summary>
        protected HttpClient Anonymous()

            => new (new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) {
                   BaseAddress = new Uri(BaseURL)
               };

        #endregion

        #region (protected) SignedIn()

        /// <summary>
        /// A browser that has signed in with the password this controller made
        /// up, carrying the session cookie from here on.
        /// </summary>
        /// <remarks>
        /// At the HTTPExt API and not at the JSON API: the password store is
        /// private to the HTTPExt API, so "/ext/login" is the only door that
        /// can check one. What it sets is the cookie the JSON API reads.
        /// </remarks>
        protected async Task<HttpClient> SignedIn()

            => await SignedInAs(LocalController.DefaultAdminUser, Password);

        #endregion

        #region (protected) SignedInAs(Login, Password)

        /// <summary>
        /// A browser that has tried to sign in as somebody in particular, for
        /// a test that wants a second account or a wrong password.
        /// </summary>
        protected async Task<HttpClient> SignedInAs(String Login, String Password)
        {

            var http      = Anonymous();

            var response  = await http.PostAsync(SignInPath, LoginBody(Login, Password));

            Assert.That(response.IsSuccessStatusCode, Is.True,
                        $"Signing in failed with {(Int32) response.StatusCode}, and every assertion below it would say so instead.");

            return http;

        }

        #endregion

        #region (protected static) SignInPath / LoginBody(Login, Password)

        /// <summary>
        /// Where a password is checked: the HTTPExt API's own sign-in.
        /// </summary>
        protected static String SignInPath

            => $"{LocalController.ExtAPIPath.ToString().TrimEnd('/')}/login";

        /// <summary>
        /// A sign-in body, as the web interface sends one: form-urlencoded,
        /// and the field is called "login" rather than "username".
        /// </summary>
        protected static FormUrlEncodedContent LoginBody(String Login, String Password)

            => new ([
                   new KeyValuePair<String, String>("login",     Login),
                   new KeyValuePair<String, String>("password",  Password)
               ]);

        #endregion

        #region (protected static) JSONBody(...)

        /// <summary>
        /// A request body, as the web interface sends one.
        /// </summary>
        protected static StringContent JSONBody(params JProperty[] Properties)

            => new (new JObject(Properties).ToString(),
                    Encoding.UTF8,
                    "application/json");

        #endregion

        #region (protected static) GetJSON(HTTP, Path)

        /// <summary>
        /// One GET, with the answer parsed and the status checked - so that a
        /// test which is about what a resource says does not also have to say
        /// what a 500 looks like.
        /// </summary>
        protected static async Task<JObject> GetJSON(HttpClient  HTTP,
                                                     String      Path)
        {

            var response = await HTTP.GetAsync(Path);

            Assert.That(response.IsSuccessStatusCode, Is.True,
                        $"GET {Path} answered {(Int32) response.StatusCode}.");

            return JObject.Parse(await response.Content.ReadAsStringAsync());

        }

        #endregion

    }

}
