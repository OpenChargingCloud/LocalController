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

using System.Reflection;

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;

using LC = cloud.charging.open.LocalController.LocalController;

#endregion

namespace cloud.charging.open.LocalController.CommandLine
{

    /// <summary>
    /// The command line of a running local controller.
    /// </summary>
    /// <remarks>
    /// The node's command line, with the commands every node has - syncNTS
    /// among them - and the console until 'quit', Ctrl+C or SIGTERM. What only
    /// a local controller can be told is a command built from a ControllerCLI
    /// in this assembly, found as the node's are: a new command is a new file
    /// and nothing else.
    ///
    /// The local controller's, not a program's: the controller hands it to
    /// whoever signs in over SSH itself, so that every program that runs one -
    /// LocalControllerCLI, or a test environment with several nodes in one
    /// process - serves the same commands. A program keeps none of its own
    /// beside them: a command found twice is run by neither.
    ///
    /// Not in a namespace called CLI: Styx's command line class is called
    /// that, and a namespace of the same name one level up would be found
    /// first.
    /// </remarks>
    public class ControllerCLI : NodeCLI
    {

        #region Data

        /// <summary>
        /// What stands in front of the command being typed.
        /// </summary>
        public const String Prompt = "LocalController> ";

        #endregion

        #region Properties

        /// <summary>
        /// The local controller these commands are about.
        /// </summary>
        public LC  Controller  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create the command line of the given local controller.
        /// </summary>
        /// <param name="Controller">The running local controller.</param>
        /// <param name="AssembliesWithCLICommands">Further assemblies to search for commands. This one is searched either way.</param>
        public ControllerCLI(LC                 Controller,
                             params Assembly[]  AssembliesWithCLICommands)

            : base(Controller, AssembliesWithCLICommands)

        {

            this.Controller = Controller;

            RegisterCLIType(typeof(ControllerCLI));

        }

        /// <summary>
        /// Create the command line of the given LC on the given terminal,
        /// for the given caller - a session over SSH.
        /// </summary>
        /// <param name="Controller">The running LC.</param>
        /// <param name="Terminal">What the command line is typed at and written on.</param>
        /// <param name="Caller">Who is typing at it.</param>
        /// <param name="AssembliesWithCLICommands">Further assemblies to search for commands. This one and the node's are searched either way.</param>
        public ControllerCLI(LC                 Controller,
                             ICLITerminal       Terminal,
                             CLICaller          Caller,
                             params Assembly[]  AssembliesWithCLICommands)

            : base(Controller, Terminal, Caller, AssembliesWithCLICommands)

        {

            this.Controller = Controller;

            RegisterCLIType(typeof(ControllerCLI));

        }

        #endregion


        #region (protected override) GetPrompt()

        /// <summary>
        /// What this program is, because a local controller is usually started
        /// on a bench beside the charging stations and a CSMS it talks to, and
        /// their consoles should not have to be told apart by what scrolls past
        /// on them.
        /// </summary>
        /// <remarks>
        /// Not its OCPP identity, which is a configured value a CSMS knows it
        /// by: two controllers on one bench usually share the default one, and
        /// would read the same.
        /// </remarks>
        protected override String GetPrompt()

            => Prompt;

        #endregion

    }

}
