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

using cloud.charging.open.protocols.WWCP.Node.Web;

using OCPPv2_1_LC = cloud.charging.open.protocols.OCPPv2_1.LocalController;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// Who may sign what is sent to this local controller itself: the roles of
    /// its web page and its command line (white paper "Signed OCPP Messages and
    /// Signer Roles", section 5.3, phase 3 of chapter 7).
    /// </summary>
    public partial class LocalController
    {

        #region (private) AttachSignaturePolicy(Node)

        /// <summary>
        /// Give the node's signature policy what it needs to hold a signer
        /// against the policy table: the direction of what this controller is
        /// sent, what each of its roles may do, and what each action needs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only the node's own policy, which decides what is addressed to this
        /// controller. What it forwards goes through the policy of the
        /// forwarding, and is held by the station it goes to against the
        /// station's own resources.
        /// </para>
        /// <para>
        /// The direction is a station's: the line up to the CSMS is the one a
        /// station has, and an entry a CSMS writes for what it sends its
        /// stations is about this controller too.
        /// </para>
        /// <para>
        /// Nothing of it is looked at while the mode of the policy is "Off",
        /// the default: this controller then behaves as before it had a table.
        /// </para>
        /// </remarks>
        private void AttachSignaturePolicy(OCPPv2_1_LC.TestLocalControllerNode Node)
        {

            var policy = Node.OCPP.SignaturePolicy;

            policy.IncomingDirection    ??= "CSMS->CS";
            policy.PermissionsOfRole    ??= PermissionsOfRole;

            // The entries of SetVariables and GetVariables are decided one by one,
            // by the node (ActionsDecidedPerEntry), each by its component's resource.
            policy.ResourceOfComponent    = ResourceOfComponent;

            if (!policy.ActionPermissions.Any())
                policy.AddActionPermissions(LocalControllerActionPermissions.Default());

        }

        #endregion

        #region (private static) ResourceOfComponent(Component)

        /// <summary>
        /// The resource of this controller's role model a component of its own
        /// device model belongs to.
        /// </summary>
        /// <remarks>
        /// The name servers and the time are "dns" and "nts", as for every node.
        /// EVQICtrlr is the keys of the station port, whose switch changes whom
        /// every station of the site trusts - a trust decision, as a change of
        /// the certificates is: "certificates". TLSClientController is the line
        /// up to the CSMS: "csms", as SetNetworkProfile.
        /// </remarks>
        private static String ResourceOfComponent(String Component)

            => Component switch {
                   "EVQICtrlr"            => NodeResources.Certificates,
                   "TLSClientController"  => LocalControllerAccess.CSMS,
                   _                      => protocols.WWCP.SignaturePolicy.DefaultResourceOfComponent(Component)
               };

        #endregion

        #region (private) PermissionsOfRole(RoleName)

        /// <summary>
        /// What the role of the given name may do here, spelt out: the roles of
        /// this controller as its configuration file leaves them, and nothing
        /// for a name that is none of them.
        /// </summary>
        private IEnumerable<String> PermissionsOfRole(String RoleName)

            => Access.RoleNamed(RoleName) is Role role
                   ? Access.PermissionsOf([ role ]).Select(permission => permission.ToString())
                   : [];

        #endregion

    }

}
