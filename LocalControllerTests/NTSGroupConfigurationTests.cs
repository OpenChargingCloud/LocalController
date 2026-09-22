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

using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.LocalController.Configuration;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// What the "nts" section may say about a group of time servers.
    /// </summary>
    [TestFixture]
    public class NTSGroupConfigurationTests
    {

        #region Data

        private String directory = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("nts-group");
        }

        [TearDown]
        public void RemoveTheDirectory()
        {
            TestControllers.Remove(directory);
        }

        #endregion


        #region AListOfNamesIsOneBandOfEqualServers()

        /// <summary>
        /// The short form, and the one most configurations want: four
        /// equivalent servers, asked together.
        /// </summary>
        [Test]
        public void AListOfNamesIsOneBandOfEqualServers()
        {

            var section = JObject.Parse("""
                              {
                                  "servers":    [ "ptbtime1.ptb.de", "ptbtime2.ptb.de",
                                                  "ptbtime3.ptb.de", "ptbtime4.ptb.de" ],
                                  "minServers": 2
                              }
                              """);

            Assert.That(NTSConfiguration.TryParse(section, out var read, out var error),  Is.True,  error);

            var group = read!.ToGroup(DomainName.Parse("unused.example"));

            Assert.Multiple(() => {
                Assert.That(read.Servers?.Count(),  Is.EqualTo(4));
                Assert.That(group.MinServers,       Is.EqualTo(2));
                Assert.That(group.Bands(),          Has.Count.EqualTo(1),  "equal priorities are one band, asked together");
                Assert.That(group.Name,             Is.EqualTo("legal"));
            });

        }

        #endregion

        #region PrioritiesBecomeBands()

        [Test]
        public void PrioritiesBecomeBands()
        {

            var section = JObject.Parse("""
                              {
                                  "servers": [
                                      { "hostname": "time.local",      "priority": 0 },
                                      { "hostname": "ptbtime1.ptb.de", "priority": 9 },
                                      { "hostname": "ptbtime2.ptb.de", "priority": 9 }
                                  ]
                              }
                              """);

            Assert.That(NTSConfiguration.TryParse(section, out var read, out var error),  Is.True,  error);

            var bands = read!.ToGroup(DomainName.Parse("unused.example")).Bands();

            Assert.Multiple(() => {
                Assert.That(bands,                            Has.Count.EqualTo(2));
                Assert.That(bands[0][0].Hostname.ToString(),  Is.EqualTo("time.local."),  "the lower priority is asked first");
                Assert.That(bands[1],                         Has.Count.EqualTo(2));
            });

        }

        #endregion

        #region ASingleHostnameStillWorks()

        /// <summary>
        /// Every configuration file written before there were groups names one
        /// host and no list. It becomes a group of one rather than an error.
        /// </summary>
        [Test]
        public void ASingleHostnameStillWorks()
        {

            var section = JObject.Parse("""{ "hostname": "ptbtime1.ptb.de" }""");

            Assert.That(NTSConfiguration.TryParse(section, out var read, out var error),  Is.True,  error);

            var group = read!.ToGroup(DomainName.Parse("unused.example"));

            Assert.Multiple(() => {
                Assert.That(read.Servers,                             Is.Null,  "a lone hostname is not a list");
                Assert.That(group.Bands(),                            Has.Count.EqualTo(1));
                Assert.That(group.Bands()[0][0].Hostname.ToString(),  Is.EqualTo("ptbtime1.ptb.de."));
                Assert.That(group.MinServers,                         Is.EqualTo(1),  "one server cannot be held to a quorum of two");
            });

        }

        #endregion

        #region AnEmptySectionFallsBackToTheGivenServer()

        [Test]
        public void AnEmptySectionFallsBackToTheGivenServer()
        {

            Assert.That(NTSConfiguration.TryParse([], out var read, out var error),  Is.True,  error);

            var group = read!.ToGroup(DomainName.Parse("ptbtime1.ptb.de"));

            Assert.That(group.Bands()[0][0].Hostname.ToString(),  Is.EqualTo("ptbtime1.ptb.de."));

        }

        #endregion

        #region AQuorumNobodyCanReachIsRefused()

        /// <summary>
        /// Three servers and a quorum of four is a controller that can never have
        /// a time, and it is worth saying so while somebody is reading the file
        /// rather than at the first check.
        /// </summary>
        [Test]
        public void AQuorumNobodyCanReachIsRefused()
        {

            var section = JObject.Parse("""
                              {
                                  "servers":    [ "a.example", "b.example", "c.example" ],
                                  "minServers": 4
                              }
                              """);

            Assert.Multiple(() => {
                Assert.That(NTSConfiguration.TryParse(section, out _, out var error),  Is.False);
                Assert.That(error,                                                     Does.Contain("minServers"));
            });

        }

        #endregion

        #region ASwitchedOffServerDoesNotCountTowardsTheQuorum()

        /// <summary>
        /// And the count that matters is of the servers actually asked.
        /// </summary>
        [Test]
        public void ASwitchedOffServerDoesNotCountTowardsTheQuorum()
        {

            var section = JObject.Parse("""
                              {
                                  "servers": [
                                      "a.example",
                                      { "hostname": "b.example", "enabled": false }
                                  ],
                                  "minServers": 2
                              }
                              """);

            Assert.That(NTSConfiguration.TryParse(section, out _, out _),  Is.False,  "a switched-off server was counted as available");

        }

        #endregion

        #region AnEmptyListIsRefused()

        /// <summary>
        /// "Ask nobody" is what switching NTS off is for, and an empty list is
        /// almost certainly somebody halfway through editing.
        /// </summary>
        [Test]
        public void AnEmptyListIsRefused()
        {

            Assert.That(NTSConfiguration.TryParse(JObject.Parse("""{ "servers": [] }"""), out _, out _),  Is.False);

        }

        #endregion

        #region WhatIsWrittenComesBack()

        [Test]
        public void WhatIsWrittenComesBack()
        {

            var written = new NTSConfiguration(
                              Servers:       [
                                                 new NTSServerConfiguration(DomainName.Parse("time.local")),
                                                 new NTSServerConfiguration(DomainName.Parse("ptbtime1.ptb.de"), Priority: 9)
                                             ],
                              MinServers:    1,
                              MaxDeviation:  TimeSpan.FromSeconds(30)
                          ).ToJSON();

            Assert.That(NTSConfiguration.TryParse(written, out var read, out var error),  Is.True,  error);

            Assert.Multiple(() => {

                Assert.That(read!.Servers?.Count(),          Is.EqualTo(2));
                Assert.That(read.Servers?.First().Priority,  Is.EqualTo(0));
                Assert.That(read.Servers?.Last().Priority,   Is.EqualTo(9));
                Assert.That(read.MinServers,                 Is.EqualTo(1));
                Assert.That(read.MaxDeviation,               Is.EqualTo(TimeSpan.FromSeconds(30)));

                // A plain server is written as a bare name, so a file this
                // station wrote reads the way somebody would have written it.
                Assert.That(written["servers"]?[0]?.Type,    Is.EqualTo(JTokenType.String));
                Assert.That(written["servers"]?[1]?.Type,    Is.EqualTo(JTokenType.Object));

            });

        }

        #endregion

        #region AGroupOfOneIsWhatAControllerStartsWith()

        /// <summary>
        /// A station handed nothing but a time client has a group of one, so
        /// that everything reading the group reads something rather than
        /// checking for null first.
        /// </summary>
        /// <remarks>
        /// This is the case every existing installation is in, and the one a
        /// port to groups is likeliest to break: the controller is built the way
        /// it has always been built, with no "nts" section at all.
        /// </remarks>
        [Test]
        public async Task AGroupOfOneIsWhatAControllerStartsWith()
        {

            await using var controller = TestControllers.New(directory);

            Assert.Multiple(() => {
                Assert.That(controller.TimeSources.Bands(),                 Has.Count.EqualTo(1));
                Assert.That(controller.TimeSources.Bands()[0],              Has.Count.EqualTo(1));
                Assert.That(controller.TimeSources.Bands()[0][0].Hostname,  Is.EqualTo(controller.NTSClient.Hostname));
                Assert.That(controller.TimeSources.MinServers,              Is.EqualTo(1));
            });

        }

        #endregion

        #region AConfiguredGroupReachesTheControllerAndItsDisplay()

        /// <summary>
        /// The whole way through: four servers in the file, four in the group,
        /// four on the display - and no server named on a screen, because
        /// naming one of four would be the nicer-looking lie.
        /// </summary>
        [Test]
        public async Task AConfiguredGroupReachesTheControllerAndItsDisplay()
        {

            await using var controller = TestControllers.New(
                                          directory,
                                          new JObject(
                                              new JProperty("nts", new JObject(
                                                  new JProperty("servers", new JArray("ptbtime1.ptb.de", "ptbtime2.ptb.de",
                                                                                      "ptbtime3.ptb.de", "ptbtime4.ptb.de")),
                                                  new JProperty("minServers", 2)
                                              ))
                                          )
                                      );

            var clock = controller.ClockJSON();

            Assert.Multiple(() => {

                Assert.That(controller.TimeSources.Bands(),                 Has.Count.EqualTo(1));
                Assert.That(controller.TimeSources.Bands()[0],              Has.Count.EqualTo(4));
                Assert.That(controller.TimeSources.MinServers,              Is.EqualTo(2));

                Assert.That(clock["nts"]?["servers"]?.Values<String>(),  Has.Exactly(4).Items);
                Assert.That(clock["nts"]?["server"]?.Type,               Is.EqualTo(JTokenType.Null),
                            "a screen would have printed one of four as though it were the one");

                // Nothing has been checked yet, and the display is told that
                // in numbers rather than being left to read it out of a name.
                Assert.That(clock["nts"]?["asked"]?.Type,                Is.EqualTo(JTokenType.Null));
                Assert.That(clock["nts"]?["answered"]?.Type,             Is.EqualTo(JTokenType.Null));

            });

        }

        #endregion

    }

}
