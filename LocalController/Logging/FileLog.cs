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

namespace cloud.charging.open.LocalController.Logging
{

    /// <summary>
    /// The event log on disk, for after the fact.
    /// </summary>
    /// <remarks>
    /// The console shows what is happening to whoever is watching, and the web
    /// interface keeps the last two thousand entries for whoever asks. Both are
    /// gone when the process is: a console that was not being read kept
    /// nothing, and the ring buffer empties with the controller. Everything
    /// that wants answering afterwards - what a charging station sent at four
    /// in the morning, what the clock did last week, what happened just before
    /// a session failed - needs a third place, and this is it.
    ///
    /// One file per day, named for the date, appended to and flushed after
    /// every entry. Flushing every time costs a system call per entry and buys
    /// the property that matters here: a controller that is killed, or that
    /// crashes, has its last lines on disk rather than in a buffer.
    ///
    /// Nothing is ever deleted. A controller that quietly threw away the
    /// evidence of the night somebody is asking about would be worse than one
    /// that needs a directory emptied now and then.
    ///
    /// A file that cannot be written does not take the controller down, and
    /// does not bury the console under one complaint per entry either. It is
    /// said once on stderr, every following entry is tried again, and the
    /// first one that makes it is preceded in the file by a line saying how
    /// many are missing and since when. A local controller runs for weeks, so
    /// a disk that was full for an hour must not cost the rest of them - and a
    /// gap the file admits to is one somebody can reason about, where a silent
    /// one is only found by the person who needed what was in it.
    ///
    /// The charging station's, which is the vehicle's in its final form.
    /// </remarks>
    public sealed class FileLog : IDisposable
    {

        #region Data

        private readonly EventLog          log;
        private readonly Action<LogEntry>  handler;
        private readonly Lock              padlock = new();

        /// <summary>
        /// The day the open file belongs to, so that midnight is noticed
        /// without asking the file system anything.
        /// </summary>
        private DateOnly         openFor;
        private StreamWriter?    writer;

        /// <summary>
        /// Since when entries have not made it into a file, and how many, while
        /// writing fails; null and zero while it works.
        /// </summary>
        private DateTimeOffset?  failingSince;
        private UInt64           missed;

        #endregion

        #region Properties

        /// <summary>
        /// The directory the files are written to.
        /// </summary>
        public String    Directory      { get; }

        /// <summary>
        /// Entries below this level are not written. Debug by default, which is
        /// everything: the console is where a level is chosen for readability,
        /// and a file nobody is reading has no such problem.
        /// </summary>
        public LogLevel  MinimumLevel   { get; }

        /// <summary>
        /// The file being written at the moment, or null before the first entry.
        /// </summary>
        public String?   CurrentFile    { get; private set; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Write the entries of the given log to a file per day below the given
        /// directory.
        /// </summary>
        /// <param name="Log">The event log to follow.</param>
        /// <param name="Directory">Where the files go. Made if it is not there.</param>
        /// <param name="MinimumLevel">Entries below this level are not written.</param>
        public FileLog(EventLog  Log,
                       String    Directory,
                       LogLevel  MinimumLevel   = LogLevel.Debug)
        {

            this.log           = Log;
            this.Directory     = Path.GetFullPath(Directory);
            this.MinimumLevel  = MinimumLevel;

            System.IO.Directory.CreateDirectory(this.Directory);

            this.handler       = Write;

            log.OnLogged      += handler;

        }

        #endregion


        #region FileFor(Day)

        /// <summary>
        /// The file the entries of the given day go to.
        /// </summary>
        /// <remarks>
        /// The day is a UTC day, as the timestamps in the file are: a log that
        /// changed files at local midnight would put 01:30 on a summer night
        /// into a file named for the day before its own first line.
        /// </remarks>
        public String FileFor(DateOnly Day)

            => Path.Combine(Directory, $"localcontroller-{Day:yyyy-MM-dd}.log");

        #endregion


        #region (private) Write(Entry)

        private void Write(LogEntry Entry)
        {

            if (Entry.Level < MinimumLevel)
                return;

            lock (padlock)
            {

                try
                {

                    var day = DateOnly.FromDateTime(Entry.Timestamp.UtcDateTime);

                    if (writer is null || day != openFor)
                    {

                        Close();

                        openFor      = day;
                        CurrentFile  = FileFor(day);
                        writer       = new StreamWriter(CurrentFile, append: true);

                    }

                    // The gap first, where it happened, and as an entry of its
                    // own - so that somebody reading from the top meets it
                    // exactly where the entries stop making sense, and a tool
                    // reading the file meets nothing it cannot parse.
                    if (failingSince is DateTimeOffset since)
                        WriteEntry(writer, new LogEntry(
                                               0,
                                               Entry.Timestamp,
                                               LogLevel.Warning,
                                               [ "log" ],
                                               $"{missed} entr{(missed == 1 ? "y" : "ies")} since {Stamp(since)} could not be written here."
                                           ));

                    WriteEntry(writer, Entry);

                    // Every entry, not every buffer: see the remarks above.
                    writer.Flush();

                    if (failingSince is not null)
                    {

                        log.Complain($"The log file in '{Directory}' is being written again; " +
                                     $"{missed} entr{(missed == 1 ? "y is" : "ies are")} missing from it.");

                        failingSince  = null;
                        missed        = 0;

                    }

                }
                catch (Exception e)
                {

                    // Once, and on stderr rather than through the log - going
                    // through the log would come back here and fail again. By
                    // way of its Complain, which keeps it off a command line
                    // somebody is typing.
                    if (failingSince is null)
                    {

                        log.Complain($"The log file in '{Directory}' could not be written: {e.Message} " +
                                     "Every following entry is tried again, and the file will say what it missed.");

                        failingSince = Entry.Timestamp;

                    }

                    missed++;

                    Close();

                }

            }

        }

        #endregion

        #region (private static) WriteEntry(Writer, Entry)

        /// <summary>
        /// One entry, on one line.
        /// </summary>
        private static void WriteEntry(StreamWriter  Writer,
                                       LogEntry      Entry)
        {

            Writer.Write    (Stamp(Entry.Timestamp));
            Writer.Write    (' ');
            Writer.Write    (Entry.LevelName.PadRight(8));

            if (Entry.Tags.Count > 0)
            {
                Writer.Write('[');
                Writer.Write(String.Join(" ", Entry.Tags));
                Writer.Write("] ");
            }

            Writer.WriteLine(Entry.Message);

        }

        #endregion

        #region (private static) Stamp(Timestamp)

        /// <summary>
        /// The timestamp in full and in UTC, unlike the console's local time of
        /// day: a file outlives the session that wrote it, and is read in
        /// another time zone often enough.
        /// </summary>
        private static String Stamp(DateTimeOffset Timestamp)

            => Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        #endregion

        #region (private) Close()

        /// <summary>
        /// Let go of the open file, whatever state it is in.
        /// </summary>
        /// <remarks>
        /// Disposing a writer flushes it first, and on a full disk that throws
        /// the very error that brought us here a second time. What is lost by
        /// swallowing it is the same buffer that was lost already.
        /// </remarks>
        private void Close()
        {

            try
            {
                writer?.Dispose();
            }
            catch
            { }

            writer = null;

        }

        #endregion

        #region Dispose()

        /// <summary>
        /// Stop writing and close the file.
        /// </summary>
        public void Dispose()
        {

            log.OnLogged -= handler;

            lock (padlock)
            {
                Close();
            }

            GC.SuppressFinalize(this);

        }

        #endregion

    }

}
