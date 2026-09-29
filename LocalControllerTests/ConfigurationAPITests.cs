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
    /// What this local controller says it is, and what happens when somebody
    /// tells it to be something else.
    /// </summary>
    public class ConfigurationAPITests : ALocalControllerTests
    {

        #region TheStatusSaysWhoTheControllerIsToOCPP()

        /// <summary>
        /// What a local controller's status says beyond every node's - see the
        /// conformance suite of WWCP_Node_TestKit for that: the identity it has
        /// towards its CSMS, right after the version.
        /// </summary>
        [Test]
        public async Task TheStatusSaysWhoTheControllerIsToOCPP()
        {

            using var http = await SignedIn();

            var status = await GetJSON(http, "/api/v1/status");

            Assert.Multiple(() => {
                Assert.That(status.Value<String>("service"),  Is.EqualTo("LocalController"));
                Assert.That(status.Value<String>("ocppId"),   Is.EqualTo(Controller.Node.Id.ToString()));
                Assert.That(status.Properties().Select(property => property.Name).Take(3),
                            Is.EqualTo(new[] { "service", "version", "ocppId" }));
            });

        }

        #endregion

        #region TheConfigurationNamesEverySection()

        /// <summary>
        /// The Configuration page renders whatever the controller sends rather
        /// than a list of its own, so a section going missing is not a broken
        /// page - it is a page that quietly stops mentioning something.
        /// </summary>
        [Test]
        public async Task TheConfigurationNamesEverySection()
        {

            using var http = await SignedIn();

            var configuration = await GetJSON(http, "/api/v1/configuration");

            // The node's own sections - http, web, log, time and assemblies -
            // are asked of every kind by the conformance suite of
            // WWCP_Node_TestKit.
            Assert.Multiple(() => {
                Assert.That(configuration["controller"], Is.Not.Null);
                Assert.That(configuration["ocpp"],       Is.Not.Null);
            });

        }

        #endregion

        #region TheOCPPSectionDescribesTheNode()

        [Test]
        public async Task TheOCPPSectionDescribesTheNode()
        {

            using var http = await SignedIn();

            var ocpp = (await GetJSON(http, "/api/v1/configuration"))["ocpp"];

            Assert.Multiple(() => {
                Assert.That(ocpp?.Value<String>("version"),  Is.EqualTo("2.1"));
                Assert.That(ocpp?.Value<String>("role"),     Is.EqualTo("Local Controller"));
                Assert.That(ocpp?.Value<String>("id"),       Is.EqualTo(Controller.Node.Id.ToString()));
                Assert.That(ocpp?.Value<String>("vendor"),   Is.EqualTo(Controller.Node.VendorName));
                Assert.That(ocpp?.Value<String>("model"),    Is.EqualTo(Controller.Node.Model));
            });

        }

        #endregion


    }

}
