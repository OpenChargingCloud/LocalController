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

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// What happened inside this local controller, as a browser reads it: a
    /// snapshot of what led up to now, and then everything from now on over one
    /// Server-Sent Events stream.
    /// </summary>
    public class EventLogTests : ALocalControllerTests
    {

        #region TheSnapshotCarriesWhatHappened()

        [Test]
        public async Task TheSnapshotCarriesWhatHappened()
        {

            using var http = await SignedIn();

            Controller.Log.Notice("A line written by a test.", "test");

            var page     = await GetJSON(http, "/api/v1/logs?limit=500");
            var entries  = page["entries"] as JArray ?? [];

            Assert.Multiple(() => {

                Assert.That(entries.Count,                 Is.GreaterThan(0));
                Assert.That(page.Value<UInt64>("lastId"),  Is.GreaterThan(0));

                Assert.That(entries.Any(entry => entry.Value<String>("message") == "A line written by a test."),
                            Is.True,
                            "The line this test wrote is not in the snapshot it asked for.");

                // Every tag seen since the start, so that the Logs page can
                // offer them instead of asking somebody to guess.
                Assert.That(page["tags"]?.Values<String>(), Does.Contain("test"));

            });

        }

        #endregion

        #region ATagFiltersTheLog()

        /// <summary>
        /// The level counts as a tag, which is how "critical" and "ocpp" can be
        /// typed into the same filter box.
        /// </summary>
        [Test]
        public async Task ATagFiltersTheLog()
        {

            using var http = await SignedIn();

            Controller.Log.Notice ("The first of two.",  "marked");
            Controller.Log.Warning("The second of two.", "marked");
            Controller.Log.Info   ("Not one of them.",   "elsewhere");

            var byTag   = await GetJSON(http, "/api/v1/logs?tag=marked");
            var byLevel = await GetJSON(http, "/api/v1/logs?tag=warning");

            var newestOnThePage = (byTag["entries"] as JArray)?.Max(entry => entry.Value<UInt64>("id")) ?? 0;

            Assert.Multiple(() => {

                Assert.That((byTag["entries"] as JArray)?.Count, Is.EqualTo(2));

                Assert.That((byLevel["entries"] as JArray)?.Any(entry => entry.Value<String>("message") == "The second of two."),
                            Is.True,
                            "Filtering by a level found nothing, so the level is not counting as a tag.");

                // The whole log's last id and not the newest entry of this
                // page: a page filtered by a tag would otherwise make the
                // browser ask again for everything between the two. The
                // unmatched line above was written after both matches, so the
                // two numbers have to differ.
                //
                // Not compared against Log.LastId as it stands now, which is a
                // race this test would lose: answering the request is itself
                // something the controller writes to its log.
                Assert.That(byTag.Value<UInt64>("lastId"), Is.GreaterThan(newestOnThePage),
                            "The page reports its own newest entry as the last id of the log.");

            });

        }

        #endregion

        #region AnImpossiblePageSizeIsRefused()

        [Test]
        public async Task AnImpossiblePageSizeIsRefused()
        {

            using var http = await SignedIn();

            var response = await http.GetAsync("/api/v1/logs?limit=0");

            Assert.That((Int32) response.StatusCode, Is.EqualTo(400));

        }

        #endregion

        #region TheEventStreamDeliversWhatHappensNext()

        /// <summary>
        /// The stream is what makes the Logs page a live one. A browser loads
        /// the snapshot, which says how far it reaches, and then applies
        /// everything from the stream with a greater id.
        /// </summary>
        [Test]
        public async Task TheEventStreamDeliversWhatHappensNext()
        {

            using var http = await SignedIn();

            using var stream = await EventStream.Open(http);

            // Written after the stream is open, so that it is the stream
            // delivering it and not the cache of events being replayed to a
            // client that has just connected.
            const String message = "A line that has to arrive over the stream.";

            Controller.Log.Notice(message, "test");

            Assert.That(await stream.ReadUntil(message), Is.True,
                        $"'{message}' did not arrive over the event stream within {EventStream.Timeout.TotalSeconds} seconds.");

        }

        #endregion

        #region TheEventStreamNeedsASession()

        [Test]
        public async Task TheEventStreamNeedsASession()
        {

            using var http = Anonymous();

            var response = await http.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);

            Assert.That((Int32) response.StatusCode, Is.EqualTo(401));

        }

        #endregion


    }

}
