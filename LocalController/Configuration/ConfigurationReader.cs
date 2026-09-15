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
