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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Norn.NTS;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// What a local controller makes of an "nts" section: the section put into
    /// effect on top of the group of time servers it already has.
    /// </summary>
    /// <remarks>
    /// Beside the tests of the section on its own, because the rule that
    /// matters here - what a section does not mention is left as it is - can
    /// only be seen against something that is already there.
    ///
    /// The controllers are built and never started. The constructor is what
    /// applies the file, and starting would open a port and make up an account
    /// for nothing.
    /// </remarks>
    public class NTSGroupInEffectTests
    {

        #region Data

        private String directory = default!;

        private String ConfigurationPath
            => Path.Combine(directory, "configuration.json");

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("nts-in-effect");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void RemoveTheDirectory()
            => TestControllers.Remove(directory);

        #endregion


        #region (private) Controller(Configuration = null)

        /// <summary>
        /// A local controller as it stands after reading this configuration
        /// file, or after reading none.
        /// </summary>
        private LocalController Controller(String? Configuration = null)

            => TestControllers.New(directory,
                                   Configuration is not null
                                       ? JObject.Parse(Configuration)
                                       : null);

        #endregion


        #region AQuorumOnItsOwnHoldsTheServersInEffectToIt()

        /// <summary>
        /// "minServers" without a list is about the servers the controller has.
        /// </summary>
        /// <remarks>
        /// It used to be put into a group of one: the section rebuilt the group
        /// from the single-server client whenever it said anything at all, so
        /// "minServers": 3 on its own made one server that had to be three.
        /// </remarks>
        [Test]
        public async Task AQuorumOnItsOwnHoldsTheServersInEffectToIt()
        {

            await using var controller = Controller("""{ "nts": { "minServers": 3 } }""");

            Assert.Multiple(() => {
                Assert.That(controller.TimeSources.Sources.Count(),  Is.EqualTo(4),  "the servers were not mentioned, so they are the default four");
                Assert.That(controller.TimeSources.MinServers,       Is.EqualTo(3),  "the quorum was read and not put into effect");
            });

        }

        #endregion

        #region ADeviationOnItsOwnAppliesToTheServersInEffect()

        [Test]
        public async Task ADeviationOnItsOwnAppliesToTheServersInEffect()
        {

            await using var controller = Controller("""{ "nts": { "maxDeviationSeconds": 0.5 } }""");

            Assert.Multiple(() => {
                Assert.That(controller.TimeSources.Sources.Count(),  Is.EqualTo(4));
                Assert.That(controller.TimeSources.MinServers,       Is.EqualTo(2));
                Assert.That(controller.TimeSources.MaxDeviation,     Is.EqualTo(TimeSpan.FromSeconds(0.5)),  "the deviation was read and not put into effect");
            });

        }

        #endregion

        #region AQuorumTheServersInEffectCannotReachStopsTheStart()

        /// <summary>
        /// Five of four is refused while the file is read, as five of a list of
        /// four always was.
        /// </summary>
        [Test]
        public void AQuorumTheServersInEffectCannotReachStopsTheStart()
        {

            var problem = Assert.Throws<InvalidOperationException>(() => Controller("""{ "nts": { "minServers": 5 } }"""));

            Assert.That(problem?.Message,  Does.Contain("minServers").And.Contain(ConfigurationPath));

        }

        #endregion

        #region AQuorumOnItsOwnIsRefusedBeforeItIsWrittenDown()

        /// <summary>
        /// And the same from the page: refused, and the file left as it was, so
        /// that the next start does not stop over what was refused.
        /// </summary>
        [Test]
        public async Task AQuorumOnItsOwnIsRefusedBeforeItIsWrittenDown()
        {

            await using var controller = Controller();

            Assert.Multiple(() => {

                Assert.That(controller.TryUpdateNTSConfiguration(JObject.Parse("""{ "minServers": 5 }"""), out var error),  Is.False);
                Assert.That(error,                                                                                      Does.Contain("minServers"));

                Assert.That(!File.Exists(ConfigurationPath) || !File.ReadAllText(ConfigurationPath).Contains("minServers"),
                            Is.True,
                            "a refused quorum was written down all the same");

                Assert.That(controller.TimeSources.MinServers,  Is.EqualTo(2));

            });

            Assert.That(controller.TryUpdateNTSConfiguration(JObject.Parse("""{ "minServers": 3 }"""), out var unexpected),  Is.True,  unexpected);
            Assert.That(controller.TimeSources.MinServers,                                                                  Is.EqualTo(3));

        }

        #endregion

        #region AListAfterALoneHostnameIsHeldToTheQuorumAgain()

        /// <summary>
        /// The quorum a group of one has to settle for is not carried over to
        /// the four that come after it.
        /// </summary>
        /// <remarks>
        /// Read from the file at the next start, the same section holds the four
        /// to two. A running controller that held them to one would be a
        /// different controller from the one that file describes.
        /// </remarks>
        [Test]
        public async Task AListAfterALoneHostnameIsHeldToTheQuorumAgain()
        {

            await using var controller = Controller();

            Assert.That(controller.TryUpdateNTSConfiguration(JObject.Parse("""{ "hostname": "ptbtime1.ptb.de" }"""), out var error),  Is.True,  error);
            Assert.That(controller.TimeSources.MinServers,                                                                          Is.EqualTo(1),  "one server cannot be held to two");

            Assert.That(controller.TryUpdateNTSConfiguration(JObject.Parse("""
                            {
                                "servers": [ "ptbtime1.ptb.de", "ptbtime2.ptb.de",
                                             "ptbtime3.ptb.de", "ptbtime4.ptb.de" ]
                            }
                            """), out error),  Is.True,  error);

            Assert.That(controller.TimeSources.MinServers,  Is.EqualTo(2),  "the group of one's quorum was carried over to four");

        }

        #endregion

        #region ASaveOfPartOfTheSectionLeavesTheRestInEffect()

        /// <summary>
        /// The switch on the page sends "enabled" and nothing else, and the
        /// rest of what the file said stays in effect.
        /// </summary>
        /// <remarks>
        /// It used to be replaced by what was sent: how often to check and who
        /// stands behind the time went back to their defaults, and came back
        /// only when the next start read the file again.
        /// </remarks>
        [Test]
        public async Task ASaveOfPartOfTheSectionLeavesTheRestInEffect()
        {

            await using var controller = Controller("""{ "nts": { "checkEverySeconds": 600, "legalTimeAuthority": "PTB" } }""");

            Assert.That(controller.TryUpdateNTSConfiguration(JObject.Parse("""{ "enabled": true }"""), out var error),  Is.True,  error);

            Assert.Multiple(() => {
                Assert.That(controller.TimeCheckEvery,      Is.EqualTo(TimeSpan.FromSeconds(600)),  "the interval went back to its default");
                Assert.That(controller.LegalTimeAuthority,  Is.EqualTo("PTB"),                      "the authority was forgotten");
            });

        }

        #endregion

        #region AListShorterThanTheQuorumIsRefusedAndNotWritten()

        /// <summary>
        /// A server deleted or switched off below the quorum the file holds is
        /// refused, and the file is left as it was.
        /// </summary>
        /// <remarks>
        /// Each half was fine on its own - the quorum in the file, the list that
        /// was sent - and merged they made a section the next start refuses. A
        /// save that is accepted and then stops the controller is the one thing
        /// worse than a save that is refused.
        /// </remarks>
        [Test]
        public async Task AListShorterThanTheQuorumIsRefusedAndNotWritten()
        {

            await using var controller = Controller("""
                                             { "nts": { "servers": [ "a.example", "b.example", "c.example" ], "minServers": 3 } }
                                             """);

            var before = File.ReadAllText(ConfigurationPath);

            Assert.Multiple(() => {

                Assert.That(controller.TryUpdateNTSConfiguration(JObject.Parse("""{ "servers": [ "a.example", "b.example" ] }"""), out var deleted),
                            Is.False,
                            "a server was deleted below the quorum");

                Assert.That(deleted,  Does.Contain("minServers"));

                Assert.That(controller.TryUpdateNTSConfiguration(JObject.Parse("""
                                { "servers": [ "a.example", "b.example", { "hostname": "c.example", "enabled": false } ] }
                                """), out _),
                            Is.False,
                            "a server was switched off below the quorum");

                Assert.That(File.ReadAllText(ConfigurationPath),       Is.EqualTo(before),  "a refused save was written down");
                Assert.That(controller.TimeSources.Sources.Count(),    Is.EqualTo(3));

            });

        }

        #endregion

        #region EveryServerIsListedWithItsPorts()

        /// <summary>
        /// The list the page edits and sends back whole has every server in it,
        /// the switched-off ones included, with the ports each is asked on.
        /// </summary>
        /// <remarks>
        /// It used to list the bands, which have only the servers switched on:
        /// a page sending back what it was shown would have deleted every
        /// server that was switched off.
        /// </remarks>
        [Test]
        public async Task EveryServerIsListedWithItsPorts()
        {

            await using var controller = Controller("""
                                             { "nts": { "servers": [ "a.example",
                                                                     { "hostname": "b.example", "ntsKEPort": 4461, "enabled": false } ] } }
                                             """);

            var listed = controller.NTSConfigurationJSON()["timeSources"] as JArray;

            Assert.Multiple(() => {
                Assert.That(listed,                                  Has.Count.EqualTo(2),  "the switched-off server is missing");
                Assert.That(listed?[1]?.Value<String>("hostname"),   Is.EqualTo("b.example."));
                Assert.That(listed?[1]?.Value<Boolean>("enabled"),   Is.False);
                Assert.That(listed?[1]?.Value<Int32>("ntsKEPort"),   Is.EqualTo(4461));
                Assert.That(listed?[0]?.Value<Int32>("ntpPort"),     Is.EqualTo(123));

                // There and empty until a key exchange has shown a chain.
                Assert.That(listed?[0]?["rootCA"]?.Type,             Is.EqualTo(JTokenType.Null));
            });

        }

        #endregion

        #region TheQuorumWantedAndTheQuorumHeldAreBothShown()

        /// <summary>
        /// A lone hostname holds the group to one, and the page shows both that
        /// and the two that is wanted, which the next list is held to.
        /// </summary>
        [Test]
        public async Task TheQuorumWantedAndTheQuorumHeldAreBothShown()
        {

            await using var controller = Controller("""{ "nts": { "hostname": "a.example" } }""");

            var shown = controller.NTSConfigurationJSON();

            Assert.Multiple(() => {
                Assert.That(shown["settings"]?.Value<Int32>("minServers"),  Is.EqualTo(2),  "the quorum wanted");
                Assert.That(shown["group"]?.   Value<Int32>("minServers"),  Is.EqualTo(1),  "the quorum one server can be held to");
            });

        }

        #endregion

        #region ANewIntervalReachesTheRunningClockCheckAtOnce()

        /// <summary>
        /// The clock is checked on a timer set at the start, and a new interval
        /// or the switch reaches that timer when it is saved - not at the next
        /// start, which is what the page's "in effect" would otherwise be
        /// saying about it.
        /// </summary>
        /// <remarks>
        /// Seen through the line the timer writes whenever it is set: a
        /// started controller writes it once at its start and again for each
        /// change that reaches it.
        /// </remarks>
        [Test]
        public async Task ANewIntervalReachesTheRunningClockCheckAtOnce()
        {

            await using var controller = Controller();

            await controller.Start();

            Assert.That(controller.TryUpdateNTSConfiguration(JObject.Parse("""{ "checkEverySeconds": 600 }"""), out var error),  Is.True,  error);
            Assert.That(controller.TryUpdateNTSConfiguration(JObject.Parse("""{ "enabled": false }"""),         out error),      Is.True,  error);

            var lines = controller.Log.Recent(200).Select(entry => entry.Message).ToArray();

            Assert.Multiple(() => {

                Assert.That(lines.Count(line => line.Contains("will be checked against") && line.Contains("every 15 minute(s)")),  Is.EqualTo(1),
                            "the start set the timer once");

                Assert.That(lines.Count(line => line.Contains("will be checked against") && line.Contains("every 10 minute(s)")),  Is.EqualTo(1),
                            "the new interval did not reach the timer");

                Assert.That(lines.Count(line => line.Contains("is not being checked: NTS is switched off")),                        Is.EqualTo(1),
                            "switching NTS off did not reach the timer");

            });

        }

        #endregion

        #region AChangeToOneServerIsWrittenDown()

        /// <summary>
        /// A server given another priority or a port of its own is a change to
        /// the group, and the log book hears of it.
        /// </summary>
        /// <remarks>
        /// The log compared the names of the servers switched on, in the order
        /// they are asked. A priority that put a server into a band of its own
        /// at the end, and a port of its own, left those just as they were -
        /// and so the group changed without a line saying so.
        /// </remarks>
        [Test]
        public async Task AChangeToOneServerIsWrittenDown()
        {

            await using var controller = Controller("""{ "nts": { "servers": [ "a.example", "b.example" ] } }""");

            var before = controller.Log.LastId;

            Assert.That(controller.TryUpdateNTSConfiguration(JObject.Parse("""
                            { "servers": [ "a.example", { "hostname": "b.example", "priority": 5, "ntsKEPort": 4461 } ] }
                            """), out var error),
                        Is.True,
                        error);

            var said = controller.Log.Recent(50, before, null).Select(entry => entry.Message).ToArray();

            Assert.That(said,  Has.Some.Contains("time servers = a.example, b.example (priority 5, NTS-KE port 4461)"),
                        String.Join(" | ", said));

        }

        #endregion

        #region TheOverviewNamesTheGroupAndNotTheTestClient()

        /// <summary>
        /// The Configuration page's time card names the servers the clock is
        /// checked against - all of them, the one switched off as well - and
        /// not the host of the single client.
        /// </summary>
        /// <remarks>
        /// It said "NTS: ptbtime1.ptb.de." and nothing else: one server, the
        /// single client's, with its root dot, where the group asked four.
        /// </remarks>
        [Test]
        public async Task TheOverviewNamesTheGroupAndNotTheTestClient()
        {

            await using var controller = Controller("""
                                             { "nts": { "servers": [ "a.example",
                                                                     { "hostname": "b.example", "priority": 5 },
                                                                     { "hostname": "c.example", "enabled": false } ] } }
                                             """);

            var time = controller.ConfigurationJSON()["time"] as JObject;

            Assert.Multiple(() => {

                Assert.That(time?.Value<String>("timeServers"),  Is.EqualTo("a.example, b.example (priority 5), c.example (switched off)"));
                Assert.That(time?.Value<Boolean>("ntsEnabled"),  Is.True);
                Assert.That(time?.Value<Int32>("minServers"),    Is.EqualTo(2));
                Assert.That(time?.Value<String>("checkedEvery"), Is.EqualTo("00:15:00"));
                Assert.That(time?.ContainsKey("nts"),            Is.False,  "the single client's host is named again");

                // There and empty while nothing has been synchronised, so that
                // the card says "-" rather than leaving the line out.
                Assert.That(time?["lastSync"]?.      Type,       Is.EqualTo(JTokenType.Null));
                Assert.That(time?["lastSyncResult"]?.Type,       Is.EqualTo(JTokenType.Null));

            });

        }

        #endregion

        #region TheOverviewSaysHowTheLastSynchronisationWent()

        /// <summary>
        /// When the last synchronisation happened, and how it went - because
        /// the moment alone reads as a success, and one that found no server
        /// has a moment just as much as one that set the record straight.
        /// </summary>
        /// <remarks>
        /// Against one server that cannot answer and costs nothing to ask: an
        /// address, which needs no lookup, on a port nobody listens on, so the
        /// key exchange is turned away at once and nothing leaves this machine.
        /// Name resolution is switched off as well, for the lookup the
        /// measurement makes on the side.
        /// </remarks>
        [Test]
        public async Task TheOverviewSaysHowTheLastSynchronisationWent()
        {

            await using var controller = TestControllers.New(
                                             directory,
                                             new JObject(
                                                 new JProperty("dns", new JObject(
                                                     new JProperty("enabled",  false)
                                                 )),
                                                 new JProperty("nts", new JObject(
                                                     new JProperty("enabled",  true),
                                                     new JProperty("servers",  new JArray(
                                                         new JObject(
                                                             new JProperty("hostname",   "127.0.0.1"),
                                                             new JProperty("ntsKEPort",  TestControllers.FreePort())
                                                         )
                                                     ))
                                                 ))
                                             )
                                         );

            var result = await controller.SyncTimeAsync();
            var time   = controller.ConfigurationJSON()["time"] as JObject;

            Assert.Multiple(() => {
                Assert.That(result.Value<Boolean>("ok"),              Is.False);
                Assert.That(time?.Value<String>("lastSync"),          Is.EqualTo(result.Value<String>("at")));
                Assert.That(time?.Value<String>("lastSyncResult"),    Is.EqualTo($"failed: {result.Value<String>("error")}"));
            });

        }

        #endregion

        #region TheClockNamesTheGroupItIsCheckedAgainst()

        /// <summary>
        /// Against whom the clock is checked, as its JSON says it: the group,
        /// its servers switched on in the order they are asked, and how many of
        /// them have to answer.
        /// </summary>
        /// <remarks>
        /// It named the servers with their root dot and neither the group nor
        /// the quorum - which is half of what "checked against" means when
        /// there is more than one.
        /// </remarks>
        [Test]
        public async Task TheClockNamesTheGroupItIsCheckedAgainst()
        {

            await using var controller = Controller("""
                                             { "nts": { "servers": [ { "hostname": "b.example", "priority": 5 },
                                                                     "a.example",
                                                                     { "hostname": "c.example", "enabled": false } ] } }
                                             """);

            var nts = (JObject) controller.ClockJSON()["nts"]!;

            Assert.Multiple(() => {

                Assert.That(nts.Value<String>("group"),        Is.EqualTo("legal"));

                Assert.That(nts["servers"]!.Values<String>(),  Is.EqualTo(new[] { "a.example", "b.example" }),
                            "switched on, in the order their bands are asked, and without the root's dot");

                Assert.That(nts.Value<Int32>("minServers"),    Is.EqualTo(2));

                Assert.That(nts["server"]?.Type,               Is.EqualTo(JTokenType.Null),
                            "one of two named as though it were the one");

            });

        }

        #endregion

        #region AClockCheckedAgainstOneServerNamesIt()

        /// <summary>
        /// A group of one is checked against that one, and says so - as it is
        /// read, without the root's dot.
        /// </summary>
        [Test]
        public async Task AClockCheckedAgainstOneServerNamesIt()
        {

            await using var controller = Controller("""{ "nts": { "hostname": "a.example" } }""");

            Assert.That(controller.ClockJSON()["nts"]?.Value<String>("server"),  Is.EqualTo("a.example"));

        }

        #endregion

        #region AClockThatIsNotCheckedNamesNobody()

        /// <summary>
        /// Switched off, the clock is checked against nobody, and says so -
        /// rather than naming servers that are not asked.
        /// </summary>
        [Test]
        public async Task AClockThatIsNotCheckedNamesNobody()
        {

            await using var controller = Controller("""{ "nts": { "enabled": false } }""");

            var nts = (JObject) controller.ClockJSON()["nts"]!;

            Assert.Multiple(() => {
                Assert.That(nts.Value<Boolean>("enabled"),  Is.False);
                Assert.That(nts["group"]?.     Type,        Is.EqualTo(JTokenType.Null));
                Assert.That(nts["servers"]?.   Type,        Is.EqualTo(JTokenType.Null));
                Assert.That(nts["minServers"]?.Type,        Is.EqualTo(JTokenType.Null));
                Assert.That(nts["server"]?.    Type,        Is.EqualTo(JTokenType.Null));
            });

        }

        #endregion

        #region TheRootCAIsNamedAndFingerprintedForTheList()

        /// <summary>
        /// What the NTS page's list shows of a server's last key exchange: the
        /// root its chain ended at, by its common name, its whole subject, and
        /// its SHA-256 fingerprint - and nothing before there was an exchange.
        /// </summary>
        [Test]
        public void TheRootCAIsNamedAndFingerprintedForTheList()
        {

            using var authority = TestCA.Create("Test Root");
            using var server    = authority.SignFor("time.example",
                                                    DateTimeOffset.UtcNow.AddDays(-10),
                                                    DateTimeOffset.UtcNow.AddDays(79),
                                                    ClientAuthentication: false);

            var root = authority.Certificate;
            var json = LocalController.RootCAJSON(new NTSKE_TLSInfo(ServerCertificate: server, ValidatedChain: [ server, root ]));

            Assert.Multiple(() => {
                Assert.That(json?.Value<String>("name"),                     Is.EqualTo("Test Root"));
                Assert.That(json?.Value<String>("subject"),                  Is.EqualTo("CN=Test Root"));
                Assert.That(json?.Value<String>("fingerprint"),              Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(root.RawData))));
                Assert.That(LocalController.RootCAJSON(null),                Is.Null);
                Assert.That(LocalController.RootCAJSON(new NTSKE_TLSInfo()), Is.Null, "a session that kept no chain names no root");
            });

        }

        #endregion

        #region ADeviationStaysWhenTheServersChange()

        /// <summary>
        /// What a section does not mention is left as it is - the deviation
        /// too, when the servers are replaced.
        /// </summary>
        [Test]
        public async Task ADeviationStaysWhenTheServersChange()
        {

            await using var controller = Controller("""{ "nts": { "maxDeviationSeconds": 0.5 } }""");

            Assert.That(controller.TryUpdateNTSConfiguration(JObject.Parse("""{ "servers": [ "a.example", "b.example" ] }"""), out var error),  Is.True,  error);

            Assert.That(controller.TimeSources.MaxDeviation,  Is.EqualTo(TimeSpan.FromSeconds(0.5)));

        }

        #endregion

    }

}
