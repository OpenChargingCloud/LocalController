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

using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The log itself: what it keeps, what it throws away, and what it hands on.
    /// </summary>
    public class LoggingTests
    {

        #region EntriesAreNumberedFromOneAndOnlyUpwards()

        /// <summary>
        /// The number is what lets a browser tell what it has already seen, so
        /// it may never go backwards or repeat.
        /// </summary>
        [Test]
        public void EntriesAreNumberedFromOneAndOnlyUpwards()
        {

            var log = new EventLog();

            var first  = log.Info("the first");
            var second = log.Info("the second");

            Assert.Multiple(() => {
                Assert.That(first.Id,   Is.EqualTo(1));
                Assert.That(second.Id,  Is.EqualTo(2));
                Assert.That(log.LastId, Is.EqualTo(2));
            });

        }

        #endregion

        #region TheClockIsTheOneTheLogWasHanded()

        /// <summary>
        /// A log whose times come from somewhere else than the controller it
        /// belongs to is a log that cannot be held against anything.
        /// </summary>
        [Test]
        public void TheClockIsTheOneTheLogWasHanded()
        {

            var clock = TestClock.At(2026, 3, 4);
            var log   = new EventLog(TimeProvider: clock);

            var before = log.Info("before");

            clock.Advance(TimeSpan.FromHours(5));

            var after  = log.Info("after");

            Assert.Multiple(() => {
                Assert.That(before.Timestamp, Is.EqualTo(new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero)));
                Assert.That(after.Timestamp,  Is.EqualTo(new DateTimeOffset(2026, 3, 4, 5, 0, 0, TimeSpan.Zero)));
            });

        }

        #endregion

        #region TheOldestEntriesFallOutAtCapacity()

        [Test]
        public void TheOldestEntriesFallOutAtCapacity()
        {

            var log = new EventLog(Capacity: 10);

            for (var i = 1; i <= 25; i++)
                log.Info($"entry {i}");

            var kept = log.Recent(100).ToArray();

            Assert.Multiple(() => {
                Assert.That(log.Count,               Is.EqualTo(10));
                Assert.That(log.LastId,              Is.EqualTo(25), "The numbering restarted when entries fell out.");
                Assert.That(kept.First().Message,    Is.EqualTo("entry 16"));
                Assert.That(kept.Last().Message,     Is.EqualTo("entry 25"));
            });

        }

        #endregion

        #region ALogHasToBeAbleToKeepSomething()

        [Test]
        public void ALogHasToBeAbleToKeepSomething()
            => Assert.Throws<ArgumentOutOfRangeException>(() => new EventLog(Capacity: 0));

        #endregion

        #region TagsAreTidiedAndRemembered()

        /// <summary>
        /// Lower case, trimmed, without empties and without repeats, in the
        /// order they were given - so that a filter box matches what a page
        /// offers.
        /// </summary>
        [Test]
        public void TagsAreTidiedAndRemembered()
        {

            var log   = new EventLog();

            var entry = log.Info("something happened", "  OCPP ", "dns", "OCPP", "", "   ");

            Assert.Multiple(() => {
                Assert.That(entry.Tags,     Is.EqualTo(new[] { "ocpp", "dns" }));
                Assert.That(log.KnownTags,  Is.EquivalentTo(new[] { "ocpp", "dns" }));
            });

        }

        #endregion

        #region TheLevelCountsAsATag()

        /// <summary>
        /// Which is how "critical" and "ocpp" can be typed into the same box.
        /// </summary>
        [Test]
        public void TheLevelCountsAsATag()
        {

            var log   = new EventLog();
            var entry = log.Warning("careful", "dns");

            Assert.Multiple(() => {
                Assert.That(entry.Matches("warning"), Is.True);
                Assert.That(entry.Matches("dns"),     Is.True);
                Assert.That(entry.Matches("DNS"),     Is.True, "A tag was matched case-sensitively.");
                Assert.That(entry.Matches("ocpp"),    Is.False);
                Assert.That(entry.Matches(""),        Is.True, "An empty filter should match everything.");
                Assert.That(entry.Tags,               Does.Not.Contain("warning"),
                            "The level was stored as a tag rather than counted as one.");
            });

        }

        #endregion

        #region RecentReadsFromTheNewEndAndKeepsTheOldOrder()

        /// <summary>
        /// The interesting end of a log is the new one; the order within the
        /// page stays oldest first, which is how a log reads and how a browser
        /// appends it.
        /// </summary>
        [Test]
        public void RecentReadsFromTheNewEndAndKeepsTheOldOrder()
        {

            var log = new EventLog();

            for (var i = 1; i <= 10; i++)
                log.Info($"entry {i}");

            var page = log.Recent(3).ToArray();

            Assert.That(page.Select(entry => entry.Message),
                        Is.EqualTo(new[] { "entry 8", "entry 9", "entry 10" }));

        }

        #endregion

        #region RecentFiltersByWhatCameAfterAndByTag()

        [Test]
        public void RecentFiltersByWhatCameAfterAndByTag()
        {

            var log = new EventLog();

            log.Info   ("one",   "a");
            log.Warning("two",   "b");
            log.Info   ("three", "a");

            Assert.Multiple(() => {

                Assert.That(log.Recent(100, After: 1).Select(entry => entry.Message),
                            Is.EqualTo(new[] { "two", "three" }));

                Assert.That(log.Recent(100, Tag: "a").Select(entry => entry.Message),
                            Is.EqualTo(new[] { "one", "three" }));

                Assert.That(log.Recent(100, After: 1, Tag: "a").Select(entry => entry.Message),
                            Is.EqualTo(new[] { "three" }));

                Assert.That(log.Recent(0), Is.Empty, "A page of no entries should be no entries.");

            });

        }

        #endregion

        #region AnEntryCarriesWhateverElseBelongsToIt()

        [Test]
        public void AnEntryCarriesWhateverElseBelongsToIt()
        {

            var log   = new EventLog();

            var entry = log.Log(LogLevel.Error,
                                "it went wrong",
                                new JObject(new JProperty("code", 42)),
                                "ocpp");

            var json  = entry.ToJSON();

            Assert.Multiple(() => {
                Assert.That(json.Value<String>("message"),      Is.EqualTo("it went wrong"));
                Assert.That(json.Value<String>("level"),        Is.EqualTo("error"));
                Assert.That(json["data"]?.Value<Int32>("code"),  Is.EqualTo(42));
                Assert.That(json["tags"]?.Values<String>(),      Does.Contain("ocpp"));
            });

        }

        #endregion

        #region AnExceptionIsLoggedWithWhatIsBehindIt()

        [Test]
        public void AnExceptionIsLoggedWithWhatIsBehindIt()
        {

            var log = new EventLog();

            var entry = log.Exception(new InvalidOperationException("the cause"), "it failed", "ocpp");

            Assert.Multiple(() => {
                Assert.That(entry.Level,                            Is.EqualTo(LogLevel.Error));
                Assert.That(entry.Message,                          Does.Contain("it failed"));
                Assert.That(entry.Message,                          Does.Contain("the cause"));
                Assert.That(entry.Data?.Value<String>("exception"), Does.Contain("InvalidOperationException"));
            });

        }

        #endregion

        #region EveryNewEntryIsHandedOnAtOnce()

        [Test]
        public void EveryNewEntryIsHandedOnAtOnce()
        {

            var log  = new EventLog();
            var seen = new List<LogEntry>();

            log.OnLogged += entry => seen.Add(entry);

            log.Info("one");
            log.Info("two");

            Assert.That(seen.Select(entry => entry.Message), Is.EqualTo(new[] { "one", "two" }));

        }

        #endregion

        #region AListenerThatThrowsDoesNotSwallowTheEntry()

        /// <summary>
        /// A broken listener must not be able to take the log down with it, nor
        /// stop the other listeners from hearing.
        /// </summary>
        [Test]
        public void AListenerThatThrowsDoesNotSwallowTheEntry()
        {

            var log      = new EventLog();
            var theOther = new List<String>();

            log.OnLogged += _ => throw new InvalidOperationException("this listener is broken");
            log.OnLogged += entry => theOther.Add(entry.Message);

            Assert.DoesNotThrow(() => log.Info("still logged"));

            Assert.Multiple(() => {
                Assert.That(log.Count,  Is.EqualTo(1));
                Assert.That(theOther,   Is.EqualTo(new[] { "still logged" }));
            });

        }

        #endregion

        #region EveryLevelHasItsOwnWayIn()

        [Test]
        public void EveryLevelHasItsOwnWayIn()
        {

            var log = new EventLog();

            log.Debug   ("d");
            log.Info    ("i");
            log.Notice  ("n");
            log.Warning ("w");
            log.Error   ("e");
            log.Critical("c");

            Assert.That(log.Recent(100).Select(entry => entry.Level),
                        Is.EqualTo(new[] { LogLevel.Debug,   LogLevel.Info,  LogLevel.Notice,
                                           LogLevel.Warning, LogLevel.Error, LogLevel.Critical }));

        }

        #endregion

        #region AnEntryReadsTheSameWayOnTheConsole()

        [Test]
        public void AnEntryReadsTheSameWayOnTheConsole()
        {

            var log   = new EventLog(TimeProvider: TestClock.At(2026, 5, 6));
            var entry = log.Notice("something worth finding again", "ocpp", "http");

            var line  = entry.ToString();

            Assert.Multiple(() => {
                Assert.That(line, Does.Contain("notice"));
                Assert.That(line, Does.Contain("ocpp http"));
                Assert.That(line, Does.Contain("something worth finding again"));
            });

        }

        #endregion

    }

}
