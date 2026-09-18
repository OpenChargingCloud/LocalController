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

using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The roles this local controller knows, and what each of them grants.
    /// </summary>
    /// <remarks>
    /// The permissions are this controller's own vocabulary and are checked
    /// here; who holds them is a user group in the HTTPExt API, and that is
    /// checked over the wire in <see cref="AuthenticationTests"/>.
    /// </remarks>
    public class UserRoleTests
    {

        #region EveryRoleGrantsSomethingAndTheAdminGrantsEverything()

        [Test]
        public void EveryRoleGrantsSomethingAndTheAdminGrantsEverything()
        {

            var everything = Enum.GetValues<Permissions>().
                                  Where(permission => permission != Permissions.None).
                                  Aggregate(Permissions.None, (all, one) => all | one);

            Assert.Multiple(() => {

                foreach (var role in UserRole.All)
                    Assert.That(role.Permissions, Is.Not.EqualTo(Permissions.None),
                                $"The {role.Name} role grants nothing at all.");

                Assert.That(UserRole.SystemAdmin.Permissions, Is.EqualTo(everything),
                            "The system administrator does not grant everything this controller knows.");

                // A viewer may look and do nothing, which is the whole of it.
                Assert.That(UserRole.Viewer.Permissions, Is.EqualTo(Permissions.ReadConfiguration));

            });

        }

        #endregion

        #region RolesAddUpAndAreNamedAsTheBrowserReadsThem()

        [Test]
        public void RolesAddUpAndAreNamedAsTheBrowserReadsThem()
        {

            var together = new[] { UserRole.Viewer, UserRole.CPO }.PermissionsOf();

            Assert.Multiple(() => {

                Assert.That(together.HasFlag(Permissions.ReadConfiguration),     Is.True);
                Assert.That(together.HasFlag(Permissions.ChangeNetworkSettings), Is.True);

                // camelCase, because that is how the bundle spells them.
                Assert.That(Permissions.ReadConfiguration.Names(), Is.EquivalentTo(new[] { "readConfiguration" }));
                Assert.That(Permissions.None.Names(),              Is.Empty);

            });

        }

        #endregion

        #region ARoleIsFoundByItsNameInAnyCase()

        [Test]
        public void ARoleIsFoundByItsNameInAnyCase()
        {

            Assert.Multiple(() => {

                Assert.That(UserRole.TryParse("cpo",         out var lower, out _), Is.True);
                Assert.That(lower,                                                  Is.EqualTo(UserRole.CPO));

                Assert.That(UserRole.TryParse("SystemAdmin", out var mixed, out _), Is.True);
                Assert.That(mixed,                                                  Is.EqualTo(UserRole.SystemAdmin));

                Assert.That(UserRole.TryParse("  viewer  ",  out var padded, out _), Is.True);
                Assert.That(padded,                                                  Is.EqualTo(UserRole.Viewer));

                Assert.That(UserRole.TryParse("installer",   out _, out var error),  Is.False,
                            "A role this controller does not have was accepted.");
                Assert.That(error,                                                   Does.Contain("installer"));

            });

        }

        #endregion

        #region ARoleIsTheGroupOfTheSameName()

        /// <summary>
        /// The name is the whole of the link between a role here and a user
        /// group in the HTTPExt API, so a role whose name cannot be a group
        /// identification is a role nobody could ever be put in.
        /// </summary>
        [Test]
        public void ARoleIsTheGroupOfTheSameName()
        {

            Assert.Multiple(() => {
                foreach (var role in UserRole.All)
                    Assert.That(role.GroupId.ToString(), Is.EqualTo(role.Name));
            });

        }

        #endregion

    }

}
