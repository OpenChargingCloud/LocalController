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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.LocalController.Configuration;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// What the "dns" section may say about the name servers - and what it may
    /// not, said as a sentence about the file rather than as an exception.
    /// </summary>
    /// <remarks>
    /// "udp://213.133.98.98:53" is how the log names a name server, and not a
    /// form this section has ever taken - here, or in the vehicle, the charging
    /// station and the energy meter, whose sections it shares so that one file
    /// can be copied between them. Written into the file anyway, it stopped a
    /// local controller at its start with an ArgumentException out of Hermod's
    /// IPAddress.TryParse, which named neither the file nor the key; sent to
    /// the DNS page's API, it threw out of the same call.
    /// </remarks>
    public class DNSConfigurationTests
    {

        #region Data

        private String directory = default!;

        private String ConfigurationPath
            => Path.Combine(directory, ControllerConfigFile.DefaultFileName);

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("dns");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void RemoveTheDirectory()
            => TestControllers.Remove(directory);

        #endregion


        #region AnAddressIsOneNameServerOverUDP()

        /// <summary>
        /// The short form: an address, which is a name server asked over UDP on
        /// port 53.
        /// </summary>
        [Test]
        public void AnAddressIsOneNameServerOverUDP()
        {

            var section = JObject.Parse("""{ "enabled": true, "servers": [ "192.168.1.1" ] }""");

            Assert.That(DNSConfiguration.TryParse(section, out var read, out var error),  Is.True,  error);

            var server = read!.Servers!.Single();

            Assert.Multiple(() => {
                Assert.That(read.Enabled,                  Is.True);
                Assert.That(server.IPAddress?.ToString(),  Is.EqualTo("192.168.1.1"));
                Assert.That(server.Port.ToUInt16(),        Is.EqualTo(53));
                Assert.That(server.Transport,              Is.EqualTo(DNSTransport.UDP));
            });

        }

        #endregion

        #region TheLongFormIsWhatThePageWritesBack()

        /// <summary>
        /// A server with more to say about it is an object, read and written
        /// back as it was - which is what makes it the form the DNS page saves
        /// the list in.
        /// </summary>
        [Test]
        public void TheLongFormIsWhatThePageWritesBack()
        {

            var entry = JObject.Parse("""{ "address": "192.168.1.1", "port": 53, "transport": "UDP", "queryTimeoutSeconds": 2 }""");

            Assert.That(DNSConfiguration.TryParseServer(entry, out var server, out var error),  Is.True,  error);

            var written = DNSConfiguration.ServerJSON(server!);

            Assert.Multiple(() => {
                Assert.That(written.Value<String>("address"),              Is.EqualTo("192.168.1.1"));
                Assert.That(written.Value<Int32> ("port"),                 Is.EqualTo(53));
                Assert.That(written.Value<String>("transport"),            Is.EqualTo("UDP"));
                Assert.That(written.Value<Double>("queryTimeoutSeconds"),  Is.EqualTo(2));
            });

        }

        #endregion

        #region TheFormTheLogNamesAServerInIsRefusedWithASentence(Entry)

        /// <summary>
        /// How the log names a name server, and an address with its port: not
        /// forms the section takes, so refused - with the entry named, and not
        /// with an exception out of the parser, which is what all three were.
        /// </summary>
        [TestCase("udp://213.133.98.98:53")]
        [TestCase("udp://[2a01:4f8:0:1::add:1010]:53")]
        [TestCase("213.133.98.98:53")]
        public void TheFormTheLogNamesAServerInIsRefusedWithASentence(String Entry)
        {

            var                section  = new JObject(new JProperty("servers", new JArray(Entry)));
            var                parsed   = true;
            DNSConfiguration?  read     = null;
            String?            error    = null;

            Assert.That(() => parsed = DNSConfiguration.TryParse(section, out read, out error),  Throws.Nothing);

            Assert.Multiple(() => {
                Assert.That(parsed,  Is.False);
                Assert.That(read,    Is.Null);
                Assert.That(error,   Does.Contain("'dns.servers'").And.Contain(Entry));
            });

        }

        #endregion

        #region AControllerWhoseFileSaysSoStopsWithASentence()

        /// <summary>
        /// And at a start: the controller stops over the file the way it stops
        /// over any file it cannot read, saying what is wrong and where - rather
        /// than with an ArgumentException, which is how it stopped before.
        /// </summary>
        /// <remarks>
        /// Built and never started: the constructor is what reads the file.
        /// </remarks>
        [Test]
        public void AControllerWhoseFileSaysSoStopsWithASentence()
        {

            var file    = JObject.Parse("""{ "dns": { "servers": [ "udp://213.133.98.98:53" ] }, "nts": { "enabled": false } }""");

            var problem = Assert.Throws<InvalidOperationException>(() => TestControllers.New(directory, file));

            Assert.That(problem?.Message,  Does.Contain("udp://213.133.98.98:53").And.Contain(ConfigurationPath));

        }

        #endregion

    }


    /// <summary>
    /// The same refusal, asked of a running local controller over the API the
    /// DNS page saves through.
    /// </summary>
    public class DNSConfigurationAPITests : ALocalControllerTests
    {

        #region TheFormTheLogNamesAServerInIsRefusedOverTheAPI()

        /// <summary>
        /// Refused with a 400 and the sentence the file would have got, and
        /// neither the running client nor the file touched - where the same
        /// request used to throw out of the parser in the middle of the handler.
        /// </summary>
        [Test]
        public async Task TheFormTheLogNamesAServerInIsRefusedOverTheAPI()
        {

            using var http = await SignedIn();

            var serversBefore = Controller.DNSClient.DNSServers.Select(server => server.ToString()).ToArray();
            var fileBefore    = File.Exists(Controller.ConfigFile.Path) ? File.ReadAllText(Controller.ConfigFile.Path) : null;

            var response      = await http.PutAsync("/api/v1/configuration/dns",
                                                    JSONBody(new JProperty("servers", new JArray("udp://213.133.98.98:53"))));

            var body          = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() => {

                Assert.That(response.StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), body);

                Assert.That(body,  Does.Contain("'dns.servers'").And.Contain("udp://213.133.98.98:53"));

                Assert.That(Controller.DNSClient.DNSServers.Select(server => server.ToString()),
                            Is.EqualTo(serversBefore),
                            "The refused request changed the name servers anyway.");

                Assert.That(File.Exists(Controller.ConfigFile.Path) ? File.ReadAllText(Controller.ConfigFile.Path) : null,
                            Is.EqualTo(fileBefore),
                            "The refused request was written into the file anyway.");

            });

        }

        #endregion

    }

}
