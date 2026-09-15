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
    /// The event log on the console, for whoever started the process.
    /// </summary>
    /// <remarks>
    /// The same entries the web interface shows, so that a local controller without a
    /// browser in front of it is not silent - and so that the two never
    /// disagree about what happened.
    /// </remarks>
    public sealed class ConsoleLog : IDisposable
    {

        #region Data

        private readonly EventLog            log;
        private readonly Action<LogEntry>    handler;
        private readonly Lock                padlock = new();

        #endregion

        #region Properties

        /// <summary>
        /// Entries below this level are not written to the console. They are
        /// still kept in the log and still reach the web interface, where there
        /// is room for them.
        /// </summary>
        public LogLevel  MinimumLevel   { get; }

        /// <summary>
        /// Whether the level and the tags are coloured.
        /// </summary>
        public Boolean   Colours        { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Write the entries of the given log to the console.
        /// </summary>
        /// <param name="Log">The event log to follow.</param>
        /// <param name="MinimumLevel">Entries below this level stay off the console.</param>
        /// <param name="Colours">Whether to colour the level; off when the output is redirected.</param>
        public ConsoleLog(EventLog   Log,
                          LogLevel   MinimumLevel   = LogLevel.Info,
                          Boolean?   Colours        = null)
        {

            this.log           = Log;
            this.MinimumLevel  = MinimumLevel;
            this.Colours       = Colours ?? !Console.IsOutputRedirected;

            this.handler       = Write;

            log.OnLogged      += handler;

        }

        #endregion


        #region (private) Write(Entry)

        private void Write(LogEntry Entry)
        {

            if (Entry.Level < MinimumLevel)
                return;

            // The console is one device and the log is written from every
            // thread the local controller has; without this the colour of one entry
            // would end up on the text of another.
            lock (padlock)
            {

                if (!Colours)
                {
                    (Entry.Level >= LogLevel.Error ? Console.Error : Console.Out).WriteLine(Entry.ToString());
                    return;
                }

                var previous = Console.ForegroundColor;

                try
                {

                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(Entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"));
                    Console.Write(' ');

                    Console.ForegroundColor = ColourOf(Entry.Level);
                    Console.Write(Entry.LevelName.PadRight(8));

                    if (Entry.Tags.Count > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkCyan;
                        Console.Write(String.Join(" ", Entry.Tags));
                        Console.Write(' ');
                    }

                    Console.ForegroundColor = previous;
                    Console.WriteLine(Entry.Message);

                }
                finally
                {
                    Console.ForegroundColor = previous;
                }

            }

        }

        #endregion

        #region (private static) ColourOf(Level)

        private static ConsoleColor ColourOf(LogLevel Level)

            => Level switch {
                   LogLevel.Debug     => ConsoleColor.DarkGray,
                   LogLevel.Info      => ConsoleColor.Gray,
                   LogLevel.Notice    => ConsoleColor.Cyan,
                   LogLevel.Warning   => ConsoleColor.Yellow,
                   LogLevel.Error     => ConsoleColor.Red,
                   LogLevel.Critical  => ConsoleColor.Magenta,
                   _                  => ConsoleColor.Gray
               };

        #endregion

        #region Dispose()

        /// <summary>
        /// Stop writing to the console.
        /// </summary>
        public void Dispose()
        {
            log.OnLogged -= handler;
            GC.SuppressFinalize(this);
        }

        #endregion

    }

}
