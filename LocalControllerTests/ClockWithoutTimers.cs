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

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The system's clock, with timers that never fire.
    /// </summary>
    /// <remarks>
    /// For a test that has to start a controller with its time client switched
    /// on - to read the line a start writes about the clock, or the one a
    /// change of the interval writes - and must not have that controller ask a
    /// time server a minute later, which is what the first clock check would
    /// do if the test ever ran that long. Those lines are written when the
    /// timer is set, not when it fires, so nothing a test reads is lost.
    ///
    /// Every other test switches the time client off, which keeps the check
    /// from being scheduled at all.
    /// </remarks>
    internal sealed class ClockWithoutTimers : TimeProvider
    {

        #region Instance

        /// <summary>
        /// The one there needs to be: it holds nothing.
        /// </summary>
        public static readonly ClockWithoutTimers Instance = new();

        #endregion


        #region (override) GetUtcNow()

        public override DateTimeOffset GetUtcNow()
            => TimeProvider.System.GetUtcNow();

        #endregion

        #region (override) CreateTimer(Callback, State, DueTime, Period)

        public override ITimer CreateTimer(TimerCallback  Callback,
                                           Object?        State,
                                           TimeSpan       DueTime,
                                           TimeSpan       Period)

            => new Inert();

        #endregion


        #region (private) Inert

        /// <summary>
        /// A timer that is set, changed and disposed of like any other, and
        /// never calls anybody.
        /// </summary>
        private sealed class Inert : ITimer
        {

            public Boolean Change(TimeSpan DueTime, TimeSpan Period)
                => true;

            public void Dispose()
            { }

            public ValueTask DisposeAsync()
                => ValueTask.CompletedTask;

        }

        #endregion

    }

}
