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

using cloud.charging.open.LocalController.Configuration;
using cloud.charging.open.protocols.OCPP.Node.Serving;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The section "webSocketServer" as a part of a local controller's configuration
    /// document. The section itself is WWCP_OCPP_Node's, and tested there (its
    /// OCPPServerConfigurationTests); what is tested here is that the document
    /// of a local controller carries it like every other of its sections.
    /// </summary>
    [TestFixture]
    public class ControllerConfigurationOCPPServerTests
    {

        #region ASectionOfTheWrongKindIsRefusedByTheDocument()

        [Test]
        public void ASectionOfTheWrongKindIsRefusedByTheDocument()
        {

            Assert.Multiple(() => {

                Assert.That(ControllerConfiguration.TryParse(
                                new JObject(new JProperty("webSocketServer", "on")),
                                out _, out var error
                            ), Is.False);

                Assert.That(error, Does.Contain("webSocketServer"));

            });

        }

        #endregion

        #region TheDocumentCarriesTheSectionLikeEveryOther()

        [Test]
        public void TheDocumentCarriesTheSectionLikeEveryOther()
        {

            var document = new JObject(
                               new JProperty("webSocketServer", new JObject(
                                   new JProperty("enabled", true),
                                   new JProperty("port",    9000)
                               ))
                           );

            Assert.That(ControllerConfiguration.TryParse(document, out var configuration, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(configuration!.OCPPServer?.Enabled,            Is.True);
                Assert.That(configuration.OCPPServer?.TCPPort?.ToUInt16(),  Is.EqualTo(9000));
                Assert.That(configuration.IsEmpty,                          Is.False);
                Assert.That(configuration.ToJSON()["webSocketServer"]?.Value<Int32>("port"), Is.EqualTo(9000));
            });

        }

        #endregion

    }

}
