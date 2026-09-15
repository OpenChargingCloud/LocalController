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

using cloud.charging.open.LocalController.Configuration;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// What time this local controller thinks it is, and what that is worth.
    /// </summary>
    /// <remarks>
    /// Two different questions, and a local controller has to keep them apart.
    /// The time it shows is its own system clock. Whether that clock is any
    /// good is a separate matter, answered by asking a server that knows - and
    /// **this local controller does not set its clock from the answer**. It measures the
    /// difference and reports it.
    ///
    /// That is deliberate. A local controller is the clock that the charging
    /// stations below it are told the time by and that the transactions
    /// travelling through it are stamped against, so stepping it is not
    /// something a background task does by surprise; a jump backwards would put
    /// two meter readings out of order in a record written somewhere else
    /// entirely, and there would be nothing in it to say why. Measuring is the
    /// part that can be done safely and is the part that tells somebody whether
    /// there is a problem.
    ///
    /// **"Legal time" is never guessed.** Nothing here can tell from a hostname
    /// whether a server disseminates a country's legal time - that is a fact
    /// about an institution, not about DNS. So the operator says so, in
    /// <c>nts.legalTimeAuthority</c>, and this local controller then only repeats that
    /// claim while it can stand behind it: the claim exists, the clock was
    /// actually checked against that server, the check is recent, and the
    /// difference it found was small. Any one of those missing and the answer
    /// says the time is unverified instead - which is a true statement about a
    /// local controller that has not checked, and the whole point of not
    /// printing the word "legal" over it.
    /// </remarks>
    public partial class LocalController
    {

        #region Properties

        /// <summary>
        /// How often this local controller checks its clock.
        /// </summary>
        public TimeSpan        TimeCheckEvery
            => ntsSettings?.CheckEvery ?? NTSConfiguration.DefaultCheckEvery;

        /// <summary>
        /// Who the operator says stands behind the time of the configured
        /// server, or null when nobody has said.
        /// </summary>
        public String?         LegalTimeAuthority
            => ntsSettings?.LegalTimeAuthority;

        /// <summary>
        /// How far this local controller's clock may be off and still count.
        /// </summary>
        public TimeSpan        LegalTimeTolerance
            => ntsSettings?.LegalTimeTolerance ?? NTSConfiguration.DefaultLegalTolerance;

        /// <summary>
        /// How old the last check may be and still count.
        /// </summary>
        public TimeSpan        LegalTimeMaxAge
            => ntsSettings?.LegalTimeMaxAge ?? NTSConfiguration.DefaultLegalMaxAge;

        #endregion


        #region (private) StartCheckingTheClock()

        /// <summary>
        /// Begin checking this local controller's clock against its time server.
        /// </summary>
        /// <remarks>
        /// Through the local controller's own <see cref="TimeProvider"/> rather than a
        /// bare timer, so that a test which moves the clock moves this too.
        ///
        /// The first check is one minute in rather than at once: everything
        /// else is still coming up, the network may not be there yet, and a
        /// page that says "unverified" for a minute after a start is telling
        /// the truth.
        /// </remarks>
        private void StartCheckingTheClock()
        {

            timeCheckTimer?.Dispose();
            timeCheckTimer = null;

            if (!NTSEnabled)
            {
                Log.Info("The clock of this local controller is not being checked: NTS is switched off.", "nts", "clock");
                return;
            }

            timeCheckTimer = TimeProvider.CreateTimer(
                                 _ => _ = CheckTheClockAsync(),
                                 null,
                                 TimeSpan.FromMinutes(1),
                                 TimeCheckEvery
                             );

            Log.Info(
                $"The clock of this local controller will be checked against {ntsClient.Hostname} every {TimeCheckEvery.TotalMinutes:F0} minute(s)" +
                (LegalTimeAuthority is not null ? $", which the operator says is {LegalTimeAuthority}." : "."),
                "nts", "clock"
            );

        }

        #endregion

        #region (private) CheckTheClockAsync()

        /// <summary>
        /// One check, quietly.
        /// </summary>
        /// <remarks>
        /// The same exchange the "Sync now" button does, so there is one way of
        /// asking and one place that records the answer. A failed check is a
        /// warning and not an exception: a time server that cannot be reached
        /// for ten minutes is a thing that happens, and the answer below
        /// already says the time is unverified when the last check is stale.
        /// </remarks>
        private async Task CheckTheClockAsync()
        {

            try
            {

                var result = await SyncTimeAsync();

                if (result.Value<Boolean>("ok") != true)
                    Log.Warning(
                        $"The clock of this local controller could not be checked: {result.Value<String>("error") ?? "no answer"}.",
                        "nts", "clock"
                    );

            }
            catch (Exception e)
            {
                Log.Warning($"The clock of this local controller could not be checked: {e.Message}", "nts", "clock");
            }

        }

        #endregion

        #region ClockJSON()

        /// <summary>
        /// What time it is here, and what that is worth.
        /// </summary>
        /// <remarks>
        /// Everything a reader needs to say something true, and nothing it
        /// would have to interpret: the time, whether it has been checked,
        /// against whom, how long ago, by how much it was out, and whether all
        /// of that adds up to legal time. The one word a page must not invent
        /// is "legal", so it is decided here and sent as a fact.
        /// </remarks>
        public JObject ClockJSON()
        {

            var now       = TimeProvider.GetUtcNow();
            var checkedAt = lastTimeCheck;
            var offset    = lastTimeCheckOffset;
            var age       = checkedAt.HasValue ? now - checkedAt.Value : (TimeSpan?) null;

            var fresh     = age.HasValue && age.Value <= LegalTimeMaxAge;
            var close     = offset.HasValue && offset.Value.Duration() <= LegalTimeTolerance;

            // Every one of these has to hold. Each is a different way of not
            // knowing, and none of them is repaired by the others.
            var legal     = LegalTimeAuthority is not null &&
                            NTSEnabled                     &&
                            fresh                          &&
                            close;

            return new JObject(

                       new JProperty("now",             now.ToString("o")),

                       // Said out loud: the number above is this local controller's own
                       // clock, and the check below did not set it.
                       new JProperty("source",          "system"),

                       new JProperty("nts",             new JObject(
                           new JProperty("enabled",       NTSEnabled),
                           new JProperty("server",        NTSEnabled ? ntsClient.Hostname.ToString() : null),
                           new JProperty("lastServer",    lastTimeCheckServer),
                           new JProperty("checkedAt",     checkedAt?.ToString("o")),
                           new JProperty("ageSeconds",    age.HasValue ? Math.Round(age.Value.TotalSeconds, 1) : null),
                           new JProperty("offset_ms",     offset.HasValue ? Math.Round(offset.Value.TotalMilliseconds, 1) : null),
                           new JProperty("everySeconds",  Math.Round(TimeCheckEvery.TotalSeconds, 0))
                       )),

                       new JProperty("legal",           legal),
                       new JProperty("authority",       LegalTimeAuthority),

                       // Why not, when not - so that a screen can say something
                       // more useful than "no" and somebody looking at it knows
                       // what to go and fix.
                       new JProperty("why",             legal
                                                            ? null
                                                            : LegalTimeAuthority is null ? "notClaimed"
                                                            : !NTSEnabled                ? "ntsOff"
                                                            : !checkedAt.HasValue        ? "neverChecked"
                                                            : !fresh                     ? "stale"
                                                            : !close                     ? "offBy"
                                                            : "unknown"),

                       new JProperty("toleranceSeconds",  Math.Round(LegalTimeTolerance.TotalSeconds, 3)),
                       new JProperty("maxAgeSeconds",     Math.Round(LegalTimeMaxAge.   TotalSeconds, 0))

                   );

        }

        #endregion

    }

}
