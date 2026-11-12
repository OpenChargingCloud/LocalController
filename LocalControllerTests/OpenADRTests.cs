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

using System.Net;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.LocalController.Configuration;
using cloud.charging.open.LocalController.OpenADR;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// This local controller as a VEN of its grid operator's OpenADR 3 VTN:
    /// signing in with its client credentials, keeping its token fresh, reading
    /// the limits meant for it, and reporting back on each - against a VTN that
    /// answers as the grid operator's does.
    /// </summary>
    public class OpenADRTests
    {

        #region Data

        private const String  VENName  = "lc-paradies";
        private const String  Secret   = "sQ3-the-secret-the-grid-operator-gave";

        private String            directory   = default!;
        private FakeVTN           vtn         = default!;
        private LocalController?  controller;
        private TestClock?        clock;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task StartAVTN()
        {
            directory  = TestControllers.TemporaryDirectory("openadr");
            vtn        = await FakeVTN.Started(VENName, Secret);
            clock      = null;
        }

        [TearDown]
        public async Task StopThem()
        {

            if (controller is not null)
                await controller.DisposeAsync();

            controller = null;

            await vtn.DisposeAsync();

            TestControllers.Remove(directory);

        }

        #endregion

        #region (private) AController(Secret = Secret, Enabled = true, Clock = null)

        /// <summary>
        /// A local controller that asks the VTN as 'lc-paradies' - once at its
        /// start, and then only when a test asks it to, so that what a test
        /// counts is what it caused.
        /// </summary>
        private async Task<LocalController> AController(String?        Secret   = OpenADRTests.Secret,
                                                        Boolean        Enabled  = true,
                                                        TimeProvider?  Clock    = null)
        {

            Directory.CreateDirectory(directory);

            if (Secret is not null)
                File.WriteAllText(Path.Combine(directory, VTNCredentials.DefaultFileName),
                                  new JObject(new JProperty("clientSecret", Secret)).ToString());

            var configuration = TestControllers.Offline;

            configuration.Add(OpenADRConfiguration.SectionName, new JObject(
                new JProperty("enabled",    Enabled),
                new JProperty("vtnURL",     vtn.VTNURL),
                new JProperty("clientID",   VENName),
                new JProperty("pollEvery",  3600)
            ));

            controller = await TestPorts.StartedOnFreshPorts(() => TestControllers.New(directory, configuration, Clock));

            return controller;

        }

        #endregion


        #region ReadsTheLimitsMeantForItAndReportsOnEachOnce()

        /// <summary>
        /// The limits the grid operator sent are kept, the lowest one in force
        /// is what holds, and one report goes back on each event - once, however
        /// often the VTN is asked.
        /// </summary>
        [Test]
        public async Task ReadsTheLimitsMeantForItAndReportsOnEachOnce()
        {

            var now      = DateTimeOffset.UtcNow;

            var import44 = vtn.Send("IMPORT_CAPACITY_LIMIT", 44, now.AddMinutes(-5), TimeSpan.FromHours(1), "Engpass Nord");
            var import30 = vtn.Send("IMPORT_CAPACITY_LIMIT", 30, now.AddMinutes(-1), TimeSpan.FromMinutes(30));
            var later    = vtn.Send("IMPORT_CAPACITY_LIMIT", 10, now.AddHours(2),     TimeSpan.FromHours(1));
            var export   = vtn.Send("EXPORT_CAPACITY_LIMIT",  0, now.AddMinutes(-5), TimeSpan.FromHours(1));

            var lc       = await AController();

            Assert.That(await lc.PollOpenADR(), Is.Null);
            Assert.That(await lc.PollOpenADR(), Is.Null);

            Assert.Multiple(() => {

                Assert.That(lc.OpenADRSignedIn,                       Is.True);
                Assert.That(lc.GridLimits.Select(limit => limit.EventId.ToString()),
                            Is.EquivalentTo(new[] { import44, import30, later, export }));
                Assert.That(lc.ImportLimitNow?.KW,                    Is.EqualTo(30), "the lowest import limit in force holds");
                Assert.That(lc.ExportLimitNow?.KW,                    Is.EqualTo(0));
                Assert.That(lc.GridLimits.Single(limit => limit.EventId.ToString() == later).StateAt(now), Is.EqualTo("scheduled"));
                Assert.That(lc.GridLimits.Single(limit => limit.EventId.ToString() == import44).EventName, Is.EqualTo("Engpass Nord"));

                Assert.That(vtn.TokenRequests,                        Is.EqualTo(1), "signed in more than once");
                Assert.That(vtn.Reports.Select(report => report.Value<String>("eventID")),
                            Is.EquivalentTo(new[] { import44, import30, later, export }), "not one report on each event");

            });

            var report = vtn.Reports.Single(report => report.Value<String>("eventID") == import44);
            var values = report["resources"]?[0]?["intervals"]?[0]?["payloads"]?[0];

            Assert.Multiple(() => {
                Assert.That(report.Value<String>("programID"),   Is.EqualTo(FakeVTN.ProgramID));
                Assert.That(report.Value<String>("clientName"),  Is.EqualTo(VENName));
                Assert.That(values?.Value<String>("type"),       Is.EqualTo("IMPORT_CAPACITY_LIMIT"));
                Assert.That(values?["values"]?[0]?.Value<Double>(), Is.EqualTo(44));
            });

        }

        #endregion

        #region SignsInWithItsClientCredentials()

        /// <summary>
        /// The token is asked for with the client credentials grant, the client
        /// ID and secret as HTTP Basic - and nothing is read with a wrong secret,
        /// which is said in the VTN's words.
        /// </summary>
        [Test]
        public async Task SignsInWithItsClientCredentials()
        {

            vtn.Send("IMPORT_CAPACITY_LIMIT", 44, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

            var lc      = await AController(Secret: "not-the-secret");

            var problem = await lc.PollOpenADR();

            Assert.Multiple(() => {
                Assert.That(problem,                 Does.Contain("invalid_client").And.Contain(VENName));
                Assert.That(lc.OpenADRLastProblem,   Is.EqualTo(problem));
                Assert.That(lc.OpenADRSignedIn,      Is.False);
                Assert.That(lc.GridLimits,           Is.Empty);
                Assert.That(vtn.EventRequests,       Is.Zero);
                Assert.That(vtn.LastTokenAuthorization,
                            Is.EqualTo("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{VENName}:not-the-secret"))));
            });

            // The right secret, set as the JSON API sets it: signed in anew.
            Assert.That(lc.OpenADRLogin.TrySetSecret(Secret, out var error), Is.True, error);

            await lc.RestartOpenADR();

            var again = await lc.PollOpenADR();

            Assert.Multiple(() => {
                Assert.That(again,                   Is.Null);
                Assert.That(lc.OpenADRSignedIn,      Is.True);
                Assert.That(lc.OpenADRLastProblem,   Is.Null);
                Assert.That(lc.GridLimits,           Has.Count.EqualTo(1));
            });

        }

        #endregion

        #region RenewsItsTokenBeforeItExpires()

        /// <summary>
        /// A token of an hour is used for most of it, and renewed five minutes
        /// before it ends.
        /// </summary>
        [Test]
        public async Task RenewsItsTokenBeforeItExpires()
        {

            clock   = new TestClock(DateTimeOffset.UtcNow);

            var lc  = await AController(Clock: clock);

            Assert.That(await lc.PollOpenADR(), Is.Null);
            Assert.That(vtn.TokenRequests, Is.EqualTo(1));

            var expires = lc.OpenADRToken.Expires ?? throw new AssertionException("The first poll left no token that expires.");

            clock.Advance(TimeSpan.FromMinutes(50));

            Assert.That(await lc.PollOpenADR(), Is.Null);
            Assert.That(vtn.TokenRequests, Is.EqualTo(1), "renewed with ten minutes to go");

            clock.Advance(TimeSpan.FromMinutes(6));

            Assert.That(await lc.PollOpenADR(), Is.Null);

            Assert.Multiple(() => {
                Assert.That(vtn.TokenRequests,            Is.EqualTo(2), "not renewed with four minutes to go");
                Assert.That(lc.OpenADRToken.Expires,      Is.GreaterThan(expires));
                Assert.That(vtn.Refused401,               Is.Zero, "a token was used that the VTN did not take");
            });

        }

        #endregion

        #region SignsInAgainOnA401()

        /// <summary>
        /// A VTN that let go of the token before its time - restarted - says 401,
        /// and the request is made again with a new token.
        /// </summary>
        [Test]
        public async Task SignsInAgainOnA401()
        {

            vtn.Send("IMPORT_CAPACITY_LIMIT", 22, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

            var lc = await AController();

            Assert.That(await lc.PollOpenADR(), Is.Null);

            vtn.ForgetTokens();

            Assert.That(await lc.PollOpenADR(), Is.Null);

            Assert.Multiple(() => {
                Assert.That(vtn.TokenRequests,     Is.EqualTo(2));
                Assert.That(vtn.Refused401,        Is.EqualTo(1));
                Assert.That(lc.OpenADRSignedIn,    Is.True);
                Assert.That(lc.ImportLimitNow?.KW, Is.EqualTo(22));
            });

        }

        #endregion

        #region AWithdrawnLimitIsGoneAndAnOverOneIsNotShown()

        /// <summary>
        /// A limit the VTN no longer lists is gone at the next poll; one whose
        /// time is over is not shown, even before the VTN stops listing it.
        /// </summary>
        [Test]
        public async Task AWithdrawnLimitIsGoneAndAnOverOneIsNotShown()
        {

            clock       = new TestClock(DateTimeOffset.UtcNow);

            var short1  = vtn.Send("IMPORT_CAPACITY_LIMIT", 11, clock.Now.AddMinutes(-1), TimeSpan.FromMinutes(10));
            var long1   = vtn.Send("IMPORT_CAPACITY_LIMIT", 40, clock.Now.AddMinutes(-1), TimeSpan.FromHours(2));

            var lc      = await AController(Clock: clock);

            Assert.That(await lc.PollOpenADR(), Is.Null);
            Assert.That(lc.ImportLimitNow?.KW, Is.EqualTo(11));

            clock.Advance(TimeSpan.FromMinutes(15));

            Assert.Multiple(() => {
                Assert.That(lc.GridLimits.Select(limit => limit.EventId.ToString()), Is.EqualTo(new[] { long1 }));
                Assert.That(lc.ImportLimitNow?.KW, Is.EqualTo(40));
            });

            vtn.Withdraw(long1);
            vtn.Withdraw(short1);

            Assert.That(await lc.PollOpenADR(), Is.Null);

            Assert.Multiple(() => {
                Assert.That(lc.GridLimits,      Is.Empty);
                Assert.That(lc.ImportLimitNow,  Is.Null);
            });

        }

        #endregion

        #region ARestartReportsNothingTwice()

        /// <summary>
        /// Which events were reported on is asked of the VTN after a start, so a
        /// controller started again reports on none of them a second time.
        /// </summary>
        [Test]
        public async Task ARestartReportsNothingTwice()
        {

            var first = vtn.Send("IMPORT_CAPACITY_LIMIT", 44, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

            var lc    = await AController();

            Assert.That(await lc.PollOpenADR(), Is.Null);

            await lc.DisposeAsync();
            controller = null;

            var second = vtn.Send("IMPORT_CAPACITY_LIMIT", 33, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

            lc = await AController();

            Assert.That(await lc.PollOpenADR(), Is.Null);

            Assert.That(vtn.Reports.Select(report => report.Value<String>("eventID")),
                        Is.EqualTo(new[] { first, second }));

        }

        #endregion

        #region ARefusedReportIsNotSentAgain()

        /// <summary>
        /// A report the VTN says no to is said, and not sent again at every
        /// poll; the limit of its event holds all the same, and the reports on
        /// the other events go.
        /// </summary>
        [Test]
        public async Task ARefusedReportIsNotSentAgain()
        {

            var refused = vtn.Send("IMPORT_CAPACITY_LIMIT", 44, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));
            var taken   = vtn.Send("EXPORT_CAPACITY_LIMIT",  5, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

            vtn.RefuseReportsOn.Add(refused);

            var lc = await AController();

            Assert.That(await lc.PollOpenADR(), Is.Null);
            Assert.That(await lc.PollOpenADR(), Is.Null);

            Assert.Multiple(() => {
                Assert.That(vtn.ReportRequests,                                          Is.EqualTo(2), "a refused report was sent again");
                Assert.That(vtn.Reports.Select(report => report.Value<String>("eventID")), Is.EqualTo(new[] { taken }));
                Assert.That(lc.ImportLimitNow?.KW,                                       Is.EqualTo(44));
                Assert.That(lc.OpenADRLastProblem,                                       Is.Null);
            });

        }

        #endregion

        #region AVTNThatCannotBeReachedIsSaidAndTheLimitsStay()

        /// <summary>
        /// A VTN that is gone is a problem said once, and the limits read last
        /// stay until they are over.
        /// </summary>
        [Test]
        public async Task AVTNThatCannotBeReachedIsSaidAndTheLimitsStay()
        {

            vtn.Send("IMPORT_CAPACITY_LIMIT", 44, DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

            var lc = await AController();

            Assert.That(await lc.PollOpenADR(), Is.Null);

            await vtn.DisposeAsync();

            var problem = await lc.PollOpenADR();

            Assert.Multiple(() => {
                Assert.That(problem,               Does.Contain("could not be reached").Or.Contain("did not answer"));
                Assert.That(lc.ImportLimitNow?.KW, Is.EqualTo(44));
            });

            // Started again for the TearDown, which stops it.
            vtn = await FakeVTN.Started(VENName, Secret);

        }

        #endregion

        #region SwitchedOffItAsksNothing()

        [Test]
        public async Task SwitchedOffItAsksNothing()
        {

            var lc   = await AController(Enabled: false);
            var said = await lc.PollOpenADR();

            Assert.Multiple(() => {
                Assert.That(said,               Does.Contain("switched off"));
                Assert.That(vtn.TokenRequests,  Is.Zero);
            });

            var (changed, error, _) = await lc.TryUpdateOpenADRConfiguration(new JObject(new JProperty("enabled", true)));

            Assert.That(changed, Is.True, error);
            Assert.That(await lc.PollOpenADR(), Is.Null);
            Assert.That(vtn.TokenRequests, Is.EqualTo(1));

            Assert.That(File.ReadAllText(lc.ConfigFile.Path), Does.Contain("\"enabled\": true"));

        }

        #endregion

        #region TheSecretIsKeptForItsOwnerAndShownToNobody()

        /// <summary>
        /// Set over the JSON API, the secret goes into a file of its own - never
        /// into the configuration file - and the API says only that there is one.
        /// </summary>
        [Test]
        public async Task TheSecretIsKeptForItsOwnerAndShownToNobody()
        {

            var lc        = await AController(Secret: null);
            var problem   = await lc.PollOpenADR();

            Assert.That(problem, Does.Contain("no client secret"));

            using var http = await SignedIn(lc);

            var put       = await http.PutAsync("/api/v1/configuration/openADR",
                                                new StringContent(new JObject(new JProperty("pollEvery",     600),
                                                                              new JProperty("clientSecret",  Secret)).ToString(),
                                                                  Encoding.UTF8, "application/json"));

            var answer    = await put.Content.ReadAsStringAsync();

            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK), answer);

            var get       = await http.GetStringAsync("/api/v1/configuration/openADR");
            var polled    = await lc.PollOpenADR();

            Assert.Multiple(() => {

                Assert.That(answer,                                              Does.Not.Contain(Secret));
                Assert.That(get,                                                 Does.Not.Contain(Secret));
                Assert.That(JObject.Parse(get)["credentials"]?.Value<Boolean>("hasSecret"), Is.True);
                Assert.That(JObject.Parse(get).Value<Double>("pollEvery"),       Is.EqualTo(600));

                Assert.That(File.ReadAllText(lc.ConfigFile.Path),                Does.Not.Contain(Secret));
                Assert.That(File.ReadAllText(lc.OpenADRLogin.Path),              Does.Contain(Secret));

                Assert.That(polled,                                              Is.Null);

            });

        }

        #endregion

        #region AnEventOfTwoIntervalsInWatts()

        /// <summary>
        /// An event in W is taken in kW, and an event of two intervals is two
        /// limits, one after the other.
        /// </summary>
        [Test]
        public async Task AnEventOfTwoIntervalsInWatts()
        {

            var start = DateTimeOffset.UtcNow.AddMinutes(-1);

            vtn.Add(JObject.Parse($$"""
                {
                    "objectType":          "EVENT",
                    "id":                  "two-steps",
                    "programID":           "{{FakeVTN.ProgramID}}",
                    "targets":             [ { "type": "VEN_NAME", "values": [ "{{VENName}}" ] } ],
                    "payloadDescriptors":  [ { "objectType": "EVENT_PAYLOAD_DESCRIPTOR", "payloadType": "IMPORT_CAPACITY_LIMIT", "units": "W" } ],
                    "intervalPeriod":      { "start": "{{start:o}}", "duration": "PT15M" },
                    "intervals": [
                        { "id": 0, "payloads": [ { "type": "IMPORT_CAPACITY_LIMIT", "values": [ 50000 ] } ] },
                        { "id": 1, "payloads": [ { "type": "IMPORT_CAPACITY_LIMIT", "values": [ 25000 ] } ] }
                    ]
                }
                """));

            var lc = await AController();

            Assert.That(await lc.PollOpenADR(), Is.Null);

            var limits = lc.GridLimits;

            Assert.Multiple(() => {
                Assert.That(limits.Select(limit => limit.KW),         Is.EqualTo(new[] { 50.0, 25.0 }));
                Assert.That(limits[1].Start,                          Is.EqualTo(limits[0].End));
                Assert.That(limits[1].End - limits[1].Start,          Is.EqualTo(TimeSpan.FromMinutes(15)));
                Assert.That(lc.ImportLimitNow?.KW,                    Is.EqualTo(50));
                Assert.That(vtn.Reports,                              Has.Count.EqualTo(1));
            });

        }

        #endregion

        #region WhatTheConfigurationRefuses()

        [TestCase("""{ "enabled": true, "clientID": "lc-1" }""",                                       "vtnURL")]
        [TestCase("""{ "enabled": true, "vtnURL": "https://grid.example/openadr3/" }""",               "clientID")]
        [TestCase("""{ "vtnURL": "ftp://grid.example/openadr3/" }""",                                  "HTTP URL")]
        [TestCase("""{ "vtnURL": "https://grid.example/openadr3/?x=1" }""",                            "no query")]
        [TestCase("""{ "clientID": "lc 1" }""",                                                        "no spaces")]
        [TestCase("""{ "pollEvery": 1 }""",                                                            "pollEvery")]
        public void WhatTheConfigurationRefuses(String JSON, String Said)
        {

            Assert.That(OpenADRConfiguration.TryParse(JObject.Parse(JSON), out _, out var error), Is.False);
            Assert.That(error, Does.Contain(Said));

        }

        [Test]
        public void TheVTNURLEndsWithASlash()
        {

            Assert.That(OpenADRConfiguration.TryParse(JObject.Parse("""{ "vtnURL": "https://grid.example/openadr3" }"""), out var configuration, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(configuration!.VTNURL,      Is.EqualTo("https://grid.example/openadr3/"));
                Assert.That(configuration.InTheClear,   Is.False);
                Assert.That(new OpenADRConfiguration(VTNURL: "http://grid.example/openadr3/").InTheClear, Is.True);
                Assert.That(new OpenADRConfiguration(VTNURL: "http://127.0.0.1:2352/openadr3/").InTheClear, Is.False);
            });

        }

        #endregion


        #region (private static) SignedIn(Controller)

        private static async Task<HttpClient> SignedIn(LocalController Controller)
        {

            var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) {
                           BaseAddress = new Uri(Controller.WebInterfaceURL.ToString())
                       };

            var response = await http.PostAsync($"{LocalController.ExtAPIPath.ToString().TrimEnd('/')}/login",
                                                new FormUrlEncodedContent([
                                                    new KeyValuePair<String, String>("login",     LocalController.DefaultAdminUser),
                                                    new KeyValuePair<String, String>("password",  Controller.GeneratedPassword!)
                                                ]));

            Assert.That(response.IsSuccessStatusCode, Is.True, $"Signing in failed with {(Int32) response.StatusCode}.");

            return http;

        }

        #endregion

    }

}
