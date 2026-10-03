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

using System.Text;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// What the log says on stderr about itself, and that it asks whoever has
    /// the console before it says it.
    /// </summary>
    /// <remarks>
    /// Rare things - a listener that failed, a log file that cannot be written
    /// - and rare enough that for a while they went straight past the command
    /// line: into the middle of a command somebody was typing, where the
    /// command line did not know they were and wrote over them. A command
    /// line cannot be driven without a terminal, so what is checked is the
    /// promise it depends on: every one of these lines is written inside the
    /// block it was handed, and none of them beside it. That promise is the
    /// node's, and asked in WWCP_Node_Tests; what is here is that a
    /// controller hands its console over with its complaints.
    /// </remarks>
    [TestFixture]
    public class LogComplaintTests
    {

        #region (record) Line

        /// <summary>
        /// One line on the console, and whether it was written while the block
        /// had the console.
        /// </summary>
        private readonly record struct Line(String Text, Boolean InBlock);

        #endregion

        #region (class) WatchedConsole

        /// <summary>
        /// Stands in for the console, and notes of every line whether it was
        /// written inside the block.
        /// </summary>
        private sealed class WatchedConsole : TextWriter
        {

            private readonly StringBuilder current = new();

            public List<Line>  Lines    { get; } = [];

            public Boolean     InBlock  { get; private set; }

            public override Encoding Encoding
                => Encoding.UTF8;

            /// <summary>
            /// What the command line does, as far as this needs it: whatever is
            /// written while it runs is written with the console to itself.
            /// </summary>
            public void Block(Action Write)
            {

                InBlock = true;

                try
                {
                    Write();
                }
                finally
                {
                    InBlock = false;
                }

            }

            public override void Write(Char Value)
            {

                if (Value == '\n')
                {
                    Lines.Add(new Line(current.ToString().TrimEnd('\r'), InBlock));
                    current.Clear();
                }
                else
                    current.Append(Value);

            }

        }

        #endregion

        #region Data

        private const String      Broken         = "An event log listener failed: The listener broke.";

        private String            directory      = "";
        private WatchedConsole    console        = default!;
        private TextWriter        previousOut    = default!;
        private TextWriter        previousError  = default!;
        private LocalController?  controller;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void SetUp()
        {

            directory      = TestControllers.TemporaryDirectory("log-complaint");
            console        = new WatchedConsole();

            previousOut    = Console.Out;
            previousError  = Console.Error;

            // Both, and into the same place: the entries of a console log go to
            // stdout, the complaints to stderr, and on a terminal they meet on
            // one screen.
            Console.SetOut  (console);
            Console.SetError(console);

        }

        [TearDown]
        public async Task TearDown()
        {

            Console.SetOut  (previousOut);
            Console.SetError(previousError);

            if (controller is not null)
                await controller.DisposeAsync();

            controller = null;

            console.Dispose();

            TestControllers.Remove(directory);

        }

        #endregion


        #region AControllerHandsItsComplaintsOverWithItsConsole(LogToConsole)

        /// <summary>
        /// What the command line is handed by ShareConsoleWith covers the
        /// complaints too - also for a controller whose entries stay off the
        /// console, since its complaints still go there.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void AControllerHandsItsComplaintsOverWithItsConsole(Boolean LogToConsole)
        {

            // Never started, and with the time client off like every other
            // controller of the suite.
            controller = TestControllers.New(directory, TestControllers.Offline, LogToConsole: LogToConsole);

            controller.ShareConsoleWith(console.Block);

            controller.Log.OnLogged += _ => throw new InvalidOperationException("The listener broke.");

            controller.Log.Info("Something happened.", "test");

            Assert.That(console.Lines.Where(line => line.Text == Broken), Is.EqualTo(new[] { new Line(Broken, InBlock: true) }),
                        "Everything on the console: " + String.Join(" | ", console.Lines));

        }

        #endregion

    }

}
