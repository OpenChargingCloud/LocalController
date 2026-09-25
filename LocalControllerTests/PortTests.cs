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

using System.Net.Sockets;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The second port a local controller listens on: the one its charging
    /// stations connect to, beside the web interface's.
    /// </summary>
    /// <remarks>
    /// Taken once the web interface has its port and before the controller
    /// calls itself started. Somebody starting a second copy of a controller
    /// used to get the operating system's own words for a port in use - in
    /// German on a German Windows - with neither the port nor what it was for
    /// named anywhere, and a web interface still listening for a controller
    /// that had not come up.
    /// </remarks>
    public class PortTests
    {

        #region Data

        private String directory = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("ports");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void RemoveTheDirectory()
        {
            TestControllers.Remove(directory);
        }

        #endregion


        #region AStationPortSomebodyElseHasIsNamedAndTheWebPortLetGo()

        /// <summary>
        /// A station port that is taken ends the start with a sentence naming
        /// the port and what it was for - and the web interface's port, which
        /// was had by then, is let go of again.
        /// </summary>
        [Test]
        public async Task AStationPortSomebodyElseHasIsNamedAndTheWebPortLetGo()
        {

            var squatter  = new TcpListener(System.Net.IPAddress.Loopback, 0);
            squatter.Start();

            try
            {

                var taken = (UInt16) ((System.Net.IPEndPoint) squatter.LocalEndpoint).Port;

                await using var controller = TestControllers.New(
                                                 directory,
                                                 new JObject(
                                                     new JProperty("nts",         new JObject(new JProperty("enabled", false))),
                                                     new JProperty("ocppServer",  new JObject(
                                                         new JProperty("enabled",  true),
                                                         new JProperty("address",  "127.0.0.1"),
                                                         new JProperty("port",     taken)
                                                     ))
                                                 )
                                             );

                var problem = Assert.ThrowsAsync<PortUnavailableException>(controller.Start);

                Assert.Multiple(() => {

                    Assert.That(problem!.Whose,              Is.EqualTo(LocalController.StationServerPort));
                    Assert.That(problem.Port,                Is.EqualTo(IPPort.Parse(taken)));
                    Assert.That(problem.Message,             Does.StartWith($"The charging station server could not be given port {taken}"));

                    Assert.That(CanBeHad(controller.HTTPPort),  Is.True,
                                "The web interface's port is still held by a controller that did not start.");

                    Assert.That(controller.ConfigurationJSON()["http"]?.Value<Boolean>("running"),  Is.False,
                                "A controller whose stations cannot reach it calls itself started.");

                });

            }
            finally
            {
                squatter.Stop();
            }

        }

        #endregion


        #region (private static) CanBeHad(Port)

        /// <summary>
        /// Whether something else could listen on this port of the loopback
        /// address right now.
        /// </summary>
        private static Boolean CanBeHad(IPPort Port)
        {

            var listener = new TcpListener(System.Net.IPAddress.Loopback, Port.ToUInt16());

            try
            {
                listener.Start();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            finally
            {
                listener.Stop();
            }

        }

        #endregion

    }

}
