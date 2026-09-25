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
    /// The two tests a page runs one server at a time: a name server asked a
    /// question on its own, and a time server asked everything.
    /// </summary>
    /// <remarks>
    /// Neither leaves this machine. The one name server configured here is on
    /// the loopback address, on a port nothing listens on, with a timeout of
    /// a second and no second try; and the time client stays switched off,
    /// which a time server's test then says rather than asking anybody.
    /// </remarks>
    public class DiagnosticsAPITests : ALocalControllerTests
    {

        #region Data

        /// <summary>
        /// Where the one name server of this controller would be, if anything
        /// listened there.
        /// </summary>
        private readonly UInt16 nameServerPort = TestControllers.FreePort();

        #endregion

        #region Configuration

        protected override JObject Configuration

            => new (
                   new JProperty("nts", new JObject(
                       new JProperty("enabled", false)
                   )),
                   new JProperty("dns", new JObject(
                       new JProperty("servers",     new JArray(
                           new JObject(
                               new JProperty("address",              "127.0.0.1"),
                               new JProperty("port",                 nameServerPort),
                               new JProperty("transport",            "UDP"),
                               new JProperty("queryTimeoutSeconds",  1)
                           )
                       )),
                       new JProperty("maxRetries",  0)
                   ))
               );

        #endregion


        #region (private) Post(HTTP, Path, Properties)

        /// <summary>
        /// One POST as the page sends it, and its answer: the status, and the
        /// body where there is one to read.
        /// </summary>
        private static async Task<(HttpStatusCode Status, JObject? JSON)> Post(HttpClient         HTTP,
                                                                                String             Path,
                                                                                params JProperty[] Properties)
        {

            var response  = await HTTP.PostAsync(Path, JSONBody(Properties));
            var text      = await response.Content.ReadAsStringAsync();

            return (response.StatusCode,
                    text.TrimStart().StartsWith('{') ? JObject.Parse(text) : null);

        }

        #endregion


        #region BothTestsNeedASignIn()

        /// <summary>
        /// Both make this controller send traffic to a host somebody named,
        /// which is not something to hand to whoever finds the port.
        /// </summary>
        [Test]
        public async Task BothTestsNeedASignIn()
        {

            using var http = Anonymous();

            var time  = await Post(http, "/api/v1/configuration/nts/test",  new JProperty("host", "time.example"));
            var name  = await Post(http, "/api/v1/configuration/dns/query", new JProperty("name", "example.org"),
                                                                            new JProperty("server", 0));

            Assert.Multiple(() => {
                Assert.That(time.Status,  Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(name.Status,  Is.EqualTo(HttpStatusCode.Unauthorized));
            });

        }

        #endregion

        #region ANameServerIsAskedOnItsOwn()

        /// <summary>
        /// A question put to one name server names it in the answer - the one
        /// asked, whatever came back.
        /// </summary>
        /// <remarks>
        /// Nothing answers here, which is the case this is for: the lookup that
        /// fails for everybody is the one where somebody wants to know which
        /// server is not answering.
        /// </remarks>
        [Test]
        public async Task ANameServerIsAskedOnItsOwn()
        {

            using var http = await SignedIn();

            var (status, json) = await Post(http, "/api/v1/configuration/dns/query",
                                            new JProperty("name",         "example.org"),
                                            new JProperty("recordTypes",  new JArray("A")),
                                            new JProperty("server",       0));

            Assert.Multiple(() => {
                Assert.That(status,                        Is.EqualTo(HttpStatusCode.OK));
                Assert.That(json?.Value<String>("asked"),  Does.StartWith($"udp://127.0.0.1:{nameServerPort}"),
                            "The answer does not say which name server it asked.");
                Assert.That(json?.Value<Boolean>("ok"),    Is.False,
                            "A name server that is not there answered.");
            });

        }

        #endregion

        #region APlaceWithNoNameServerIsSaidSo()

        /// <summary>
        /// A place in the list with no server at it is an answer that says so,
        /// and not a lookup of all of them.
        /// </summary>
        [Test]
        public async Task APlaceWithNoNameServerIsSaidSo()
        {

            using var http = await SignedIn();

            var (status, json) = await Post(http, "/api/v1/configuration/dns/query",
                                            new JProperty("name",    "example.org"),
                                            new JProperty("server",  3));

            Assert.Multiple(() => {
                Assert.That(status,                        Is.EqualTo(HttpStatusCode.OK));
                Assert.That(json?.Value<Boolean>("ok"),    Is.False);
                Assert.That(json?.Value<String>("error"),  Is.EqualTo("This local controller has no name server number 4."));
            });

        }

        #endregion

        #region APlaceThatIsNotOneIsRefused()

        /// <summary>
        /// Something that is not a place in the list is refused, rather than
        /// read as "all of them".
        /// </summary>
        [Test]
        public async Task APlaceThatIsNotOneIsRefused()
        {

            using var http = await SignedIn();

            var named     = await Post(http, "/api/v1/configuration/dns/query", new JProperty("name", "example.org"), new JProperty("server", "first"));
            var negative  = await Post(http, "/api/v1/configuration/dns/query", new JProperty("name", "example.org"), new JProperty("server", -1));
            var half      = await Post(http, "/api/v1/configuration/dns/query", new JProperty("name", "example.org"), new JProperty("server", 0.5));

            Assert.Multiple(() => {

                foreach (var (what, answer) in new[] { ("a name", named), ("a negative place", negative), ("half a place", half) })
                {
                    Assert.That(answer.Status,                        Is.EqualTo(HttpStatusCode.BadRequest), what);
                    Assert.That(answer.JSON?.Value<String>("error"),  Does.Contain("'server'"),              what);
                }

            });

        }

        #endregion

        #region ATimeServerTestSaysWhyItAskedNobody()

        /// <summary>
        /// With the time client switched off, a test asks nobody and says so -
        /// as its one step, and about the server it was asked to test.
        /// </summary>
        [Test]
        public async Task ATimeServerTestSaysWhyItAskedNobody()
        {

            using var http = await SignedIn();

            var (status, json) = await Post(http, "/api/v1/configuration/nts/test",
                                            new JProperty("host", "time.example"));

            var steps = json?["steps"] as JArray;

            Assert.Multiple(() => {
                Assert.That(status,                         Is.EqualTo(HttpStatusCode.OK));
                Assert.That(json?.Value<String>("host"),    Is.EqualTo("time.example"));
                Assert.That(json?.Value<Boolean>("ok"),     Is.False);
                Assert.That(steps?.Count,                   Is.EqualTo(1));
                Assert.That(steps?[0]?.Value<String>("level"),  Is.EqualTo("error"));
                Assert.That(steps?[0]?.Value<String>("text"),   Is.EqualTo("Time synchronisation is switched off on this local controller, so nothing was asked."));
            });

        }

        #endregion

        #region AHostThatIsNotANameIsRefused()

        /// <summary>
        /// A host has to be written down as one; a number where a name belongs
        /// is refused rather than asked about.
        /// </summary>
        [Test]
        public async Task AHostThatIsNotANameIsRefused()
        {

            using var http = await SignedIn();

            var (status, json) = await Post(http, "/api/v1/configuration/nts/test",
                                            new JProperty("host", 42));

            Assert.Multiple(() => {
                Assert.That(status,                        Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(json?.Value<String>("error"),  Does.Contain("'host'"));
            });

        }

        #endregion

    }

}
