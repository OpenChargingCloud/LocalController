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

using System.Diagnostics.CodeAnalysis;

#endregion

namespace cloud.charging.open.LocalController.Web
{

    /// <summary>
    /// What somebody signed in to this local controller is allowed to do.
    /// </summary>
    /// <remarks>
    /// Flags rather than a list, because a permission is asked about one at a
    /// time and answered by a single test - and because the set a role grants
    /// is then a constant instead of a collection to be built and searched.
    ///
    /// Only what this local controller actually enforces is named here. A
    /// permission with nothing behind it is a promise made to whoever reads the
    /// login file and not kept: the day this controller can be told which CSMS
    /// to call and which charging stations may call it, those become
    /// permissions of their own rather than arriving quietly inside one that
    /// already exists.
    /// </remarks>
    [Flags]
    public enum Permissions : UInt32
    {

        /// <summary>
        /// Nothing at all. What an unknown role would grant.
        /// </summary>
        None                   = 0,

        /// <summary>
        /// See how this local controller is configured.
        /// </summary>
        ReadConfiguration      = 1,

        /// <summary>
        /// Change how this local controller reaches the network: its name
        /// resolution and where it reads the time.
        /// </summary>
        /// <remarks>
        /// Reversible, and it complains: a wrong name server makes the
        /// controller say so, and the next change puts it right. That is what
        /// will separate it from the settings describing what is around this
        /// controller - the CSMS above it, the charging stations below - where
        /// being wrong is quiet and somebody else notices first.
        /// </remarks>
        ChangeNetworkSettings  = 2,

        /// <summary>
        /// Make this local controller ask a name server or a time server
        /// something, to find out whether it can.
        /// </summary>
        /// <remarks>
        /// Its own permission and not part of reading: a diagnostic sends
        /// traffic from this controller to a host somebody named, which is more
        /// than it sounds like to hand to everybody who may look at a page.
        /// </remarks>
        RunDiagnostics         = 4

    }


    /// <summary>
    /// A role somebody signs in as: a name, and the permissions it carries.
    /// </summary>
    /// <remarks>
    /// A closed set: a role this local controller has never heard of is a role
    /// it cannot enforce. So an unrecognised name is refused when the login
    /// file is read, rather than quietly granting nothing - or, far worse,
    /// being taken for a known one because it looks similar.
    /// </remarks>
    /// <param name="Name">How the role is written in the login file.</param>
    /// <param name="Permissions">What it grants.</param>
    public sealed record UserRole(String       Name,
                                  Permissions  Permissions)
    {

        #region Data

        /// <summary>
        /// May look at this local controller, and do nothing to it.
        /// </summary>
        public static readonly UserRole  Viewer       = new ("viewer",
                                                             Permissions.ReadConfiguration);

        /// <summary>
        /// The operator of this local controller: may point it at other name
        /// and time servers, and may test them.
        /// </summary>
        /// <remarks>
        /// Day-to-day operation. A local controller in a car park is reached by
        /// whoever runs the site long before it is reached by whoever installed
        /// it, and a name server that moved is their problem to fix.
        /// </remarks>
        public static readonly UserRole  CPO          = new ("cpo",
                                                             Permissions.ReadConfiguration     |
                                                             Permissions.ChangeNetworkSettings |
                                                             Permissions.RunDiagnostics);

        /// <summary>
        /// Everything this local controller can be told, by whoever is trusted
        /// with all of it at once.
        /// </summary>
        /// <remarks>
        /// The same as the CPO today, and not the same role: what this
        /// controller may be told will grow - which CSMS it calls, which
        /// charging stations may call it - and those belong here first.
        /// </remarks>
        public static readonly UserRole  SystemAdmin  = new ("systemadmin",
                                                             Permissions.ReadConfiguration     |
                                                             Permissions.ChangeNetworkSettings |
                                                             Permissions.RunDiagnostics);

        /// <summary>
        /// Every role this local controller knows.
        /// </summary>
        public static readonly IReadOnlyList<UserRole>  All = [ Viewer, CPO, SystemAdmin ];

        #endregion


        #region (static) TryParse(Text, out Role, out Error)

        /// <summary>
        /// A role by the name the login file writes it under, in any case.
        /// </summary>
        public static Boolean TryParse(String?                           Text,
                                       [NotNullWhen(true)]  out UserRole?  Role,
                                       [NotNullWhen(false)] out String?    Error)
        {

            Role   = All.FirstOrDefault(role => String.Equals(role.Name, Text?.Trim(), StringComparison.OrdinalIgnoreCase));

            Error  = Role is null
                         ? $"\"{Text}\" is not a role this local controller knows. Known roles: {String.Join(", ", All.Select(role => role.Name))}."
                         : null;

            return Role is not null;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()
            => Name;

        #endregion

    }


    /// <summary>
    /// What a set of roles adds up to.
    /// </summary>
    public static class UserRoleExtensions
    {

        #region PermissionsOf(this Roles)

        /// <summary>
        /// Everything the given roles grant together.
        /// </summary>
        public static Permissions PermissionsOf(this IEnumerable<UserRole> Roles)
        {

            var permissions = Permissions.None;

            foreach (var role in Roles)
                permissions |= role.Permissions;

            return permissions;

        }

        #endregion

        #region Names(this Permissions)

        /// <summary>
        /// The permissions as the web interface reads them, so that a page can
        /// grey out what this browser may not do instead of finding out by
        /// being refused.
        /// </summary>
        /// <remarks>
        /// What the browser is told is a copy of what the local controller
        /// enforces, and not the enforcement: every request is checked again on
        /// arrival. A greyed-out button is a courtesy, not a lock.
        /// </remarks>
        public static IEnumerable<String> Names(this Permissions Permissions)

            => Enum.GetValues<Permissions>().
                    Where (permission => permission != Web.Permissions.None && Permissions.HasFlag(permission)).
                    Select(permission => Char.ToLowerInvariant(permission.ToString()[0]) + permission.ToString()[1..]);

        #endregion

    }

}
