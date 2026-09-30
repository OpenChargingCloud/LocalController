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
    /// The routes the CSMS page sits on, while the files behind them cannot
    /// be written.
    /// </summary>
    /// <remarks>
    /// The controller under here dials no CSMS: nothing it is told here
    /// switches the connection on.
    /// </remarks>
    public class CSMSAPITests : ALocalControllerTests
    {

        #region (private) Root

        private const String Root = "/api/v1/configuration/csms";

        #endregion


        #region AChangeItsFileCannotTakeIsAServerError(Change)

        /// <summary>
        /// A change of the CSMS connection, or of what this controller signs in
        /// to the CSMS with, that is fine in itself, and that its file cannot be
        /// written with, is answered 500 with why - and changes nothing. It was
        /// a 400, as if something had been wrong with it.
        /// </summary>
        [TestCase("PUT csms")]
        [TestCase("PUT csms/credentials, password")]
        [TestCase("PUT csms/credentials, sharedSecret")]
        [TestCase("DELETE csms/credentials")]
        public async Task AChangeItsFileCannotTakeIsAServerError(String Change)
        {

            using var http  = await SignedIn();

            // What makes the removal a change: something to remove.
            var set         = await http.PutAsync($"{Root}/credentials", JSONBody(new JProperty("username", "lc001"),
                                                                                  new JProperty("password", "a password the CSMS issued")));

            Assert.That(set.IsSuccessStatusCode, Is.True, await set.Content.ReadAsStringAsync());

            var file        = Change == "PUT csms" ? Controller.ConfigFile.Path : Controller.CSMSLogin.Path;
            var before      = (await GetJSON(http, Root)).ToString();

            // Where the file's next version is written first is a directory.
            System.IO.Directory.CreateDirectory(file + ".tmp");

            var response    = Change switch {
                "PUT csms"                            => await http.PutAsync   (Root,                   JSONBody(new JProperty("pingEvery", 42))),
                "PUT csms/credentials, password"      => await http.PutAsync   ($"{Root}/credentials",  JSONBody(new JProperty("username", "lc002"), new JProperty("password",     "another password the CSMS issued"))),
                "PUT csms/credentials, sharedSecret"  => await http.PutAsync   ($"{Root}/credentials",  JSONBody(new JProperty("username", "lc002"), new JProperty("sharedSecret", "aSharedSecretTheCSMSIssued"))),
                "DELETE csms/credentials"             => await http.DeleteAsync($"{Root}/credentials"),
                _                                     => throw new ArgumentException($"No change '{Change}' here.", nameof(Change))
            };

            var body        = await response.Content.ReadAsStringAsync();
            var after       = (await GetJSON(http, Root)).ToString();

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,                         Is.EqualTo(HttpStatusCode.InternalServerError), body);
                Assert.That(JObject.Parse(body).Value<String>("error"),  Does.StartWith($"'{file}' could not be written: "));
                Assert.That(after,                                       Is.EqualTo(before), "what the page shows");
            });

        }

        #endregion

        #region ARefusalIsWhatItWasWhileItsFileCannotBeWritten()

        /// <summary>
        /// What was wrong with a change is answered as it was while the files
        /// cannot be written: a 500 is the files', and only where it was the
        /// files that refused.
        /// </summary>
        [Test]
        public async Task ARefusalIsWhatItWasWhileItsFileCannotBeWritten()
        {

            using var http  = await SignedIn();

            System.IO.Directory.CreateDirectory(Controller.ConfigFile.Path + ".tmp");
            System.IO.Directory.CreateDirectory(Controller.CSMSLogin.Path  + ".tmp");

            var profile     = await http.PutAsync(Root,                  JSONBody(new JProperty("securityProfile", 4)));
            var tooShort    = await http.PutAsync($"{Root}/credentials", JSONBody(new JProperty("username", "lc002"), new JProperty("password",     "short")));
            var shortSecret = await http.PutAsync($"{Root}/credentials", JSONBody(new JProperty("username", "lc002"), new JProperty("sharedSecret", "short")));

            Assert.Multiple(() => {
                Assert.That(profile.    StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest),  "a security profile there is none of");
                Assert.That(tooShort.   StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest),  "a password too short");
                Assert.That(shortSecret.StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest),  "a shared secret too short");
            });

        }

        #endregion

    }

}
