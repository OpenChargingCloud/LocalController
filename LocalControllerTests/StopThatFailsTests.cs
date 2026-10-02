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

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// A local controller whose stopping fails is let go of all the same: what
    /// is its own, and then what the node below it holds.
    /// </summary>
    /// <remarks>
    /// It used to stop first and let go afterwards, with nothing between to
    /// catch a stop that threw: its certificate stores and everything of the
    /// node below - the log file among it - stayed held.
    /// </remarks>
    [TestFixture]
    public class StopThatFailsTests
    {

        #region Data

        private String                        directory  = default!;
        private readonly List<FailingToStop>  made       = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeTheDirectory()
        {
            directory = TestControllers.TemporaryDirectory("stop-fails");
        }

        [TearDown]
        public async Task LetGoOfEverything()
        {

            foreach (var controller in made)
            {
                try
                {
                    await controller.DisposeAsync();
                }
                catch (InvalidOperationException)
                {
                    // Its stopping fails; that is what it is for.
                }
            }

            made.Clear();

            TestControllers.Remove(directory);

        }

        #endregion


        #region (helper) Read(File)

        /// <summary>
        /// What a log file says, read beside whoever may still be writing it.
        /// </summary>
        private static String Read(String File)
        {

            using var stream = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();

        }

        #endregion


        #region LettingGoOfAControllerWhoseStopFailsLetsGoOfItsLogFile()

        /// <summary>
        /// Letting go of a controller whose stopping fails says that it failed
        /// - and lets go of it all the same, down to the node below: nothing
        /// logged afterwards reaches its log file, which is closed.
        /// </summary>
        [Test]
        public async Task LettingGoOfAControllerWhoseStopFailsLetsGoOfItsLogFile()
        {

            var controller = await TestPorts.StartedOnFreshPorts(() => {

                var here = Path.Combine(directory, Guid.NewGuid().ToString("N")[..8]);

                Directory.CreateDirectory(here);

                var configFile = Path.Combine(here, "configuration.json");

                File.WriteAllText(configFile, TestControllers.Offline.ToString());

                return new FailingToStop(
                           HTTPPort:      IPPort.Parse(TestPorts.Free()),
                           AccountsPath:  Path.Combine(here, "accounts"),
                           ConfigFile:    new WWCPConfigFile(configFile),
                           LogPath:       Path.Combine(here, "logs")
                       );

            });

            made.Add(controller);

            controller.Log.Notice("Written while the controller runs.", "test");

            var file = controller.LogFile;

            Assert.That(file, Is.Not.Null, "the controller wrote no log file");

            Assert.That(async () => await controller.DisposeAsync(),
                        Throws.InstanceOf<InvalidOperationException>().With.Message.EqualTo(FailingToStop.Why));

            controller.Log.Notice("Written after the controller was let go of.", "test");

            var written = Read(file!);

            Assert.Multiple(() => {
                Assert.That(written, Does.Contain    ("Written while the controller runs."));
                Assert.That(written, Does.Not.Contain("Written after the controller was let go of."),
                            "the log file of a controller that was let go of was still written");
            });

        }

        #endregion


        #region (private class) FailingToStop

        /// <summary>
        /// A local controller whose stopping fails: what it ends before the
        /// server stops - its CSMS and its station port - throws instead.
        /// </summary>
        private sealed class FailingToStop(IPPort          HTTPPort,
                                           String          AccountsPath,
                                           WWCPConfigFile  ConfigFile,
                                           String          LogPath)

            : LocalController(HTTPPort:        HTTPPort,
                              AccountsPath:    AccountsPath,
                              ConfigFile:      ConfigFile,
                              LogToConsole:    false,
                              LogPath:         LogPath,
                              BridgeDebugLog:  false)

        {

            /// <summary>
            /// What its stopping says.
            /// </summary>
            public const String Why = "What this controller holds open would not end.";

            /// <summary>
            /// The file its log is written to, once something was.
            /// </summary>
            public String? LogFile
                => fileLog?.CurrentFile;

            protected override Task OnStopping()
                => Task.FromException(new InvalidOperationException(Why));

        }

        #endregion

    }

}
