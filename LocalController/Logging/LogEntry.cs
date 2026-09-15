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

#endregion

namespace cloud.charging.open.LocalController.Logging
{

    #region LogLevel

    /// <summary>
    /// How much a log entry wants somebody's attention.
    /// </summary>
    /// <remarks>
    /// One of these is always the first tag of an entry, so that the web
    /// interface can filter by "critical" the same way it filters by "ocpp".
    /// </remarks>
    public enum LogLevel
    {

        /// <summary>
        /// The detail one only wants while looking for something.
        /// </summary>
        Debug,

        /// <summary>
        /// What happened, in the ordinary course of things.
        /// </summary>
        Info,

        /// <summary>
        /// Ordinary, but worth finding again later: a start, a sign-in.
        /// </summary>
        Notice,

        /// <summary>
        /// Something is not as it should be, but the local controller carries on.
        /// </summary>
        Warning,

        /// <summary>
        /// Something did not work.
        /// </summary>
        Error,

        /// <summary>
        /// Something did not work and will not start working by itself.
        /// </summary>
        Critical

    }

    #endregion


    /// <summary>
    /// One thing that happened inside this local controller: when, how loudly,
    /// what it was about, and what it was.
    /// </summary>
    /// <param name="Id">A number that only ever grows, so a browser can tell what it has already seen.</param>
    /// <param name="Timestamp">When it happened.</param>
    /// <param name="Level">How much attention it wants.</param>
    /// <param name="Tags">What it is about: "ocpp", "http", "dns", ... - lower case, without the level.</param>
    /// <param name="Message">One line, as somebody would read it.</param>
    /// <param name="Data">Whatever else belongs to it, or null.</param>
    public sealed record LogEntry(UInt64                 Id,
                                  DateTimeOffset         Timestamp,
                                  LogLevel               Level,
                                  IReadOnlyList<String>  Tags,
                                  String                 Message,
                                  JObject?               Data   = null)
    {

        #region Properties

        /// <summary>
        /// The level as it is written in JSON and shown in the browser: "info",
        /// "critical", ...
        /// </summary>
        public String  LevelName
            => Level.ToString().ToLowerInvariant();

        #endregion


        #region Matches(Tag)

        /// <summary>
        /// Whether this entry carries the given tag - the level counting as one
        /// of them, which is how "critical" and "ocpp" can be typed into the
        /// same filter box.
        /// </summary>
        public Boolean Matches(String Tag)
        {

            var tag = Tag.Trim().ToLowerInvariant();

            if (tag.Length == 0)
                return true;

            if (LevelName == tag)
                return true;

            foreach (var own in Tags)
            {
                if (own == tag)
                    return true;
            }

            return false;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The entry as the browser reads it.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject(
                           new JProperty("id",         Id),
                           new JProperty("timestamp",  Timestamp.ToString("o")),
                           new JProperty("level",      LevelName),
                           new JProperty("tags",       new JArray(Tags)),
                           new JProperty("message",    Message)
                       );

            if (Data is not null)
                json.Add("data", Data);

            return json;

        }

        #endregion

        #region (override) ToString()

        /// <summary>
        /// The entry as it is written to the console.
        /// </summary>
        public override String ToString()

            => String.Concat(
                   Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
                   " [", LevelName, Tags.Count > 0 ? " " + String.Join(" ", Tags) : "", "] ",
                   Message
               );

        #endregion

    }

}
