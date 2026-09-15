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
        /// Waiting for an entry to arrive is what makes the difference: once
        /// one has, the handler has drained everything it had and is waiting
        /// for the next one, which is the state worth testing.
        ///
        /// The entry is written over and over rather than once, and that is not
        /// belt and braces. The handler writes out the events that were already
        /// cached and subscribes to new ones only afterwards, so an entry
        /// logged in the window between the two reaches that client at all -
        /// and a test waiting for exactly that entry would wait for ever.
        /// Repeating costs a few log lines and removes the race.
        /// </remarks>
        public static async Task<EventStream> OpenAndSettle(LocalController  Controller,
                                                            HttpClient       HTTP)
        {

            var stream  = await Open(HTTP);

            var marker  = $"The stream is live: {Guid.NewGuid()}";

            using var settled = new CancellationTokenSource();

            var knocking = Task.Run(async () => {
                               try
                               {
                                   while (!settled.IsCancellationRequested)
                                   {
                                       Controller.Log.Debug(marker, "test");
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

        #region Dispose()

        public void Dispose()
        {
            reader.  Dispose();
            response.Dispose();
        }

        #endregion

    }

}
