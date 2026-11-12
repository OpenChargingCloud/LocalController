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

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The local controller's own device model shows how it resolves names and
    /// keeps its time, as a charging station's does: the components of
    /// WWCP_OCPP_Node's DeviceModelBridge, read from the controller at every read.
    /// </summary>
    /// <remarks>
    /// The controller is made and not started; nothing is sent over a network.
    /// </remarks>
    [TestFixture]
    public class DeviceModelTests
    {

        #region Data

        private String            directory   = "";
        private LocalController?  controller;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("devicemodel");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public async Task TakeItAwayAgain()
        {

            if (controller is not null)
                await controller.DisposeAsync();

            controller = null;

            TestControllers.Remove(directory);

        }

        #endregion


        #region (private) Read(Component, Variable)

        private OCPPv2_1.GetVariableResult Read(String Component, String Variable)

            => OCPPv2_1.DeviceModel.GetVariables(
                   [ new OCPPv2_1.GetVariableData(new OCPPv2_1.Component(Name: Component), new OCPPv2_1.Variable(Name: Variable)) ],
                   (name, evse) => controller!.Node.GetComponentConfigs(name, evse)
               ).Single();

        #endregion


        #region TheControllerShowsItsNamesAndItsTime()

        /// <summary>
        /// The components of the name servers and of the time are there, beside
        /// the keys of the station port, and what they say is the controller's.
        /// </summary>
        [Test]
        public void TheControllerShowsItsNamesAndItsTime()
        {

            controller = TestControllers.New(directory, TestControllers.Offline);

            var node        = controller.Node;
            var components  = node.AllComponentConfigs.Select(component => component.Name).Distinct().ToArray();

            var servers     = Read("DNSClientController", "Servers");
            var groups      = Read("NTPController",       "ClientGroups");

            Assert.Multiple(() => {

                Assert.That(components,              Is.SupersetOf(new[] { "DNSClientController", "ClockCtrlr", "NTPController", "EVQICtrlr" }));

                Assert.That(servers.AttributeStatus, Is.EqualTo(OCPPv2_1.GetVariableStatus.Accepted), servers.AttributeStatusInfo?.AdditionalInfo);
                Assert.That(groups. AttributeStatus, Is.EqualTo(OCPPv2_1.GetVariableStatus.Accepted), groups. AttributeStatusInfo?.AdditionalInfo);

                // One DNSServerController for each name server Servers names.
                Assert.That(node.GetComponentConfigs("DNSServerController").Select(component => component.Instance).Order(),
                            Is.EqualTo((servers.AttributeValue ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Order()));

            });

        }

        #endregion

    }

}
