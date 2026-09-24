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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;

#endregion

namespace cloud.charging.open.LocalController.Logging
{

    /// <summary>
    /// Everything that happens inside this local controller, in one place: the
    /// last few thousand entries in memory, and every new one handed on at once
    /// to whoever is listening - the console, and the Server-Sent Events stream
    /// the web interface hangs on.
    /// </summary>
    /// <remarks>
    /// One log rather than one per protocol, because the question somebody
    /// actually has in front of a local controller is "what happened just now",
    /// and the answer is an OCPP message next to the HTTP request that caused
    /// it next to the ISO 15118 session it is about. What keeps that readable
    /// is the tags: every entry says what it is about, and the web interface
    /// filters on them.
    ///
    /// The entries are numbered, and the number only ever grows. A browser
    /// loads a snapshot, which says how far it reaches, and then applies
    /// everything from the stream that is newer - so a reconnect that replays
    /// a few cached events costs bytes and nothing else.
    /// </remarks>
    public sealed class EventLog
    {

        #region Data

        /// <summary>
        /// How many entries are kept in memory by default: enough that a
        /// browser opening the page sees what led up to now.
        /// </summary>
        public const Int32 DefaultCapacity = 2_000;

        private readonly Queue<LogEntry>   entries = new();
        private readonly SortedSet<String> tags    = new(StringComparer.Ordinal);
        private readonly Lock              padlock = new();

        private UInt64 lastId;

        #endregion

        #region Properties

        /// <summary>
        /// How many entries are kept in memory.
        /// </summary>
        public Int32         Capacity      { get; }

        /// <summary>
        /// Where the timestamp of an entry comes from.
        /// </summary>
        /// <remarks>
        /// Handed in rather than reached for: a log whose times come from
        /// somewhere else than the rest of the local controller is a log that cannot be
        /// held against anything - and a test that cannot move the clock can
        /// only ever watch the log say "now".
        /// </remarks>
        public TimeProvider  TimeProvider  { get; }

        /// <summary>
        /// What puts a complaint of this log about itself on the console as one
        /// piece - see <see cref="Complain"/>. By default it just writes it.
        /// </summary>
        /// <remarks>
        /// Settable for the reason the console log's WriteBlock is: once a
        /// command line is being typed on the same console, whatever is written
        /// there has to go around that line, and only whoever draws the line
        /// knows how. The local controller puts the same block here as on its
        /// console log, so that a complaint lands neither in the middle of an
        /// entry nor past a command somebody is typing.
        /// </remarks>
        public Action<Action>  ComplaintBlock  { get; set; } = write => write();

        /// <summary>
        /// The number of the newest entry; 0 when nothing has been logged yet.
        /// </summary>
        public UInt64  LastId
        {
            get
            {
                lock (padlock)
                    return lastId;
            }
        }

        /// <summary>
        /// How many entries are in memory right now.
        /// </summary>
        public Int32   Count
        {
            get
            {
                lock (padlock)
                    return entries.Count;
            }
        }

