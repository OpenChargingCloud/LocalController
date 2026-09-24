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

using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Norn.Monitoring;
using org.GraphDefined.Vanaheimr.Norn.TimeSync;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The lines a synchronisation writes into the log with numbers in them.
    /// </summary>
    /// <remarks>
    /// Under a German culture - which is what this controller's own machine
    /// runs - they read "+702,4 ms from 4 server(s), spread 0,5 ms": a decimal
    /// comma in the middle of an English sentence, in a log whose numbers then
    /// change their punctuation with the machine that wrote them. Every test
    /// here runs under de-DE for that reason.
    /// </remarks>
    [SetCulture("de-DE")]
    public class NTSLogLineTests
    {

        #region (private static) Answer(Name, OffsetMilliseconds)

        /// <summary>
        /// One server that answered, authenticated, with the given offset.
        /// </summary>
        private static NTSMeasurementResult Answer(String  Name,
                                                   Double  OffsetMilliseconds)

            => new (DomainName.Parse(Name),
                    Guid.Empty) {

                   Success  = true,
                   NTP      = new NTPMeasurementResult {
                                  Success                 = true,
                                  NTSAuthenticationValid  = true,
                                  Offset                  = TimeSpan.FromMilliseconds(OffsetMilliseconds)
                              }

               };

        #endregion


        #region TheAnsweredLineReadsTheSameUnderEveryCulture()

        /// <summary>
        /// The line a synchronisation that produced a time ends with: the
        /// verdict is Norn's, and Norn writes it invariantly now.
        /// </summary>
        [Test]
        public void TheAnsweredLineReadsTheSameUnderEveryCulture()
        {

            var verdict = TimeSyncVerdict.From(
                              [ Answer("a.example", 1.5), Answer("b.example", 2.5), Answer("c.example", 3.5) ],
                              MinServers:    2,
                              MaxDeviation:  TimeSpan.FromSeconds(60)
                          );

            Assert.That(LocalController.AnsweredLine("legal", 766, verdict),
                        Is.EqualTo("NTS: group 'legal' answered in 766 ms - +2.5 ms from 3 server(s), spread 2.0 ms."));

        }

        #endregion

        #region TheDeviationWarningKeepsItsPointAndItsDeviation()

        /// <summary>
        /// The warning beside it, likewise - and with the agreed deviation in
        /// full, which may be set as low as a millisecond and was written in
        /// whole seconds: 0.001 s read "0 s".
        /// </summary>
        [Test]
        public void TheDeviationWarningKeepsItsPointAndItsDeviation()
        {

            Assert.Multiple(() => {

                Assert.That(LocalController.DeviationWarning("legal", TimeSpan.FromMilliseconds(2.2), TimeSpan.FromMilliseconds(1)),
                            Is.EqualTo("NTS: the time servers of group 'legal' disagree by 2.2 ms, " +
                                       "which reaches the agreed deviation of 0.001 s."));

                // And a whole number of seconds stays one, without a tail of
                // zeros to read past.
                Assert.That(LocalController.DeviationWarning("legal", TimeSpan.FromMilliseconds(61234.5), TimeSpan.FromSeconds(60)),
                            Is.EqualTo("NTS: the time servers of group 'legal' disagree by 61234.5 ms, " +
                                       "which reaches the agreed deviation of 60 s."));

            });

        }

        #endregion

    }

}
