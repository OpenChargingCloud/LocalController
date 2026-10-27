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

using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.CommandLine;

using cloud.charging.open.LocalController.Configuration;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// What a local controller does with the configuration file it is handed
    /// and the accounts it finds, and what it refuses to do.
    /// </summary>
    /// <remarks>
    /// The configuration is read in the constructor, so those tests only build
    /// a controller. What every node's start does - the first account and its
    /// hash, a second start that makes up nothing, a file that cannot be read,
    /// the clock set before anything asks it - is asked in WWCP_Node_Tests,
    /// once for every kind of node; what is here is the controller's own.
    /// </remarks>
    public class StartupTests
    {

        #region Data

        private String directory = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("startup");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void RemoveTheDirectory()
            => TestControllers.Remove(directory);

        #endregion


        #region AControllerWithNoFilesIsTheDefaultOCPPNode()

        /// <summary>
        /// Nobody wrote down who this controller is in OCPP, so it is the one
        /// the defaults describe.
        /// </summary>
        [Test]
        public async Task AControllerWithNoFilesIsTheDefaultOCPPNode()
        {

            await using var controller = TestControllers.New(directory);

            Assert.Multiple(() => {
                Assert.That(controller.Node.Id.ToString(),   Is.EqualTo(OCPPConfiguration.DefaultNodeId));
                Assert.That(controller.Node.VendorName,      Is.EqualTo(OCPPConfiguration.DefaultVendorName));
            });

        }

        #endregion

        #region AControllerKeepsTheKindsOfTLSInTheNodesStore()

        /// <summary>
        /// The node below keeps the certificates its kind asks for, and a
        /// local controller asks for TLS's: the roots of the servers it
        /// connects to, their certificates, and what it presents itself - and
        /// none of ISO 15118's, which are a vehicle's.
        /// </summary>
        /// <remarks>
        /// Until it had a use for them it asked for none, and nothing was made
        /// beside the configuration file. Now it has one, the store lives
        /// there, in certificates/, which the repository ignores like the
        /// accounts. What a controller presents to its charging stations, and
        /// which of their chains it believes, stay in stores of their own.
        /// </remarks>
        [Test]
        public async Task AControllerKeepsTheKindsOfTLSInTheNodesStore()
        {

            await using var controller = TestControllers.New(directory, TestControllers.Offline);

            await controller.Start();

            Assert.Multiple(() => {

                Assert.That(controller.Certificates.Kinds,
                            Is.EquivalentTo(new[] { CertificateKind.TLSRoot, CertificateKind.TLSServer, CertificateKind.TLSIdentity }),
                            "The node's store keeps other kinds of certificate than a controller has use for.");

                Assert.That(Path.GetFullPath(controller.Certificates.Directory),
                            Is.EqualTo(Path.GetFullPath(Path.Combine(directory, "certificates"))),
                            "The node's store is not beside the configuration file, which is what it is measured from.");

            });

        }

        #endregion

        #region TheSwitchesForHTTPSAndTheProxiesInFrontReachTheNode()

        /// <summary>
        /// --https and --trusted-proxy, handed to the controller as its program
        /// hands them, reach the node below: it serves its web interface over
        /// HTTPS from the store, keeping a TLS server identity for it, believes
        /// the proxy - and the step every program takes before the start lets
        /// such a command line through, where a controller that kept them to
        /// itself would be stopped there with 2.
        /// </summary>
        [Test]
        public async Task TheSwitchesForHTTPSAndTheProxiesInFrontReachTheNode()
        {

            var arguments        = NodeArguments.Parse(["--https", "--trusted-proxy", "192.0.2.1"]);
            var web              = arguments.Web;

            await using var controller = TestControllers.New(directory, TestControllers.Offline, Web: web);

            var error            = new StringWriter();
            var notImported      = controller.ImportCertificates(arguments, out _, TextWriter.Null, error);

            Assert.Multiple(() => {

                Assert.That(arguments.Problem,  Is.Null);
                Assert.That(web,                Is.Not.Null);
                Assert.That(notImported,        Is.Null, error.ToString());

                Assert.That(controller.WebSwitches,                 Is.SameAs(web), "the switches did not reach the node");
                Assert.That(controller.WebSettings.TLS,             Is.Not.Null,    "the node does not serve HTTPS");
                Assert.That(controller.WebSettings.TrustedProxies,  Is.EqualTo(new[] { System.Net.IPNetwork.Parse("192.0.2.1/32") }));
                Assert.That(controller.Certificates.Kinds,          Does.Contain(CertificateKind.TLSServerIdentity),
                            "the node keeps no TLS server identity to serve HTTPS with");

            });

        }

        #endregion

        #region TheFileDecidesWhoThisControllerIsInOCPP()

        /// <summary>
        /// Read once, at the start. What the file says beats what the
        /// constructor was handed, and what it does not mention is left alone.
        /// </summary>
        [Test]
        public async Task TheFileDecidesWhoThisControllerIsInOCPP()
        {

            var configuration = new JObject(
                                    new JProperty("nts",  new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                    new JProperty("ocpp", new JObject(
                                        new JProperty("nodeId",      "lc-in-the-file"),
                                        new JProperty("vendorName",  "Somebody Else")
                                    ))
                                );

            await using var controller = TestControllers.New(directory, configuration);

            Assert.Multiple(() => {
                Assert.That(controller.Node.Id.ToString(),  Is.EqualTo("lc-in-the-file"));
                Assert.That(controller.Node.VendorName,     Is.EqualTo("Somebody Else"));
                // Not mentioned, so the default stands.
                Assert.That(controller.Node.Model,          Is.EqualTo(OCPPConfiguration.DefaultModel));
            });

        }

        #endregion

    }

}
