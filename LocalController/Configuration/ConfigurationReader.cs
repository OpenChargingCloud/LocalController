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

using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace cloud.charging.open.LocalController.Configuration
{

    /// <summary>
    /// Reading optional fields out of a configuration section.
    /// </summary>
    /// <remarks>
    /// Three rules, and they are the same for every field this local controller reads
    /// from a file or from the web interface:
    ///
    /// Absent is not an error, and comes back as null - the caller then leaves
    /// whatever it had. Present but of the wrong kind is an error, and never a
    /// silent null: somebody wrote <c>"useCache": "yes"</c> and deserves to be
    /// told, rather than watching the setting quietly not happen. And an
    /// explicit JSON null counts as absent, so that a page may send a whole
    /// object with the fields it does not touch left empty.
    ///
    /// Every message names the field by its path in the document, because a
    /// browser showing "must be a whole number" over a form with nine numbers
    /// in it has said nothing.
    /// </remarks>
    public static class ConfigurationReader
    {

        #region TryReadBoolean(JSON, Name, Path, out Value, out Error)

        /// <summary>
        /// An optional true or false.
        /// </summary>
        public static Boolean TryReadBoolean(JObject                           JSON,
                                             String                            Name,
                                             String?                           Path,
                                             out Boolean?                      Value,
                                             [NotNullWhen(false)] out String?  Error)
        {

            Value  = null;
            Error  = null;

            if (!JSON.TryGetValue(Name, out var token) || token.Type == JTokenType.Null)
                return true;

            if (token.Type != JTokenType.Boolean)
            {
                Error = $"'{Where(Path, Name)}' must be true or false.";
                return false;
            }

            Value = token.Value<Boolean>();
            return true;

        }

        #endregion

        #region TryReadByte   (JSON, Name, Path, out Value, out Error)

        /// <summary>
        /// An optional count between 0 and 255.
        /// </summary>
        public static Boolean TryReadByte(JObject                           JSON,
                                          String                            Name,
                                          String?                           Path,
                                          out Byte?                         Value,
                                          [NotNullWhen(false)] out String?  Error)
        {

            Value  = null;
            Error  = null;

            if (!JSON.TryGetValue(Name, out var token) || token.Type == JTokenType.Null)
                return true;

            if (token.Type != JTokenType.Integer)
            {
                Error = $"'{Where(Path, Name)}' must be a whole number.";
                return false;
            }

            var number = token.Value<Int64>();

            if (number < 0 || number > Byte.MaxValue)
            {
                Error = $"'{Where(Path, Name)}' must be between 0 and {Byte.MaxValue}.";
                return false;
            }

            Value = (Byte) number;
            return true;

        }

        #endregion

        #region TryReadSeconds(JSON, Name, Path, Minimum, Maximum, out Value, out Error)

        /// <summary>
        /// An optional number of seconds, as a time span.
        /// </summary>
        public static Boolean TryReadSeconds(JObject                           JSON,
                                             String                            Name,
                                             String?                           Path,
                                             Double                            Minimum,
                                             Double                            Maximum,
                                             out TimeSpan?                     Value,
                                             [NotNullWhen(false)] out String?  Error)
        {

            Value  = null;
            Error  = null;

            if (!JSON.TryGetValue(Name, out var token) || token.Type == JTokenType.Null)
                return true;

            if (token.Type is not (JTokenType.Integer or JTokenType.Float))
            {
                Error = $"'{Where(Path, Name)}' must be a number of seconds.";
                return false;
            }

            var seconds = token.Value<Double>();

            if (seconds < Minimum || seconds > Maximum)
            {
                Error = $"'{Where(Path, Name)}' must be between {Minimum} and {Maximum} seconds.";
                return false;
            }

            Value = TimeSpan.FromSeconds(seconds);
            return true;

        }

        #endregion

        #region TryReadPort   (JSON, Name, Path, out Value, out Error)

        /// <summary>
        /// An optional TCP or UDP port.
        /// </summary>
        /// <remarks>
        /// Port 0 is refused although it is a number in range: it means "any
        /// free port" to a listener and nothing at all to a client, and a
        /// client is what every port in this file belongs to.
        /// </remarks>
        public static Boolean TryReadPort(JObject                           JSON,
                                          String                            Name,
                                          String?                           Path,
                                          out IPPort?                       Value,
                                          [NotNullWhen(false)] out String?  Error)
        {

            Value  = null;
            Error  = null;

            if (!JSON.TryGetValue(Name, out var token) || token.Type == JTokenType.Null)
                return true;

            if (token.Type != JTokenType.Integer)
            {
                Error = $"'{Where(Path, Name)}' must be a port number.";
                return false;
            }

            var number = token.Value<Int64>();

            if (number < 1 || number > UInt16.MaxValue)
            {
                Error = $"'{Where(Path, Name)}' must be a port number between 1 and {UInt16.MaxValue}.";
                return false;
            }

            Value = IPPort.Parse((UInt16) number);
            return true;

        }

        #endregion

        #region TryReadUInt32 (JSON, Name, Path, Minimum, Maximum, out Value, out Error)

        /// <summary>
        /// An optional whole number within a range.
        /// </summary>
        public static Boolean TryReadUInt32(JObject                           JSON,
                                            String                            Name,
                                            String?                           Path,
                                            UInt32                            Minimum,
                                            UInt32                            Maximum,
                                            out UInt32?                       Value,
                                            [NotNullWhen(false)] out String?  Error)
        {

            Value  = null;
            Error  = null;

            if (!JSON.TryGetValue(Name, out var token) || token.Type == JTokenType.Null)
                return true;

            if (token.Type != JTokenType.Integer)
            {
                Error = $"'{Where(Path, Name)}' must be a whole number.";
                return false;
            }

            var number = token.Value<Int64>();

            if (number < Minimum || number > Maximum)
            {
                Error = $"'{Where(Path, Name)}' must be between {Minimum} and {Maximum}.";
                return false;
            }

            Value = (UInt32) number;
            return true;

        }

        #endregion

        #region TryReadStrings(JSON, Name, Path, MaxItems, MaxLength, out Value, out Error)

        /// <summary>
        /// An optional list of pieces of text, each trimmed, with the blank
        /// ones left out.
        /// </summary>
        /// <remarks>
        /// An empty list is a list, and not the same as a missing one: a form
        /// that offers a list and was emptied says "none of them", while a form
        /// that never mentioned it says nothing at all. Whether "none" is an
        /// acceptable answer is for the section to decide, not for this.
        /// </remarks>
        public static Boolean TryReadStrings(JObject                           JSON,
                                             String                            Name,
                                             String?                           Path,
                                             Int32                             MaxItems,
                                             Int32                             MaxLength,
                                             out IReadOnlyList<String>?        Value,
                                             [NotNullWhen(false)] out String?  Error)
        {

            Value  = null;
            Error  = null;

            if (!JSON.TryGetValue(Name, out var token) || token.Type == JTokenType.Null)
                return true;

            if (token is not JArray array)
            {
                Error = $"'{Where(Path, Name)}' must be an array.";
                return false;
            }

            if (array.Count > MaxItems)
            {
                Error = $"'{Where(Path, Name)}' may have at most {MaxItems} entries.";
                return false;
            }

            var items = new List<String>();

            foreach (var item in array)
            {

                if (item.Type != JTokenType.String)
                {
                    Error = $"'{Where(Path, Name)}' must contain nothing but strings.";
                    return false;
                }

                var text = item.Value<String>()?.Trim() ?? "";

                if (text.Length == 0)
                    continue;

                if (text.Length > MaxLength)
                {
                    Error = $"'{Where(Path, Name)}' may contain nothing longer than {MaxLength} characters.";
                    return false;
                }

                items.Add(text);

            }

            Value = items;
            return true;

        }

        #endregion

        #region TryReadTimestamp(JSON, Name, Path, out Value, out Error)

        /// <summary>
        /// An optional moment in time, written the one way a moment should be
        /// written down: ISO 8601 with its offset.
        /// </summary>
        public static Boolean TryReadTimestamp(JObject                           JSON,
                                               String                            Name,
                                               String?                           Path,
                                               out DateTimeOffset?               Value,
                                               [NotNullWhen(false)] out String?  Error)
        {

            Value  = null;
            Error  = null;

            if (!JSON.TryGetValue(Name, out var token) || token.Type == JTokenType.Null)
                return true;

            // Newtonsoft parses what looks like a date into a date of its own
            // accord, so both a string and an already-parsed date arrive here.
            //
            // And what it parsed it into is a DateTime and not a
            // DateTimeOffset - reading it as the latter throws rather than
            // converting. Which of the two turns up depends on whether the
            // document was parsed from text or built in memory, so a value that
            // works when a test hands it over directly fails when it arrives
            // through a request body.
            if (token.Type == JTokenType.Date)
            {

                Value = (token as JValue)?.Value switch {
                            DateTimeOffset offset    => offset,
                            DateTime       dateTime  => new DateTimeOffset(dateTime.ToUniversalTime(), TimeSpan.Zero),
                            _                        => null
                        };

                if (!Value.HasValue)
                {
                    Error = $"'{Where(Path, Name)}' must be a timestamp, e.g. \"2026-09-16T10:30:00Z\".";
                    return false;
                }

                return true;

            }

            if (token.Type != JTokenType.String)
            {
                Error = $"'{Where(Path, Name)}' must be a timestamp, e.g. \"2026-09-16T10:30:00Z\".";
                return false;
            }

            var text = token.Value<String>()?.Trim() ?? "";

            if (text.Length == 0)
                return true;

            if (!DateTimeOffset.TryParse(text,
                                         System.Globalization.CultureInfo.InvariantCulture,
                                         System.Globalization.DateTimeStyles.RoundtripKind,
                                         out var timestamp))
            {
                Error = $"'{Where(Path, Name)}' must be a timestamp, e.g. \"2026-09-16T10:30:00Z\".";
                return false;
            }

            Value = timestamp;
            return true;

        }

        #endregion

        #region TryReadString (JSON, Name, Path, MaxLength, out Value, out Error)

        /// <summary>
        /// An optional piece of text, trimmed, with a blank one counting as
        /// absent - a field somebody cleared in a form is a field they left
        /// empty, not a setting of the empty string.
        /// </summary>
        public static Boolean TryReadString(JObject                           JSON,
                                            String                            Name,
                                            String?                           Path,
                                            Int32                             MaxLength,
                                            out String?                       Value,
                                            [NotNullWhen(false)] out String?  Error)
        {

            Value  = null;
            Error  = null;

            if (!JSON.TryGetValue(Name, out var token) || token.Type == JTokenType.Null)
                return true;

            if (token.Type != JTokenType.String)
            {
                Error = $"'{Where(Path, Name)}' must be a string.";
                return false;
            }

            var text = token.Value<String>()?.Trim() ?? "";

            if (text.Length == 0)
                return true;

            if (text.Length > MaxLength)
            {
                Error = $"'{Where(Path, Name)}' may be at most {MaxLength} characters long.";
                return false;
            }

            Value = text;
            return true;

        }

        #endregion

        #region (private static) Where(Path, Name)

        /// <summary>
        /// Where a field is, as it is written in the document.
        /// </summary>
        private static String Where(String?  Path,
                                    String   Name)

            => String.IsNullOrEmpty(Path)
                   ? Name
                   : $"{Path}.{Name}";

        #endregion

    }

}
