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

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The local controller's log on disk: the file it writes, from its very
    /// first line.
    /// </summary>
    /// <remarks>
    /// What goes into a log file, which file it goes into, and what happens on
    /// the day the file cannot be written is the node's, and asked in
    /// WWCP_Node_Tests. What is here is that a controller attaches its file
    /// early enough.
    /// </remarks>
    [TestFixture]
    public class FileLogTests
    {

        #region AControllerWritesItsVeryFirstEntryIntoTheFile()

        /// <summary>
        /// The file is attached before the controller says anything at all.
        /// </summary>
        /// <remarks>
        /// The first entry a controller writes is that it is starting up, and a
        /// file attached after that would begin in the middle of the story -
        /// with the one line that dates the run already missing from it.
        /// </remarks>
        [Test]
        public async Task AControllerWritesItsVeryFirstEntryIntoTheFile()
        {

            var controllerDirectory  = TestControllers.TemporaryDirectory("file-log-controller");
            var logs                 = Path.Combine(controllerDirectory, "logs");

            try
            {

                var controller = TestControllers.New(controllerDirectory, TestControllers.Offline, LogPath: logs);

                String[] lines;

                try
                {

                    Assert.That(controller.LogPath, Is.EqualTo(Path.GetFullPath(logs)));

                    var file = Directory.GetFiles(logs, "localcontroller-*.log").Single();

                    using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

                    lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);

                }
                finally
                {
                    await controller.DisposeAsync();
                }

                Assert.That(lines.FirstOrDefault(), Does.Contain("[lc] Local controller v").And.Contain("starting up."));

            }
            finally
            {
                TestControllers.Remove(controllerDirectory);
            }

        }

        #endregion

    }

}
