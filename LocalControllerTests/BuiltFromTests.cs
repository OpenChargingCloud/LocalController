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

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The "built from" lines: one per repository, and none of them lost.
    /// </summary>
    public class BuiltFromTests
    {

        #region AssembliesOfOneRepositoryAreOneLine()

        /// <summary>
        /// Many assemblies out of one repository carry one commit, and are the
        /// one line the grouping exists for. Assemblies without a stamp are
        /// not ours and are left out.
        /// </summary>
        [Test]
        public void AssembliesOfOneRepositoryAreOneLine()
        {

            var lines = BuiltFrom.OnePerRepository([
                            new LoadedAssembly("WWCP_OCPPv2.1",     "1.0", "WWCP_OCPP",  "4a8eb9e8b7ee13610a4c81f56af9a152690188d1"),
                            new LoadedAssembly("WWCP_OCPP_Common",  "1.0", "WWCP_OCPP",  "4a8eb9e8b7ee13610a4c81f56af9a152690188d1"),
                            new LoadedAssembly("Hermod",            "1.0", "Hermod",     "8801bcfee26a11b5ac4ac7af9486bf909b25d67c"),
                            new LoadedAssembly("Newtonsoft.Json",   "13.0", null,        null)
                        ]).ToArray();

            Assert.That(lines.Select(line => line.Repository),  Is.EqualTo(new[] { "Hermod", "WWCP_OCPP" }));

        }

        #endregion

        #region TwoRepositoriesOfOneNameAreTwoLines()

        /// <summary>
        /// A repository is named after the directory it was cloned into, so a
        /// command line tool and its library can both be called
        /// "ModbusTLSEnergyMeter". Grouped by the name alone they were one
        /// line, whichever sorted first won, and the other commit was dropped
        /// without a word - in a report that exists to say which commits are
        /// running.
        /// </summary>
        [Test]
        public void TwoRepositoriesOfOneNameAreTwoLines()
        {

            var lines = BuiltFrom.OnePerRepository([
                            new LoadedAssembly("ModbusTLSEnergyMeterCLI",  "1.0", "ModbusTLSEnergyMeter", "88bee69aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                            new LoadedAssembly("ModbusTLSEnergyMeter",     "1.0", "ModbusTLSEnergyMeter", "3556194bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                        ]).ToArray();

            Assert.That(lines.Select(line => line.Commit),
                        Is.EqualTo(new[] { "3556194bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "88bee69aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }));

        }

        #endregion

    }

}
