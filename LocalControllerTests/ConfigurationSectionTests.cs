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

using NUnit.Framework;

using cloud.charging.open.LocalController.Configuration;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The sections of the configuration file, read one at a time.
    /// </summary>
    /// <remarks>
    /// Three rules run through all of them, and most of these tests are one of
    /// the three: absent is not an error and comes back as null, so the caller
    /// keeps whatever it had; present but of the wrong kind is an error and
    /// never a silent null; and an explicit JSON null counts as absent, so a
    /// page may send a whole object with the fields it does not touch left
    /// empty.
    /// </remarks>
    public class ConfigurationSectionTests
    {

        #region DNS: an absent field leaves what the controller had

        [Test]
        public void AnAbsentDNSFieldIsNull()
        {

            Assert.That(DNSConfiguration.TryParse([], out var dns, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(dns!.Enabled,       Is.Null);
                Assert.That(dns.Servers,        Is.Null);
                Assert.That(dns.QueryTimeout,   Is.Null);
                Assert.That(dns.UseCache,       Is.Null);
            });

        }

        #endregion

        #region DNS: a field of the wrong kind is an error

        [Test]
        public void ADNSFieldOfTheWrongKindIsRefused()
        {

            Assert.Multiple(() => {

                Assert.That(DNSConfiguration.TryParse(new JObject(new JProperty("useCache", "yes")),
                                                      out _, out var text), Is.False);
                Assert.That(text, Does.Contain("dns.useCache"));

                Assert.That(DNSConfiguration.TryParse(new JObject(new JProperty("maxRetries", "many")),
                                                      out _, out var number), Is.False);
                Assert.That(number, Does.Contain("dns.maxRetries"));

                Assert.That(DNSConfiguration.TryParse(new JObject(new JProperty("servers", "8.8.8.8")),
                                                      out _, out var list), Is.False);
                Assert.That(list, Does.Contain("dns.servers"));

            });

        }

        #endregion

        #region DNS: an explicit null is the same as absent

        [Test]
        public void AnExplicitNullDNSFieldCountsAsAbsent()
        {

            var json = new JObject(
                           new JProperty("useCache", JValue.CreateNull()),
                           new JProperty("enabled",  true)
                       );

            Assert.That(DNSConfiguration.TryParse(json, out var dns, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(dns!.UseCache, Is.Null);
                Assert.That(dns.Enabled,   Is.True);
            });

        }

        #endregion

        #region DNS: the name servers

        [Test]
        public void ANameServerMayBeATextOrAnObject()
        {

            var json = new JObject(
                           new JProperty("servers", new JArray(
                               "8.8.8.8",
                               new JObject(
                                   new JProperty("address",   "9.9.9.9"),
                                   new JProperty("port",      853),
                                   new JProperty("transport", "TLS")
                               )
                           ))
                       );

            Assert.That(DNSConfiguration.TryParse(json, out var dns, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(dns!.Servers,                      Has.Count.EqualTo(2));
                Assert.That(dns.Servers![0].IPAddress?.ToString(), Is.EqualTo("8.8.8.8"));
                Assert.That(dns.Servers[1].Port.ToUInt16(),     Is.EqualTo(853));
            });

        }

        #endregion

        #region DNS: what is not a name server at all

        [Test]
        public void SomethingThatIsNeitherAnAddressNorANameIsRefused()
        {

            var json = new JObject(new JProperty("servers", new JArray("not a host name!")));

            Assert.That(DNSConfiguration.TryParse(json, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("dns.servers"));

        }

        #endregion

        #region DNS: too many name servers

        /// <summary>
        /// Not a rule of DNS: a query goes to all of them at once, and a
        /// hundred would be a hundred packets for every name looked up.
        /// </summary>
        [Test]
        public void MoreNameServersThanAControllerMayHaveAreRefused()
        {

            var tooMany = new JArray(Enumerable.Range(1, DNSConfiguration.MaxServers + 1).
                                                Select(i => (JToken) $"10.0.0.{i}"));

            Assert.That(DNSConfiguration.TryParse(new JObject(new JProperty("servers", tooMany)),
                                                  out _, out var error), Is.False);

            Assert.That(error, Does.Contain($"{DNSConfiguration.MaxServers}"));

        }

        #endregion

        #region DNS: what is not written down stays unwritten

        /// <summary>
        /// So the file keeps saying "the system decides" rather than freezing
        /// today's system default.
        /// </summary>
        [Test]
        public void ADNSSectionWritesOnlyWhatItWasTold()
        {

            var written = new DNSConfiguration(UseCache: true).ToJSON();

            Assert.Multiple(() => {
                Assert.That(written.Value<Boolean>("useCache"), Is.True);
                Assert.That(written["enabled"],                 Is.Null);
                Assert.That(written["maxRetries"],              Is.Null);
                Assert.That(written.Properties().Count(),       Is.EqualTo(1));
            });

        }

        #endregion


        #region NTS: a host name that is not one

        [Test]
        public void ATimeServerThatIsNotAHostNameIsRefused()
        {

            Assert.That(NTSConfiguration.TryParse(new JObject(new JProperty("hostname", "not a host name!")),
                                                  out _, out var error), Is.False);

            Assert.That(error, Does.Contain("nts.hostname"));

        }

        #endregion

        #region NTS: a port that is not one

        [Test]
        public void ATimeServerPortOutsideItsRangeIsRefused()
        {

            Assert.Multiple(() => {

                Assert.That(NTSConfiguration.TryParse(new JObject(new JProperty("ntpPort", 0)),
                                                      out _, out var zero), Is.False,
                            "Port 0 means 'any free port' to a listener and nothing at all to a client.");
                Assert.That(zero, Does.Contain("nts.ntpPort"));

                Assert.That(NTSConfiguration.TryParse(new JObject(new JProperty("ntsKEPort", 70000)),
                                                      out _, out var high), Is.False);
                Assert.That(high, Does.Contain("nts.ntsKEPort"));

            });

        }

        #endregion

        #region NTS: the spans that decide what "legal" needs

        [Test]
        public void TheLegalTimeSpansAreReadAsSeconds()
        {

            var json = new JObject(
                           new JProperty("checkEverySeconds",          600),
                           new JProperty("legalTimeToleranceSeconds",  0.25),
                           new JProperty("legalTimeMaxAgeSeconds",     1800),
                           new JProperty("legalTimeAuthority",         "PTB")
                       );

            Assert.That(NTSConfiguration.TryParse(json, out var nts, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(nts!.CheckEvery,          Is.EqualTo(TimeSpan.FromMinutes(10)));
                Assert.That(nts.LegalTimeTolerance,   Is.EqualTo(TimeSpan.FromMilliseconds(250)));
                Assert.That(nts.LegalTimeMaxAge,      Is.EqualTo(TimeSpan.FromMinutes(30)));
                Assert.That(nts.LegalTimeAuthority,   Is.EqualTo("PTB"));
            });

        }

        #endregion

        #region NTS: a span outside what a span may be

        [Test]
        public void ASpanOutsideItsRangeIsRefused()
        {

            Assert.Multiple(() => {

                Assert.That(NTSConfiguration.TryParse(new JObject(new JProperty("timeoutSeconds", 0)),
                                                      out _, out var tooSmall), Is.False);
                Assert.That(tooSmall, Does.Contain("nts.timeoutSeconds"));

                Assert.That(NTSConfiguration.TryParse(new JObject(new JProperty("timeoutSeconds", NTSConfiguration.MaxTimeoutSeconds + 1)),
                                                      out _, out var tooBig), Is.False);
                Assert.That(tooBig, Does.Contain("nts.timeoutSeconds"));

            });

        }

        #endregion


        #region OCPP: the defaults, and what the file may say instead

        [Test]
        public void AnOCPPSectionSaysNothingUntilItIsToldSomething()
        {

            Assert.That(OCPPConfiguration.TryParse([], out var ocpp, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(ocpp!.NodeId,          Is.Null);
                Assert.That(ocpp.VendorName,       Is.Null);
                Assert.That(ocpp.Model,            Is.Null);
                // Which is what lets the controller fall back to its own.
                Assert.That(OCPPConfiguration.DefaultNodeId, Is.Not.Empty);
            });

        }

        #endregion

        #region OCPP: an identification with a space in it

        /// <summary>
        /// A networking node identification is what a CSMS addresses this
        /// controller by, and a space in one is a typing mistake rather than a
        /// name.
        /// </summary>
        [Test]
        public void AnIdentificationWithASpaceIsRefused()
        {

            Assert.That(OCPPConfiguration.TryParse(new JObject(new JProperty("nodeId", "lc 001")),
                                                   out _, out var error), Is.False);

            Assert.That(error, Does.Contain("ocpp.nodeId"));

        }

        #endregion

        #region OCPP: a field longer than the protocol allows

        [Test]
        public void AFieldLongerThanOCPPAllowsIsRefused()
        {

            var tooLong = new String('x', OCPPConfiguration.MaxModelLength + 1);

            Assert.That(OCPPConfiguration.TryParse(new JObject(new JProperty("model", tooLong)),
                                                   out _, out var error), Is.False);

            Assert.That(error, Does.Contain("ocpp.model"));

        }

        #endregion

        #region OCPP: what it says about itself

        [Test]
        public void AnOCPPSectionNamesItselfReadably()
        {

            var ocpp = new OCPPConfiguration(NodeId: "lc042", VendorName: "ACME", Model: "Box");

            Assert.Multiple(() => {
                Assert.That(ocpp.ToString(),                  Does.Contain("lc042"));
                Assert.That(ocpp.ToString(),                  Does.Contain("ACME"));
                Assert.That(ocpp.ToJSON().Value<String>("nodeId"), Is.EqualTo("lc042"));
                Assert.That(ocpp.ToJSON()["serialNumber"],    Is.Null,
                            "A field nobody set was written to the file anyway.");
            });

        }

        #endregion


        #region The whole document

        [Test]
        public void ADocumentReportsWhichSectionsSpoke()
        {

            var json = new JObject(
                           new JProperty("dns",  new JObject(new JProperty("enabled", true))),
                           new JProperty("ocpp", new JObject(new JProperty("nodeId",  "lc007")))
                       );

            Assert.That(ControllerConfiguration.TryParse(json, out var configuration, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(configuration!.DNS,        Is.Not.Null);
                Assert.That(configuration.NTS,         Is.Null);
                Assert.That(configuration.OCPP,        Is.Not.Null);
                Assert.That(configuration.IsEmpty,     Is.False);
                Assert.That(configuration.ToString(),  Does.Contain("DNS"));
                Assert.That(configuration.ToString(),  Does.Contain("lc007"));
            });

        }

        #endregion

        #region An empty document says so

        [Test]
        public void AnEmptyDocumentSaysNothingIsConfigured()
        {

            Assert.That(ControllerConfiguration.TryParse([], out var configuration, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(configuration!.IsEmpty,    Is.True);
                Assert.That(configuration.ToString(),  Is.EqualTo("nothing configured"));
            });

        }

        #endregion

    }

}
