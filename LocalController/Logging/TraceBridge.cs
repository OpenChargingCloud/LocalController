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

using System.Diagnostics;
using System.Text;

#endregion

namespace cloud.charging.open.LocalController.Logging
{

    /// <summary>
    /// Everything the libraries below this local controller write with Illias' DebugX,
    /// into the event log - and from there onto the web interface.
    /// </summary>
    /// <remarks>
    /// DebugX writes to <see cref="Debug"/>, and on .NET that reaches
    /// <see cref="Trace.Listeners"/>. So OCPP, ISO 15118, Hermod and the rest
    /// need not know this log exists to end up in it: attaching one listener is
    /// enough.
    ///
    /// <b>Only in a debug build.</b> <c>Debug.WriteLine</c> carries
    /// <c>[Conditional("DEBUG")]</c>, so in a release build of those libraries
    /// the calls are not compiled in at all and nothing arrives here. What a
    /// release build shows on the Logs page is what this local controller logs itself.
    ///
    /// The tags are guessed from the text, by the table in
    /// <see cref="TagsFor"/> - a short, deliberate list of the protocol names
    /// that matter here, and not an attempt to understand the line. A name
    /// counts where a word of its own could begin, not inside a word written in
    /// lower case; see <see cref="Mentions"/>. A line nothing matches is tagged
    /// "trace" alone, which is also how it can be filtered away. What a local controller overhears is mostly OCPP going
    /// past it in both directions, so the table leans that way: which side a
    /// line is about - the CSMS above or a charging station below - is worth
    /// more here than which layer it came from.
    /// </remarks>
    public sealed class TraceBridge : TraceListener
    {

        #region Data

        /// <summary>
        /// The tag every bridged line carries, so that the web interface can
        /// tell what the local controller said itself from what it overheard.
        /// </summary>
        public const String TraceTag = "trace";

        /// <summary>
        /// What a line has to contain for a tag to be added to it. Ordered, and
        /// searched case-insensitively; a line may collect several.
        /// </summary>
        private static readonly (String Needle, String Tag)[] tagTable = [
            ("ocpp",           "ocpp"),
            ("bootnotification", "ocpp"),
            ("heartbeat",      "ocpp"),
            ("websocket",      "websocket"),
            ("http",           "http"),
            ("tls",            "tls"),
            ("certificate",    "tls"),
            ("dns",            "dns"),
            ("nts",            "nts"),
            ("ntp",            "nts"),
            ("csms",           "csms"),
            ("charging station", "station"),
            ("chargingstation",  "station"),
            ("chargebox",      "station"),
            ("routing",        "routing"),
            ("forward",        "routing")
        ];

        /// <summary>
        /// What a line has to contain to be logged louder than Debug.
        /// </summary>
        private static readonly (String Needle, LogLevel Level)[] levelTable = [
            ("exception",   LogLevel.Error),
            ("failed",      LogLevel.Error),
            ("error",       LogLevel.Error),
            ("could not",   LogLevel.Warning),
            ("timeout",     LogLevel.Warning),
            ("timed out",   LogLevel.Warning),
            ("warning",     LogLevel.Warning),
            ("refused",     LogLevel.Warning),
            ("rejected",    LogLevel.Warning)
        ];

        private readonly EventLog       log;
        private readonly StringBuilder  pending = new();
        private readonly Lock           padlock = new();

        #endregion

        #region Constructor(s)

        /// <summary>
        /// A listener writing into the given event log. Attach it with
        /// <see cref="Attach"/> rather than by hand, so that it is registered
        /// once.
        /// </summary>
        private TraceBridge(EventLog Log)
        {
            this.log = Log;
        }

        #endregion


        #region (static) Attach(Log)

        /// <summary>
        /// Send everything written through DebugX into the given event log,
        /// from now until the returned listener is disposed.
        /// </summary>
        public static TraceBridge Attach(EventLog Log)
        {

            var bridge = new TraceBridge(Log);

            Trace.Listeners.Add(bridge);

            return bridge;

        }

