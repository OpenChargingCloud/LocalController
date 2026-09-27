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

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// What a local controller adds to the resources every node has, and the
    /// role of whoever runs the site it stands on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The node brings the viewer, who may look at everything, and the
    /// administrators, who may do everything - the certificates included,
    /// which is the one resource nobody else may edit here: somebody who can
    /// add a certificate authority can let in a charging station that nobody
    /// issued a password to, and who this controller is and whom it believes
    /// is decided twice in the life of the box.
    /// </para>
    /// <para>
    /// The configuration file may add roles to these and say differently what
    /// one of them may do - see the node's "roles" section. What is written
    /// here is what a local controller is when its file says nothing.
    /// </para>
    /// </remarks>
    public static class LocalControllerAccess
    {

        #region Resources

        /// <summary>
        /// The line up to the charging station management system: where it
        /// goes, how it is dialled, and what this controller signs in with.
        /// </summary>
        public const String  CSMS      = "csms";

        /// <summary>
        /// The server the charging stations connect to, and which of them may:
        /// its port, the security profiles it accepts, what it logs, the names
        /// it is reachable as - and the charging station logins and their
        /// groups.
        /// </summary>
        /// <remarks>
        /// The logins with the server rather than a resource of their own: a
        /// port nobody may come through is the same as no port, and one
        /// question to whoever runs the site is what the page asks.
        /// </remarks>
        public const String  Stations  = "stations";

        /// <summary>
        /// Both.
        /// </summary>
        public static readonly IReadOnlyList<String>  Resources = [ CSMS, Stations ];

        #endregion

        #region Roles

        /// <summary>
        /// Whoever runs the site: may point this controller at other name and
        /// time servers and ask whether they work, may change the line up to
        /// the CSMS, and adds and takes out charging stations - but may not
        /// touch the certificates.
        /// </summary>
        /// <remarks>
        /// Day-to-day operation. A local controller in a car park is reached by
        /// whoever runs the site long before it is reached by whoever installed
        /// it, and a name server that moved, or a charging station that was
        /// replaced, is theirs to put right.
        /// </remarks>
        public static readonly Role  CPO    = new ("cpo",
                                                   [ Permission.Read(Permission.AnyResource),
                                                     Permission.Edit(NodeResources.DNS),
                                                     Permission.Run (NodeResources.DNS),
                                                     Permission.Edit(NodeResources.NTS),
                                                     Permission.Run (NodeResources.NTS),
                                                     Permission.Edit(CSMS),
                                                     Permission.Edit(Stations) ],
                                                   "runs the site: its name and time servers, the line up to the CSMS and the charging stations");

        /// <summary>
        /// The one role a local controller adds to the node's.
        /// </summary>
        public static readonly IReadOnlyList<Role>  Roles = [ CPO ];

        #endregion

    }

}
