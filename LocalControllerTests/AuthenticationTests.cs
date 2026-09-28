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

using org.GraphDefined.Vanaheimr.Hermod.HTTP;


#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Who may ask this local controller anything, and what happens to
    /// everybody else.
    /// </summary>
    public class AuthenticationTests : ALocalControllerTests
    {

        #region NoGroupOfTheVehiclesRoles()

        /// <summary>
        /// The groups are the controller's roles and not the vehicle's.
        /// </summary>
        /// <remarks>
        /// The node below makes a group for every role its kind hands it, and
        /// a node handed none knows the vehicle's. A controller that did not
        /// hand its own over would have a "driver" and a "service" group
        /// nobody here has a use for - and no "cpo", which the test above
        /// catches from the other side.
        /// </remarks>
        [Test]
        public void NoGroupOfTheVehiclesRoles()
        {

            Assert.Multiple(() => {

                foreach (var vehicles in new[] { "driver", "service" })
                    Assert.That(Controller.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(vehicles), out _), Is.False,
                                $"A local controller has the vehicle's '{vehicles}' group.");

            });

        }

        #endregion

        #region TheSessionSaysWhatItMayDo()

        /// <summary>
        /// A first start signs in as the system administrator, because there is
        /// nobody else yet to hand the rest to. What the browser is told is a
        /// copy of what the controller enforces and not the enforcement itself;
        /// this is the copy - every operation on every resource, spelt out.
        /// </summary>
        [Test]
        public async Task TheSessionSaysWhatItMayDo()
        {

            using var http = await SignedIn();

            var me = await GetJSON(http, "/api/v1/auth/me");

            var roles        = me["roles"]?.      Values<String>().ToArray() ?? [];
            var permissions  = me["permissions"]?.Values<String>().ToArray() ?? [];

            Assert.Multiple(() => {
                Assert.That(roles,       Is.EquivalentTo(new[] { "systemadmin" }));
                Assert.That(permissions, Is.EquivalentTo(from resource  in new[] { "configuration", "dns", "nts", "certificates", "csms", "stations" }
                                                         from operation in new[] { "read", "edit", "run" }
                                                         select $"{resource}:{operation}"));
            });

        }

        #endregion

    }

}
