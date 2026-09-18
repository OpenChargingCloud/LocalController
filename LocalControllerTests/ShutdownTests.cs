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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Whether a local controller that was told to stop actually does.
    /// </summary>
    /// <remarks>
    /// This exists because it once did not. A browser on the Logs page holds a
    /// request open that is waiting for the next log entry rather than for its
    /// socket, so the server closing that socket underneath it does not wake
    /// it - and Hermod waits for every request it started before it reports
    /// itself stopped. A controller with one browser watching would therefore
    /// never finish shutting down, which on a machine being restarted means
    /// waiting out a kill timeout instead of stopping.
    ///
    /// Stop() now ends the event streams before it stops the server. These
    /// tests are what says it still does.
    ///
    /// Each builds its own controller and stops it, so neither can use the one
    /// the fixture base would have started and taken away again.
    ///
    /// One thing cannot be undone once it happens: a stop that never returns
    /// cannot be cancelled, so a test that hits it leaves it running. A later
    /// test in the same run can then stop in milliseconds where on its own it
    /// hangs, which would report a pass nobody earned.
    ///
    /// So the first such test poisons the rest: every test after one that was
    /// left waiting is reported inconclusive rather than passed. The run still
    /// shows the real failure, and shows plainly that it cannot vouch for what
    /// came after it - run them one at a time to see the rest.
    /// </remarks>
    public class ShutdownTests
    {

        #region Data

        /// <summary>
        /// How long stopping may take before this counts as not stopping.
        /// </summary>
        /// <remarks>
        /// Stopping takes single-digit milliseconds when it works, and forever
        /// when it does not - so anything in between is a slow machine rather
        /// than a near miss, and this is set where a slow machine still passes.
        /// </remarks>
        private static readonly TimeSpan  MustStopWithin = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Whether a stop in this run was given up on and left running.
        /// </summary>
        /// <remarks>
        /// Static, and deliberately never reset: the abandoned stop is still
        /// there for the rest of the process, so every test after it is
        /// suspect for the rest of the process.
        /// </remarks>
        private static Boolean  aStopWasLeftRunning;

        private String directory = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {

            if (aStopWasLeftRunning)
                Assert.Inconclusive(
                    "An earlier test in this fixture was left waiting for a stop that never returned, and " +
                    "that stop is still running. Whatever this test would report cannot be trusted - run it " +
                    "on its own."
                );

            directory = TestControllers.TemporaryDirectory("shutdown");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void RemoveTheDirectory()
        {
            TestControllers.Remove(directory);
        }

        #endregion


        #region StopsWithNobodyWatching()

        [Test]
        public async Task StopsWithNobodyWatching()
        {

            var (controller, _) = await StartOne();

            var elapsed = await TimeTheStop(controller);

            Assert.That(elapsed, Is.LessThan(MustStopWithin),
                        $"An idle controller took {elapsed.TotalSeconds:F1} s to stop.");

        }

        #endregion

        #region StopsWithABrowserOnTheLogsPage()

        /// <summary>
        /// The regression test. One open event stream, which is what a browser
        /// showing the Logs page is, and then a stop.
        /// </summary>
        [Test]
        public async Task StopsWithABrowserOnTheLogsPage()
        {

            var (controller, http) = await StartOne();

            using (http)
            {

                // Settled, not merely opened: a handler that is still writing
                // is ended by its socket closing, and this test would then pass
                // against a controller that cannot shut down at all.
                using var stream = await EventStream.OpenAndSettle(controller, http);

                // And now left alone, which is what a browser sitting on the
                // Logs page is when the controller is told to stop.
                var elapsed = await TimeTheStop(controller);

                Assert.That(elapsed, Is.LessThan(MustStopWithin),
                            $"A controller with one open event stream took {elapsed.TotalSeconds:F1} s to stop. " +
                            "The streams are not being ended before the server is.");

            }

        }

        #endregion

        #region StopsWithSeveralBrowsersOnTheLogsPage()

        /// <summary>
        /// Several of them, because ending the streams has to end all of them -
        /// one cancellation token that only the first stream observed would
        /// pass the test above and hang a real controller.
        /// </summary>
        [Test]
        public async Task StopsWithSeveralBrowsersOnTheLogsPage()
        {

            var (controller, first) = await StartOne();

            var browsers = new List<HttpClient> { first };
            var streams  = new List<EventStream>();

            try
            {

                streams.Add(await EventStream.OpenAndSettle(controller, first));

                for (var i = 0; i < 3; i++)
                {
                    var another = await SignIn(controller);
                    browsers.Add(another);
                    streams.Add(await EventStream.OpenAndSettle(controller, another));
                }

                var elapsed = await TimeTheStop(controller);

                Assert.That(elapsed, Is.LessThan(MustStopWithin),
                            $"A controller with {streams.Count} open event streams took {elapsed.TotalSeconds:F1} s to stop.");

            }
            finally
            {
                foreach (var stream  in streams)   stream. Dispose();
                foreach (var browser in browsers)  browser.Dispose();
            }

        }

        #endregion

        #region StoppingTwiceIsHarmless()

        /// <summary>
        /// DisposeAsync stops as well, and a controller inside a using block
        /// that was also stopped by hand is an ordinary thing to write.
        /// </summary>
        [Test]
        public async Task StoppingTwiceIsHarmless()
        {

            var (controller, http) = await StartOne();

            http.Dispose();

            await controller.Stop();

            Assert.DoesNotThrowAsync(async () => {
                await controller.Stop();
                await controller.DisposeAsync();
            });

        }

        #endregion


        #region (private) StartOne()

        /// <summary>
        /// A controller, listening, and a browser already signed in to it.
        /// </summary>
        private async Task<(LocalController Controller, HttpClient HTTP)> StartOne()
        {

            // Offline: nothing here should wait on a network while it is
            // trying to measure how long stopping takes.
            var controller = TestControllers.New(directory, TestControllers.Offline);

            await controller.Start();

            return (controller, await SignIn(controller));

        }

        #endregion

        #region (private static) SignIn(Controller)

        private static async Task<HttpClient> SignIn(LocalController Controller)
        {

            var http = new HttpClient(new HttpClientHandler { UseCookies = true }) {
                           BaseAddress = new Uri(Controller.WebInterfaceURL.ToString())
                       };

            // At the HTTPExt API: it is the only place that can check a
            // password, and the cookie it sets is what the JSON API reads.
            var response = await http.PostAsync(
                                     $"{LocalController.ExtAPIPath.ToString().TrimEnd('/')}/login",
                                     new FormUrlEncodedContent([
                                         new KeyValuePair<String, String>("login",     LocalController.DefaultAdminUser),
                                         new KeyValuePair<String, String>("password",  Controller.GeneratedPassword ?? "")
                                     ])
                                 );

            Assert.That(response.IsSuccessStatusCode, Is.True, "Signing in failed.");

            return http;

        }

        #endregion

        #region (private static) TimeTheStop(Controller)

        /// <summary>
        /// How long it took to stop - and, when it does not stop at all, how
        /// long this test was prepared to wait.
        /// </summary>
        /// <remarks>
        /// The stop is raced against a timer rather than simply awaited,
        /// because the failure this whole file is about is a stop that never
        /// returns: awaiting it would hang the test run instead of failing it,
        /// and a hung run says nothing about which test hung.
        /// </remarks>
        private static async Task<TimeSpan> TimeTheStop(LocalController Controller)
        {

            var clock    = Stopwatch.StartNew();

            var stopping = Controller.Stop();
            var giveUp   = Task.Delay(MustStopWithin);

            var first    = await Task.WhenAny(stopping, giveUp);

            clock.Stop();

            if (first == stopping)
            {
                // Observed, so that a stop which failed rather than hung is
                // reported as the exception it threw.
                await stopping;
                return clock.Elapsed;
            }

            // Left running, because there is no way not to: awaiting the stop
            // that did not finish is exactly the hang this is here to report
            // instead of. What can be done is to stop trusting what comes next.
            aStopWasLeftRunning = true;

            return MustStopWithin + TimeSpan.FromSeconds(1);

        }

        #endregion

    }

}
