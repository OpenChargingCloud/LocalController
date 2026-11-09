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

using System.Globalization;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.OpenADRv3;

#endregion

namespace cloud.charging.open.LocalController.OpenADR
{

    /// <summary>
    /// One limit the grid operator sent this local controller: one interval of
    /// an OpenADR 3 event - how much power, of which kind, from when to when.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An event of the grid operator has one interval, and the event's
    /// <c>intervalPeriod</c> says when; OpenADR 3 lets an event have several,
    /// one after the other, each as long as the event's period says unless it
    /// has a period of its own. So every interval is a limit here, and they
    /// are numbered as they come.
    /// </para>
    /// <para>
    /// The kind is the payload type - <c>IMPORT_CAPACITY_LIMIT</c>, how much
    /// the charging pool may draw from the grid, or <c>EXPORT_CAPACITY_LIMIT</c>,
    /// how much it may feed in - and the value is in kW unless the event's
    /// payload descriptor says W.
    /// </para>
    /// </remarks>
    /// <param name="EventId">The event it is an interval of.</param>
    /// <param name="ProgramId">The program of that event.</param>
    /// <param name="EventName">What the grid operator called the event, if anything.</param>
    /// <param name="Priority">The priority of the event: the lower, the more it counts. Null for none.</param>
    /// <param name="Interval">The number of the interval within the event.</param>
    /// <param name="Kind">The payload type: IMPORT_CAPACITY_LIMIT, EXPORT_CAPACITY_LIMIT, or whatever else the grid operator sent.</param>
    /// <param name="KW">The limit, in kW.</param>
    /// <param name="Start">When it begins.</param>
    /// <param name="End">When it ends; null for an interval of no given length, which lasts until it is withdrawn.</param>
    public sealed record GridLimit(Event_Id         EventId,
                                   String           ProgramId,
                                   String?          EventName,
                                   UInt32?          Priority,
                                   Int32            Interval,
                                   String           Kind,
                                   Double           KW,
                                   DateTimeOffset   Start,
                                   DateTimeOffset?  End)
    {

        #region Data

        /// <summary>
        /// How much the charging pool may draw from the grid.
        /// </summary>
        public const String  ImportCapacityLimit  = "IMPORT_CAPACITY_LIMIT";

        /// <summary>
        /// How much it may feed into the grid.
        /// </summary>
        public const String  ExportCapacityLimit  = "EXPORT_CAPACITY_LIMIT";

        #endregion


        #region IsInForceAt(Now) / IsOverAt(Now) / StateAt(Now)

        /// <summary>
        /// Whether this limit holds at the given time.
        /// </summary>
        public Boolean IsInForceAt(DateTimeOffset Now)

            => Start <= Now && (End is null || Now < End);

        /// <summary>
        /// Whether it is over at the given time.
        /// </summary>
        public Boolean IsOverAt(DateTimeOffset Now)

            => End is not null && End <= Now;

        /// <summary>
        /// "active", "scheduled" or "over", as the grid operator says it.
        /// </summary>
        public String StateAt(DateTimeOffset Now)

            => IsInForceAt(Now) ? "active"
             : IsOverAt   (Now) ? "over"
             :                    "scheduled";

        #endregion


        #region (static) Of(Event, out Problems)

        /// <summary>
        /// The limits of an event, interval by interval - and what of it could
        /// not be taken as a limit, said rather than guessed at.
        /// </summary>
        public static IReadOnlyList<GridLimit> Of(Event                Event,
                                                  out List<String>     Problems)
        {

            Problems = [];

            var limits     = new List<GridLimit>();
            var eventId    = Event.Id;

            if (eventId is null)
            {
                Problems.Add("An event without an identification was passed over.");
                return limits;
            }

            var descriptor = Event.PayloadDescriptors.FirstOrDefault();
            var unit       = descriptor?.Units?.ToString()?.ToUpperInvariant();
            var factor     = unit switch {
                                 null or "KW"  => 1.0,
                                 "W"           => 0.001,
                                 "MW"          => 1000.0,
                                 _             => Double.NaN
                             };

            if (Double.IsNaN(factor))
            {
                Problems.Add($"The event '{eventId}' gives its limits in '{unit}', which is not a unit of power; it was passed over.");
                return limits;
            }

            var period     = Event.IntervalPeriod;
            var number     = 0;
            var next       = period?.Start;

            foreach (var interval in Event.Intervals)
            {

                var own       = interval.IntervalPeriod;
                var start     = own?.Start ?? next;
                var duration  = own?.Duration ?? period?.Duration;

                if (start is null)
                {
                    Problems.Add($"The interval {interval.Id} of the event '{eventId}' says not when it begins, and neither does the event; it was passed over.");
                    number++;
                    continue;
                }

                var end       = duration is TimeSpan length && length > TimeSpan.Zero
                                    ? start + length
                                    : (DateTimeOffset?) null;

                next = end;

                foreach (var payload in interval.Payloads)
                {

                    var kind  = payload.Type.ToString();
                    var value = payload.Values.FirstOrDefault();

                    if (!TryNumber(value, out var kw))
                    {
                        Problems.Add($"The interval {interval.Id} of the event '{eventId}' has no number for '{kind}', but '{value}'; it was passed over.");
                        continue;
                    }

                    limits.Add(new GridLimit(eventId.Value,
                                             Event.ProgramId.ToString(),
                                             Event.EventName is { Length: > 0 } name ? name : null,
                                             Event.Priority,
                                             number,
                                             kind,
                                             kw * factor,
                                             start.Value,
                                             end));

                }

                number++;

            }

            return limits;

        }

        #endregion

        #region (static) InForce(Limits, Kind, Now)

        /// <summary>
        /// The limit of the given kind in force now: the lowest of those that
        /// hold, whatever their priorities - two limits of a grid operator that
        /// overlap are both kept by keeping the lower one. Null where none
        /// holds.
        /// </summary>
        public static GridLimit? InForce(IEnumerable<GridLimit>  Limits,
                                         String                  Kind,
                                         DateTimeOffset          Now)

            => Limits.Where  (limit => limit.Kind == Kind && limit.IsInForceAt(Now)).
                      OrderBy(limit => limit.KW).
                      ThenBy (limit => limit.Priority ?? UInt32.MaxValue).
                      FirstOrDefault();

        #endregion


        #region ToJSON(Now)

        /// <summary>
        /// The limit, for the web interface.
        /// </summary>
        public JObject ToJSON(DateTimeOffset Now)

            => new (new JProperty("eventID",    EventId.ToString()),
                    new JProperty("programID",  ProgramId),
                    new JProperty("name",       EventName),
                    new JProperty("priority",   Priority),
                    new JProperty("interval",   Interval),
                    new JProperty("kind",       Kind),
                    new JProperty("kW",         KW),
                    new JProperty("start",      Start.ToString("o")),
                    new JProperty("end",        End?.ToString("o")),
                    new JProperty("state",      StateAt(Now)));

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Kind} {KW.ToString("0.###", CultureInfo.InvariantCulture)} kW from {Start.UtcDateTime:yyyy-MM-dd HH:mm} " +
               (End is DateTimeOffset end ? $"to {end.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "UTC on, until withdrawn");

        #endregion


        #region (private static) TryNumber(Value, out Number)

        private static Boolean TryNumber(Object? Value, out Double Number)
        {

            Number = Value switch {
                         Double   number  => number,
                         Single   number  => number,
                         Int64    number  => number,
                         Int32    number  => number,
                         Decimal  number  => (Double) number,
                         JValue   json when json.Type is JTokenType.Float or JTokenType.Integer
                                          => json.Value<Double>(),
                         String   text when Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                                          => parsed,
                         _                => Double.NaN
                     };

            return !Double.IsNaN(Number) && !Double.IsInfinity(Number) && Number >= 0;

        }

        #endregion

    }

}
