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

using System.Text;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Opening the Server-Sent Events stream and waiting for something to come
    /// down it.
    /// </summary>
    internal sealed class EventStream : IDisposable
    {

        #region Data

        /// <summary>
        /// How long a test waits for something to arrive before it says the
        /// stream is broken.
        /// </summary>
        public static readonly TimeSpan  Timeout = TimeSpan.FromSeconds(20);

        /// <summary>
        /// How long the handler is left undisturbed to reach the end of what
        /// it had cached. Generous: the cache is a few dozen entries, and the
        /// cost of being wrong here is a test that passes against a broken
        /// server.
        /// </summary>
        private static readonly TimeSpan  Draining = TimeSpan.FromMilliseconds(500);

        private readonly HttpResponseMessage  response;
        private readonly StreamReader         reader;
        private readonly StringBuilder        read = new ();

        #endregion

        #region Constructor(s)

        private EventStream(HttpResponseMessage  Response,
                            StreamReader         Reader)
        {
            this.response  = Response;
            this.reader    = Reader;
        }

        #endregion


        #region (static) Open(HTTP)

        /// <summary>
        /// Open the stream and check that it was opened.
        /// </summary>
        public static async Task<EventStream> Open(HttpClient HTTP)
        {

            var response = await HTTP.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);

            Assert.Multiple(() => {
                Assert.That(response.IsSuccessStatusCode,                     Is.True,
                            "The event stream did not open, so the test using it would pass for the wrong reason.");
                Assert.That(response.Content.Headers.ContentType?.MediaType,  Is.EqualTo("text/event-stream"));
            });

            return new EventStream(response, new StreamReader(await response.Content.ReadAsStreamAsync()));

        }

        #endregion

        #region (static) OpenAndSettle(Controller, HTTP)

        /// <summary>
        /// Open the stream and wait until it is demonstrably live: until an
        /// entry written after it was opened has come down it.
        /// </summary>
        /// <remarks>
        /// Opening one is not enough to test anything about it, and that is not
        /// a detail. The handler on the other end first writes out the events
        /// the source had cached and only then parks on the channel waiting for
        /// the next one - and the whole question a shutdown test asks is what
        /// happens to a handler that is parked. A test that opened a stream and
        /// immediately stopped the controller would usually catch the handler
        /// still writing, where a closed socket ends it by itself, and would
        /// therefore pass against a controller that cannot shut down at all.
        ///
        /// So this settles in two steps, and the second one is the one that
        /// proves anything.
        ///
        /// First it knocks - writes an entry over and over until one comes
        /// back - which says the stream carries entries at all. Written
        /// repeatedly rather than once because the handler subscribes only
        /// after it has drained what was cached, and an entry written into
        /// that window never reaches that client; a test waiting for exactly
        /// that one entry would wait for ever.
        ///
        /// But arriving is not yet proof of parking: an entry written while
        /// the handler is still draining is delivered out of the cache, and
        /// the handler is then still writing rather than waiting. So the
        /// knocking stops, nothing is written for a moment - long enough for
        /// the handler to run out of cached entries and subscribe - and then
        /// one last entry goes in. That one has no cache to be delivered from.
        /// Its arrival means the handler was waiting for it, which is the
        /// state the bug was about.
        /// </remarks>
        public static async Task<EventStream> OpenAndSettle(LocalController  Controller,
                                                            HttpClient       HTTP)
        {

            var stream  = await Open(HTTP);

            var marker  = $"The stream is live: {Guid.NewGuid()}";

            using var settled = new CancellationTokenSource();

            void Write(String Text)
                => Controller.Log.Debug(Text, "test");

            var knocking = Task.Run(async () => {
                               try
                               {
                                   while (!settled.IsCancellationRequested)
                                   {
                                       Write(marker);
                                       await Task.Delay(TimeSpan.FromMilliseconds(100), settled.Token);
                                   }
                               }
                               catch (OperationCanceledException)
                               { }
                           });

            var arrived = await stream.ReadUntil(marker);

            settled.Cancel();
            await knocking;

            Assert.That(arrived, Is.True,
                        $"Nothing arrived over the event stream within {Timeout.TotalSeconds} seconds, so it never became live.");

            // The quiet moment, so that the handler reaches the end of what it
            // had cached and subscribes.
            await Task.Delay(Draining);

            var parked = $"The stream is parked: {Guid.NewGuid()}";

            Write(parked);

            Assert.That(await stream.ReadUntil(parked), Is.True,
                        $"Nothing arrived over the event stream in the {Timeout.TotalSeconds} seconds after it went quiet, " +
                        "so the handler never started waiting for new entries.");

            return stream;

        }

        #endregion

        #region ReadUntil(Text)

        /// <summary>
        /// Read until the given text has come through, or until waiting stops
        /// being reasonable.
        /// </summary>
        public async Task<Boolean> ReadUntil(String Text)
        {

            var buffer = new Char[1024];

            using var cancellation = new CancellationTokenSource(Timeout);

            try
            {

                while (!read.ToString().Contains(Text, StringComparison.Ordinal))
                {

                    var count = await reader.ReadAsync(buffer, cancellation.Token);

                    if (count == 0)
                        return false;

                    read.Append(buffer, 0, count);

                }

                return true;

            }
            catch (OperationCanceledException)
            {
                return false;
            }

        }

        #endregion

        #region Count(Text)

        /// <summary>
        /// How often the given text has come down the stream so far: for a
        /// test that has to know an entry arrived once, and not twice.
        /// </summary>
        public Int32 Count(String Text)

            => read.ToString().Split(Text).Length - 1;

        #endregion

        #region Dispose()

        public void Dispose()
        {
            reader.  Dispose();
            response.Dispose();
        }

        #endregion

    }

}
