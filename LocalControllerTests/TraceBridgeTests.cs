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

using NUnit.Framework;

using cloud.charging.open.LocalController.Logging;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// What the libraries below write through DebugX, as the event log gets it:
    /// above all, which tags a line is given.
    /// </summary>
    /// <remarks>
    /// The tags are what the Logs page filters by, so a wrong one is not
    /// cosmetic: a line about the accounts that says "nts" is shown to
    /// somebody looking for the time servers, and hidden from nobody.
    /// </remarks>
    public class TraceBridgeTests
    {

        #region (helper) TagsOf(Line)

        /// <summary>
        /// The tags a line is given: written into a bridge of its own, and read
        /// back from that bridge's log.
        /// </summary>
        private static IReadOnlyList<String> TagsOf(String Line)
        {

            var log = new EventLog();

            using (var bridge = TraceBridge.Attach(log))
                bridge.WriteLine(Line);

            return log.Recent(100).Single(entry => entry.Message == Line).Tags;

        }

        #endregion


        #region AWordThatOnlyEndsInAProtocolNameIsNotAboutIt()

        /// <summary>
        /// "nts" inside "accounts" is not the time servers.
        /// </summary>
        /// <remarks>
        /// Found anywhere in a line, the needle tagged every line that said
        /// "accounts", "clients" or "events" as one about the time servers -
        /// the first start's own complaint about the account store among them,
        /// which then stood on the Logs page as "[warning trace nts]".
        /// </remarks>
        [Test]
        public void AWordThatOnlyEndsInAProtocolNameIsNotAboutIt()
        {

            var firstStart = @"Could not find database file 'C:\LocalController\accounts\UsersAPI\users.db'!";

            Assert.Multiple(() => {
                Assert.That(TagsOf(firstStart),                                  Does.Not.Contain("nts"));
                Assert.That(TagsOf("2 clients, 12 events and 3 agents waiting"),  Does.Not.Contain("nts"));
                Assert.That(TagsOf(firstStart),                                  Is.EqualTo(new[] { TraceBridge.TraceTag }));
            });

        }

        #endregion

        #region AProtocolNameCountsWhereverAWordOfItsOwnBegins()

        /// <summary>
        /// The names still count where a word of their own begins, whatever
        /// follows them: at the start, after a sign, and at a capital.
        /// </summary>
        /// <remarks>
        /// What keeps the fix from overshooting. Whole words would have been
        /// the obvious rule and the wrong one: "OCPPv2.1", "https" and
        /// "certificates" are the words these names turn up in, and "mDNS" and
        /// "OCPPWebSocketServer" put theirs in the middle.
        /// </remarks>
        [Test]
        public void AProtocolNameCountsWhereverAWordOfItsOwnBegins()
        {

            Assert.Multiple(() => {
                Assert.That(TagsOf("NTS-KE handshake with ptbtime1.ptb.de failed"),  Does.Contain("nts"));
                Assert.That(TagsOf("Sending an NTP request"),                        Does.Contain("nts"));
                Assert.That(TagsOf("OCPPv2.1 BootNotification accepted"),            Does.Contain("ocpp"));
                Assert.That(TagsOf("OCPPWebSocketServer: a new connection"),         Does.Contain("websocket"));
                Assert.That(TagsOf("mDNS query for _ocpp._tcp.local"),               Does.Contain("dns"));
                Assert.That(TagsOf("GET https://example.org/ answered 200"),         Does.Contain("http"));
                Assert.That(TagsOf("The ServerCertificate has expired"),             Does.Contain("tls"));
                Assert.That(TagsOf("Forwarding it to the CSMS"),                     Does.Contain("csms").And.Contain("routing"));
            });

        }

        #endregion

    }

}
