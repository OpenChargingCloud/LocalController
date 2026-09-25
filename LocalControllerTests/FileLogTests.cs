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

using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The log on disk: what goes into it, which file it goes into, and what
    /// happens on the day the file cannot be written.
    /// </summary>
    /// <remarks>
    /// Everything here is about afterwards - the file is the one of the three
    /// places a log is kept that is still there when somebody asks what
    /// happened last night - so every test reads the file back rather than
    /// asking the writer what it believes it wrote. The charging station's
    /// tests, and the vehicle's before them.
    /// </remarks>
    [TestFixture]
    public class FileLogTests
    {

        #region (class) MovableClock

        /// <summary>
        /// A clock that says whatever time it was last set to.
        /// </summary>
        private sealed class MovableClock(DateTimeOffset Start) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = Start;
            public override DateTimeOffset GetUtcNow() => Now;
        }

        #endregion

        #region Data

        /// <summary>
        /// A quarter to two in the afternoon, UTC, on a day picked so that the
        /// expected lines can be written down in full.
        /// </summary>
        private static readonly DateTimeOffset  Afternoon  = new (2026, 9, 23, 13, 45, 1, 123, TimeSpan.Zero);

        private String        directory  = "";
        private MovableClock  clock      = default!;
        private EventLog      log        = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeALog()
        {
            directory  = TestControllers.TemporaryDirectory("file-log");
            clock      = new MovableClock(Afternoon);
            log        = new EventLog(TimeProvider: clock);
        }

        [TearDown]
        public void TakeItAwayAgain()
        {
            TestControllers.Remove(directory);
        }

        #endregion


        #region EverythingIsWrittenDownToTheDebugEntries()

        /// <summary>
        /// Every entry, in the order it came, each on a line of its own.
        /// </summary>
        /// <remarks>
        /// Debug too, and that is the point of having a file: the console is
        /// given a level so that it stays readable, and a file nobody is
        /// reading has no such problem. The timestamp is written in full and
        /// in UTC, unlike on the console, because a file is read long after
        /// and often somewhere else.
        /// </remarks>
        [Test]
        public void EverythingIsWrittenDownToTheDebugEntries()
        {

            using (new FileLog(log, directory, FilePrefix: "localcontroller"))
            {
                log.Debug ("A debug line.",            "test");
                log.Info  ("An info line.",            "nts", "test", "cli");
                log.Notice("A line without any tags.");
            }

            Assert.That(File.ReadAllLines(Path.Combine(directory, "localcontroller-2026-09-23.log")),
                        Is.EqualTo(new[] {
                            "2026-09-23T13:45:01.123Z debug   [test] A debug line.",
                            "2026-09-23T13:45:01.123Z info    [nts test cli] An info line.",
                            "2026-09-23T13:45:01.123Z notice  A line without any tags."
                        }));

        }

        #endregion

        #region EachEntryIsOnDiskTheMomentItIsLogged()

        /// <summary>
        /// Flushed after every entry, and readable while it is being written.
        /// </summary>
        /// <remarks>
        /// Read here while the writer still holds the file, which is the
        /// situation that matters: a controller that is killed has its last
        /// lines on disk rather than in a buffer, and somebody following the
        /// file while the controller runs sees each entry as it happens.
        /// </remarks>
        [Test]
        public void EachEntryIsOnDiskTheMomentItIsLogged()
        {

            using var fileLog = new FileLog(log, directory, FilePrefix: "localcontroller");

            log.Info("Written, and not yet closed.", "test");

            using var reader = new StreamReader(new FileStream(fileLog.CurrentFile!,
                                                               FileMode.Open,
                                                               FileAccess.Read,
                                                               FileShare.ReadWrite));

            Assert.That(reader.ReadToEnd(), Does.Contain("Written, and not yet closed."));

        }

        #endregion

        #region AFileIsADayAndTheDayIsUTC()

        /// <summary>
        /// A new file at midnight - UTC midnight, as the timestamps in it are.
        /// </summary>
        /// <remarks>
        /// Half past one on a summer night in Germany is still the day before
        /// in UTC, and that is where it has to be found: in the file named for
        /// the day its own timestamp says.
        /// </remarks>
        [Test]
        public void AFileIsADayAndTheDayIsUTC()
        {

            using (new FileLog(log, directory, FilePrefix: "localcontroller"))
            {

                clock.Now = new DateTimeOffset(2026, 9, 23, 23, 59, 59, 999, TimeSpan.Zero);
                log.Info("The last moment of the day.", "test");

                clock.Now = new DateTimeOffset(2026, 9, 24,  1, 30,  0,   0, TimeSpan.FromHours(2));
                log.Info("Half past one in Berlin, still the 23rd in UTC.", "test");

                clock.Now = new DateTimeOffset(2026, 9, 24,  0,  0,  0,   0, TimeSpan.Zero);
                log.Info("The first moment of the next.", "test");

            }

            Assert.Multiple(() => {

                Assert.That(File.ReadAllLines(Path.Combine(directory, "localcontroller-2026-09-23.log")),
                            Is.EqualTo(new[] {
                                "2026-09-23T23:59:59.999Z info    [test] The last moment of the day.",
                                "2026-09-23T23:30:00.000Z info    [test] Half past one in Berlin, still the 23rd in UTC."
                            }));

                Assert.That(File.ReadAllLines(Path.Combine(directory, "localcontroller-2026-09-24.log")),
                            Is.EqualTo(new[] {
                                "2026-09-24T00:00:00.000Z info    [test] The first moment of the next."
                            }));

            });

        }

        #endregion

        #region NothingThatWasThereIsOverwritten()

        /// <summary>
        /// A controller started twice on one day adds to that day's file.
        /// </summary>
        [Test]
        public void NothingThatWasThereIsOverwritten()
        {

            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "localcontroller-2026-09-23.log"), "What the morning's run wrote.\n");

            using (new FileLog(log, directory, FilePrefix: "localcontroller"))
                log.Info("What the afternoon's run wrote.", "test");

            Assert.That(File.ReadAllLines(Path.Combine(directory, "localcontroller-2026-09-23.log")),
                        Is.EqualTo(new[] {
                            "What the morning's run wrote.",
                            "2026-09-23T13:45:01.123Z info    [test] What the afternoon's run wrote."
                        }));

        }

        #endregion

        #region AFileThatCannotBeWrittenIsSaidOnceAndItsGapWrittenDown()

        /// <summary>
        /// A file that cannot be written is said once, every entry is tried
        /// again, and the first one that makes it brings the size of the gap.
        /// </summary>
        /// <remarks>
        /// A directory standing where the day's file should be is how this is
        /// made to fail: it fails the same way on every platform and on every
        /// attempt, and taking it away again is how the disk "recovers".
        ///
        /// Four things are measured, because each has a failure that looks
        /// fine from the outside. A complaint per entry would bury the console
        /// of a controller that runs for weeks under one line per log entry. A
        /// writer that gave up for good would cost those weeks the file over an
        /// hour of full disk. A file that simply carried on afterwards would
        /// have a hole in it that nobody would find until they needed what was
        /// in it. And the entry after the first one that makes it comes on its
        /// own: a gap said again before every entry that follows would be the
        /// complaint per entry, moved into the file.
        /// </remarks>
        [Test]
        public void AFileThatCannotBeWrittenIsSaidOnceAndItsGapWrittenDown()
        {

            var blocked  = Path.Combine(directory, "localcontroller-2026-09-23.log");
            var stderr   = new StringWriter();
            var previous = Console.Error;

            Directory.CreateDirectory(blocked);

            Console.SetError(stderr);

            try
            {

                using (new FileLog(log, directory, FilePrefix: "localcontroller"))
                {

                    log.Info("The first entry the disk refuses.",  "test");
                    log.Info("The second.",                        "test");
                    log.Info("The third.",                         "test");

                    Directory.Delete(blocked);

                    clock.Now = Afternoon.AddSeconds(5);
                    log.Info("The first entry after the disk came back.", "test");
                    log.Info("And the one after that.",                   "test");

                }

            }
            finally
            {
                Console.SetError(previous);
            }

            var said = stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

            Assert.Multiple(() => {

                Assert.That(said, Has.Length.EqualTo(2),
                            "The console was told something other than once that the file failed and once that it came back: " +
                            String.Join(" | ", said));

                Assert.That(said.ElementAtOrDefault(0), Does.Contain("could not be written"));
                Assert.That(said.ElementAtOrDefault(1), Does.Contain("is being written again; 3 entries are missing from it."));

                Assert.That(File.ReadAllLines(blocked),
                            Is.EqualTo(new[] {
                                "2026-09-23T13:45:06.123Z warning [log] 3 entries since 2026-09-23T13:45:01.123Z could not be written here.",
                                "2026-09-23T13:45:06.123Z info    [test] The first entry after the disk came back.",
                                "2026-09-23T13:45:06.123Z info    [test] And the one after that."
                            }));

            });

        }

        #endregion

        #region ADisposedLogWritesNoMore()

        /// <summary>
        /// Disposed means let go: nothing more is written, and the file is not
        /// held open any longer.
        /// </summary>
        [Test]
        public void ADisposedLogWritesNoMore()
        {

            var fileLog = new FileLog(log, directory, FilePrefix: "localcontroller");

            log.Info("Before.", "test");

            fileLog.Dispose();

            log.Info("After.",  "test");

            // FileShare.None, because that is refused while anybody else holds
            // the file - on every platform, where a delete would only prove it
            // on Windows.
            using var nobodyElse = new FileStream(fileLog.CurrentFile!, FileMode.Open, FileAccess.Read, FileShare.None);
            using var reader     = new StreamReader(nobodyElse);

            Assert.That(reader.ReadToEnd().TrimEnd(),
                        Is.EqualTo("2026-09-23T13:45:01.123Z info    [test] Before."));

        }

        #endregion


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