        #endregion

        #region Write(Message) / WriteLine(Message)

        /// <summary>
        /// Part of a line; it is held until the line ends.
        /// </summary>
        public override void Write(String? Message)
        {

            if (Message is null)
                return;

            lock (padlock)
                pending.Append(Message);

        }

        /// <summary>
        /// The end of a line: whatever was held, plus this, becomes one entry.
        /// </summary>
        public override void WriteLine(String? Message)
        {

            String line;

            lock (padlock)
            {
                pending.Append(Message);
                line = pending.ToString();
                pending.Clear();
            }

            Emit(line);

        }

        #endregion

        #region (protected override) Dispose(Disposing)

        /// <summary>
        /// Stop listening, and write out whatever line was half-finished.
        /// </summary>
        protected override void Dispose(Boolean Disposing)
        {

            if (Disposing)
            {

                Trace.Listeners.Remove(this);

                String rest;

                lock (padlock)
                {
                    rest = pending.ToString();
                    pending.Clear();
                }

                if (rest.Length > 0)
                    Emit(rest);

            }

            base.Dispose(Disposing);

        }

        #endregion


        #region (private) Emit(Line)

        private void Emit(String Line)
        {

            var text = Line.TrimEnd('\r', '\n');

            if (text.Trim().Length == 0)
                return;

            // DebugX puts "[dd.MM.yyyy HH:mm:ss zzz] " in front of every line;
            // the log has a timestamp of its own, and two of them side by side
            // in a narrow column help nobody.
            if (text.StartsWith('[') && text.IndexOf("] ", StringComparison.Ordinal) is var end && end > 0 && end < 40)
                text = text[(end + 2)..];

            log.Log(
                LevelFor(text),
                text,
                TagsFor(text)
            );

        }

        #endregion

        #region (private static) TagsFor(Line) / Mentions(Line, Needle) / LevelFor(Line)

        /// <summary>
        /// What a bridged line is about, as far as its text gives it away.
        /// </summary>
        private static String[] TagsFor(String Line)
        {

            var tags = new List<String> { TraceTag };

            foreach (var (needle, tag) in tagTable)
            {
                if (Mentions(Line, needle) &&
                    !tags.Contains(tag))
                {
                    tags.Add(tag);
                }
            }

            return [.. tags];

        }

        /// <summary>
        /// Whether the line names the needle where a word of its own could
        /// begin, rather than inside another word.
        /// </summary>
        /// <remarks>
        /// Found anywhere, "nts" tagged every line that said "accounts",
        /// "clients" or "events" as one about the time servers - the first
        /// start's complaint about the account store among them. So a place
        /// inside a word written in lower case does not count: the character
        /// before it is a lower-case letter and so is its own first one.
        /// Everything else still does - the start of the line, after a digit or
        /// a sign, and at a capital, which is where "mDNS" and
        /// "OCPPWebSocketServer" begin the words that matter. What follows the
        /// name is not looked at, so that "OCPPv2.1", "https" and "certificates"
        /// count as they did.
        /// </remarks>
        private static Boolean Mentions(String Line, String Needle)
        {

            for (var at = Line.IndexOf(Needle, StringComparison.OrdinalIgnoreCase);
                 at >= 0;
                 at = Line.IndexOf(Needle, at + 1, StringComparison.OrdinalIgnoreCase))
            {

                if (at == 0 || !Char.IsLower(Line[at - 1]) || !Char.IsLower(Line[at]))
                    return true;

            }

            return false;

        }

        /// <summary>
        /// How loudly a bridged line asks to be read. DebugX knows no levels,
        /// so a line that says it is about a failure is taken at its word.
        /// </summary>
        private static LogLevel LevelFor(String Line)
        {

            foreach (var (needle, level) in levelTable)
            {
                if (Line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return level;
            }

            return LogLevel.Debug;

        }

        #endregion

    }

}
