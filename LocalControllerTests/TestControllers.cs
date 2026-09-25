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
using System.Net.Sockets;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Configuration;

using cloud.charging.open.LocalController.Configuration;
using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Building local controllers to test against.
    /// </summary>
    /// <remarks>
    /// Its own class rather than a few protected methods on the fixture base,
    /// because not every test wants a controller that is started and taken away
    /// again for it: the clock is a question one can be asked without a socket,
    /// and the shutdown tests have to do the stopping themselves.
    /// </remarks>
    internal static class TestControllers
    {

        #region New(Directory, Configuration = null, Clock = null)

        /// <summary>
        /// A local controller, built and not started.
        /// </summary>
        /// <remarks>
        /// Not started, and that is often the point: it is <c>Start()</c> that
        /// puts a timer on the network to check the clock, so a controller that
        /// was only built is also one that will not quietly go and ask a time
        /// server in the middle of a test run.
        ///
        /// It is also <c>Start()</c> that makes the accounts, so a controller
        /// that was only built has none yet and no password to show for them.
        /// </remarks>
        /// <param name="Directory">Where its accounts and its configuration go; created when it does not exist.</param>
        /// <param name="Configuration">What its configuration file says, or null for a controller nobody has configured.</param>
        /// <param name="Clock">Where it reads the time, for a test that needs to decide what time it is.</param>
        /// <param name="LogToConsole">Whether its log reaches the console, for a test about who gets to write there. Off otherwise, because a test run's console is for the test run.</param>
        /// <param name="LogPath">A directory for its log files, for a test about those. None otherwise.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX ends up in its log, for a test about that. Off otherwise, because every controller of a test run would pick up every other one's lines.</param>
        public static LocalController New(String         Directory,
                                          JObject?       Configuration    = null,
                                          TimeProvider?  Clock            = null,
                                          Boolean        LogToConsole     = false,
                                          String?        LogPath          = null,
                                          Boolean        BridgeDebugLog   = false)
        {

            System.IO.Directory.CreateDirectory(Directory);

            var configFile = Path.Combine(Directory, "configuration.json");

            if (Configuration is not null)
                File.WriteAllText(configFile, Configuration.ToString());

            return new LocalController(
                       HTTPPort:         IPPort.Parse(FreePort()),
                       AccountsPath:     Path.Combine(Directory, "accounts"),
                       ConfigFile:       new WWCPConfigFile(configFile),
                       LogToConsole:     LogToConsole,
                       LogPath:          LogPath,
                       BridgeDebugLog:   BridgeDebugLog,
                       TimeProvider:     Clock
                   );

        }

        #endregion

        #region Offline

        /// <summary>
        /// A configuration with the time client switched off.
        /// </summary>
        /// <remarks>
        /// Written before a controller is built, because that is when it is
        /// read, and switched off there rather than afterwards because it is
        /// <c>StartCheckingTheClock</c> inside <c>Start()</c> that would
        /// otherwise schedule the first check. A test run has no business
        /// asking a public time server anything.
        /// </remarks>
        public static JObject Offline

            => new (
                   new JProperty("nts", new JObject(
                       new JProperty("enabled", false)
                   ))
               );

        #endregion

        #region FreePort()

        /// <summary>
        /// A TCP port nobody was listening on a moment ago.
        /// </summary>
        /// <remarks>
        /// Asked of the operating system rather than counted up from a
        /// constant, so that these tests do not fight with a controller
        /// somebody has running on 2350 while they write them - and do not
        /// fight with each other when the runner is told to parallelise.
        ///
        /// There is a gap between letting the port go and binding it again, and
        /// nothing here can close it; what it buys is that the gap is
        /// milliseconds wide instead of the whole test run.
        /// </remarks>
        public static UInt16 FreePort()
        {

            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);

            listener.Start();

            try
            {
                return (UInt16) ((IPEndPoint) listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }

        }

        #endregion

        #region TemporaryDirectory(Purpose)

        /// <summary>
        /// A directory of its own for one test, so that no two of them read
        /// each other's accounts or configuration.
        /// </summary>
        public static String TemporaryDirectory(String Purpose)

            => Path.Combine(
                   Path.GetTempPath(),
                   $"lc-{Purpose}-{Guid.NewGuid().ToString("N")[..12]}"
               );

        #endregion

        #region Remove(Directory)

        /// <summary>
        /// Take a test's directory away again.
        /// </summary>
        /// <remarks>
        /// A directory that survives a failed run is untidy and nothing more,
        /// so this never throws: failing a teardown over it would hide the
        /// failure that actually matters.
        /// </remarks>
        public static void Remove(String? Directory)
        {

            try
            {
                if (Directory is not null && System.IO.Directory.Exists(Directory))
                    System.IO.Directory.Delete(Directory, true);
            }
            catch (IOException)
            { }
            catch (UnauthorizedAccessException)
            { }

        }

        #endregion

    }

}
