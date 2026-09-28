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

using cloud.charging.open.protocols.WWCP.Node.Configuration;

using cloud.charging.open.LocalController.Configuration;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The controller's own section of the configuration file, read on its
    /// own and beside the node's.
    /// </summary>
    /// <remarks>
    /// Three rules run through all of the sections, and most of these tests
    /// are one of the three: absent is not an error and comes back as null, so
    /// the caller keeps whatever it had; present but of the wrong kind is an
    /// error and never a silent null; and an explicit JSON null counts as
    /// absent, so a page may send a whole object with the fields it does not
    /// touch left empty. The sections every node has are asked in
    /// WWCP_Node_Tests, once for every kind of node.
    /// </remarks>
    public class ConfigurationSectionTests
    {

        #region OCPP: the defaults, and what the file may say instead

        [Test]
        public void AnOCPPSectionSaysNothingUntilItIsToldSomething()
        {

            Assert.That(OCPPConfiguration.TryParse([], out var ocpp, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(ocpp!.NodeId,          Is.Null);
                Assert.That(ocpp.VendorName,       Is.Null);
                Assert.That(ocpp.Model,            Is.Null);
                // Which is what lets the controller fall back to its own.
                Assert.That(OCPPConfiguration.DefaultNodeId, Is.Not.Empty);
            });

        }

        #endregion

        #region OCPP: an identification with a space in it

        /// <summary>
        /// A networking node identification is what a CSMS addresses this
        /// controller by, and a space in one is a typing mistake rather than a
        /// name.
        /// </summary>
        [Test]
        public void AnIdentificationWithASpaceIsRefused()
        {

            Assert.That(OCPPConfiguration.TryParse(new JObject(new JProperty("nodeId", "lc 001")),
                                                   out _, out var error), Is.False);

            Assert.That(error, Does.Contain("ocpp.nodeId"));

        }

        #endregion

        #region OCPP: a field longer than the protocol allows

        [Test]
        public void AFieldLongerThanOCPPAllowsIsRefused()
        {

            var tooLong = new String('x', OCPPConfiguration.MaxModelLength + 1);

            Assert.That(OCPPConfiguration.TryParse(new JObject(new JProperty("model", tooLong)),
                                                   out _, out var error), Is.False);

            Assert.That(error, Does.Contain("ocpp.model"));

        }

        #endregion

        #region OCPP: what it says about itself

        [Test]
        public void AnOCPPSectionNamesItselfReadably()
        {

            var ocpp = new OCPPConfiguration(NodeId: "lc042", VendorName: "ACME", Model: "Box");

            Assert.Multiple(() => {
                Assert.That(ocpp.ToString(),                  Does.Contain("lc042"));
                Assert.That(ocpp.ToString(),                  Does.Contain("ACME"));
                Assert.That(ocpp.ToJSON().Value<String>("nodeId"), Is.EqualTo("lc042"));
                Assert.That(ocpp.ToJSON()["serialNumber"],    Is.Null,
                            "A field nobody set was written to the file anyway.");
            });

        }

        #endregion


        #region The whole document

        /// <summary>
        /// One document, read twice, as the controller reads it: the node below
        /// takes the sections every node has, the controller its own.
        /// </summary>
        /// <remarks>
        /// Each passes the other's over without a word, and each says which of
        /// its own spoke.
        /// </remarks>
        [Test]
        public void TheControllerAndTheNodeEachReadTheirOwnSections()
        {

            var json = new JObject(
                           new JProperty("dns",  new JObject(new JProperty("enabled", true))),
                           new JProperty("ocpp", new JObject(new JProperty("nodeId",  "lc007")))
                       );

            Assert.That(WWCPConfiguration.      TryParse(json, out var node,       out var nodeError), Is.True, nodeError);
            Assert.That(ControllerConfiguration.TryParse(json, out var controller, out var error),     Is.True, error);

            Assert.Multiple(() => {
                Assert.That(node!.DNS,                 Is.Not.Null);
                Assert.That(node.NTS,                  Is.Null);
                Assert.That(controller!.OCPP,          Is.Not.Null);
                Assert.That(controller.IsEmpty,        Is.False);
                Assert.That(node.ToString(),           Does.Contain("DNS"));
                Assert.That(controller.ToString(),     Does.Contain("lc007"));
            });

        }

        #endregion

        #region An empty document says so

        [Test]
        public void AnEmptyDocumentConfiguresNothingOfTheController()
        {

            Assert.That(ControllerConfiguration.TryParse([], out var configuration, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(configuration!.IsEmpty,    Is.True);
                Assert.That(configuration.ToString(),  Is.EqualTo("nothing configured"));
            });

        }

        #endregion

    }

}