        /// <summary>
        /// Every tag that has been seen since the start, so that the web
        /// interface can offer them instead of asking somebody to guess.
        /// </summary>
        public IEnumerable<String> KnownTags
        {
            get
            {
                lock (padlock)
                    return [.. tags];
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Sent for every new entry, while the caller of <see cref="Log"/>
        /// waits - so a listener has to be quick and must not throw. One that
        /// does throw is caught here and complained about: a broken listener
        /// must not swallow the event that was being logged.
        /// </summary>
        public event Action<LogEntry>? OnLogged;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create an event log keeping the given number of entries.
        /// </summary>
        /// <param name="Capacity">How many entries are kept in memory.</param>
        /// <param name="TimeProvider">Where the timestamp of an entry comes from; the system clock by default.</param>
        public EventLog(Int32          Capacity       = DefaultCapacity,
                        TimeProvider?  TimeProvider   = null)
        {

            if (Capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(Capacity), "An event log must be able to keep at least one entry!");

            this.Capacity      = Capacity;
            this.TimeProvider  = TimeProvider ?? System.TimeProvider.System;

        }

        #endregion


        #region Log(Level, Message, params Tags)

        /// <summary>
        /// Write one entry.
        /// </summary>
        /// <param name="Level">How much attention it wants.</param>
        /// <param name="Message">One line, as somebody would read it.</param>
        /// <param name="Tags">What it is about: "ocpp", "15118", "http", ...</param>
        public LogEntry Log(LogLevel         Level,
                            String           Message,
                            params String[]  Tags)

            => Log(Level, Message, null, Tags);

        #endregion

        #region Log(Level, Message, Data, params Tags)

        /// <summary>
        /// Write one entry with the whole of what it is about attached.
        /// </summary>
        /// <param name="Level">How much attention it wants.</param>
        /// <param name="Message">One line, as somebody would read it.</param>
        /// <param name="Data">Whatever else belongs to it, or null.</param>
        /// <param name="Tags">What it is about: "ocpp", "15118", "http", ...</param>
        public LogEntry Log(LogLevel         Level,
                            String           Message,
                            JObject?         Data,
                            params String[]  Tags)
        {

            var entry = default(LogEntry);

            lock (padlock)
            {

                entry = new LogEntry(
                            ++lastId,
                            TimeProvider.GetUtcNow(),
                            Level,
                            Normalize(Tags),
                            Message?.Trim() ?? "",
                            Data
                        );

                entries.Enqueue(entry);

                while (entries.Count > Capacity)
                    entries.Dequeue();

                foreach (var tag in entry.Tags)
                    tags.Add(tag);

            }

            // Outside the lock: a listener writing to the console or handing
            // the entry to a browser must not hold up whoever is logging - and
            // certainly must not be able to deadlock them.
            var handler = OnLogged;

            if (handler is not null)
            {
                foreach (var listener in handler.GetInvocationList().Cast<Action<LogEntry>>())
                {
                    try
                    {
                        listener(entry);
                    }
                    catch (Exception e)
                    {
                        // Beside the log and not through it - see Complain.
                        Complain($"An event log listener failed: {e.Message}");
                    }
                }
            }

            return entry;

        }

        #endregion

        #region Debug/Info/Notice/Warning/Error/Critical(Message, params Tags)

        /// <summary>The detail one only wants while looking for something.</summary>
        public LogEntry Debug   (String Message, params String[] Tags) => Log(LogLevel.Debug,    Message, Tags);

        /// <summary>What happened, in the ordinary course of things.</summary>
        public LogEntry Info    (String Message, params String[] Tags) => Log(LogLevel.Info,     Message, Tags);

        /// <summary>Ordinary, but worth finding again later.</summary>
        public LogEntry Notice  (String Message, params String[] Tags) => Log(LogLevel.Notice,   Message, Tags);

        /// <summary>Something is not as it should be, but the local controller carries on.</summary>
        public LogEntry Warning (String Message, params String[] Tags) => Log(LogLevel.Warning,  Message, Tags);

        /// <summary>Something did not work.</summary>
        public LogEntry Error   (String Message, params String[] Tags) => Log(LogLevel.Error,    Message, Tags);

        /// <summary>Something did not work and will not start working by itself.</summary>
        public LogEntry Critical(String Message, params String[] Tags) => Log(LogLevel.Critical, Message, Tags);

        #endregion

        #region Exception(Exception, Message, params Tags)

        /// <summary>
        /// Write an error with the exception behind it attached.
        /// </summary>
        public LogEntry Exception(Exception        Exception,
                                  String           Message,
                                  params String[]  Tags)

            => Log(
                   LogLevel.Error,
                   $"{Message}: {Exception.Message}",
                   new JObject(
                       new JProperty("exception",   Exception.GetType().FullName),
                       new JProperty("message",     Exception.Message),
                       new JProperty("stackTrace",  Exception.StackTrace)
                   ),
                   Tags
               );

        #endregion


        #region Complain(Complaint)

        /// <summary>
        /// Say on stderr what is wrong with this log itself: a listener that
        /// failed, a file that cannot be written.
        /// </summary>
        /// <remarks>
        /// On stderr and not through the log, because a complaint about the log
        /// that went through the log would come back to whatever failed and fail
        /// again - a fine way to spend an afternoon in a loop. And through the
        /// <see cref="ComplaintBlock"/>, because the console may have a command
        /// line on it that somebody is typing.
        ///
        /// Never with an exception, since a complaint is made where something
        /// has gone wrong already - and often what went wrong is the console: a
        /// listener fails because the command line could not draw itself, and
        /// then the block fails as well. The complaint is written without it
        /// rather than not at all, and not a second time when the block got as
        /// far as writing it. One that cannot be written anywhere is dropped;
        /// there is nobody left to tell.
        /// </remarks>
        /// <param name="Complaint">One line, as somebody would read it.</param>
        public void Complain(String Complaint)
        {

            var written = false;

            try
            {
                ComplaintBlock(() => {
                    Console.Error.WriteLine(Complaint);
                    written = true;
                });
            }
            catch
            {

                if (!written)
                {
                    try
                    {
                        Console.Error.WriteLine(Complaint);
                    }
                    catch
                    { }
                }

            }

        }

        #endregion

        #region Recent(Limit, After = null, Tag = null)

        /// <summary>
        /// The newest entries, oldest first - what a browser loads before it
        /// starts following the stream.
        /// </summary>
        /// <param name="Limit">At most this many entries.</param>
        /// <param name="After">Only entries newer than this number.</param>
        /// <param name="Tag">Only entries carrying this tag or level.</param>
        public IEnumerable<LogEntry> Recent(Int32    Limit,
                                            UInt64?  After   = null,
                                            String?  Tag     = null)
        {

            if (Limit < 1)
                return [];

            LogEntry[] snapshot;

            lock (padlock)
                snapshot = [.. entries];

            var matching = snapshot.AsEnumerable();

            if (After.HasValue)
                matching = matching.Where(entry => entry.Id > After.Value);

            if (!String.IsNullOrWhiteSpace(Tag))
                matching = matching.Where(entry => entry.Matches(Tag));

            // Counted from the end, because the interesting end of a log is the
            // new one; the order within the page stays oldest first, which is
            // how a log reads and how the browser appends it.
            return [.. matching.TakeLast(Limit)];

        }

        #endregion


        #region (private static) Normalize(Tags)

        /// <summary>
        /// Tags as they are stored: lower case, trimmed, without empties and
        /// without repeats, in the order they were given.
        /// </summary>
        private static IReadOnlyList<String> Normalize(String[] Tags)
        {

            if (Tags.Length == 0)
                return [];

            var normalized = new List<String>(Tags.Length);

            foreach (var tag in Tags)
            {

                if (String.IsNullOrWhiteSpace(tag))
                    continue;

                var lower = tag.Trim().ToLowerInvariant();

                if (!normalized.Contains(lower))
                    normalized.Add(lower);

            }

            return normalized;

        }

        #endregion

    }

}
