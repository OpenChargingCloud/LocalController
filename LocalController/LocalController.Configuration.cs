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

using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Norn.NTS;
using org.GraphDefined.Vanaheimr.Norn.Monitoring;
using org.GraphDefined.Vanaheimr.Norn.TimeSync;

using cloud.charging.open.LocalController.Configuration;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// What the Configuration pages of the web interface read and write.
    /// </summary>
    /// <remarks>
    /// Every change here takes effect at once and is written to the
    /// configuration file, in that order of importance and in the opposite
    /// order of doing: the file is written first, because a change that was
    /// applied but not written down is a change that disappears at the next
    /// start without anybody noticing, and that is the worse of the two
    /// failures. A file that was written but could not be applied is the lesser
    /// one - it says so loudly, and a restart makes it true.
    ///
    /// Nothing here decides who may call it. That is the API's business, and it
    /// asks before it calls: see the permissions in
    /// <see cref="Web.UserRole"/>. Nothing in this file knows what a permission
    /// is, which is what keeps the two answerable separately: what a change
    /// does, and who may ask for it.
    /// </remarks>
    public partial class LocalController
    {

        #region Data

        /// <summary>
        /// How the last time synchronisation went, as the web interface reads
        /// it, or null while none has been asked for.
        /// </summary>
        private JObject? lastTimeSync;

        /// <summary>
        /// The record types the DNS test offers, of the several hundred that
        /// exist. Anything else may still be typed - this is the list of what
        /// somebody is likely to want, not of what is allowed.
        /// </summary>
        private static readonly DNSResourceRecordTypes[] commonRecordTypes = [
            DNSResourceRecordTypes.A,
            DNSResourceRecordTypes.AAAA,
            DNSResourceRecordTypes.CNAME,
            DNSResourceRecordTypes.MX,
            DNSResourceRecordTypes.NS,
            DNSResourceRecordTypes.TXT,
            DNSResourceRecordTypes.SOA,
            DNSResourceRecordTypes.SRV,
            DNSResourceRecordTypes.PTR,
            DNSResourceRecordTypes.CAA,
            DNSResourceRecordTypes.TLSA,
            DNSResourceRecordTypes.DNSKEY,
            DNSResourceRecordTypes.DS,
            DNSResourceRecordTypes.HTTPS,
            DNSResourceRecordTypes.SVCB
        ];

        #endregion


        #region DNS

        #region DNSConfigurationJSON()

        /// <summary>
        /// How this local controller resolves names.
        /// </summary>
        public JObject DNSConfigurationJSON()

            => new (

                   new JProperty("enabled",           DNSEnabled),

                   // The servers this local controller would ask, which is not the same
                   // as the ones the client holds: switched off, it holds none.
                   new JProperty("servers",           new JArray(
                       configuredDNSServers.Select(DNSConfiguration.ServerJSON)
                   )),

                   new JProperty("settings",          new JObject(
                       new JProperty("queryTimeoutSeconds",  dnsClient.QueryTimeout.TotalSeconds),
                       new JProperty("recursionDesired",     dnsClient.RecursionDesired),
                       new JProperty("useCache",             dnsClient.UseCache),
                       new JProperty("dnssecOK",             dnsClient.DnssecOK),
                       new JProperty("followCNAMEs",         dnsClient.FollowCNAMEs),
                       new JProperty("maxCNAMEFollows",      dnsClient.MaxCNAMEFollows),
                       new JProperty("maxRetries",           dnsClient.MaxRetries)
                   )),

                   // What was decided when the client was made and is not on
                   // offer here; shown so that the page does not read as if
                   // these were the only settings there are.
                   new JProperty("fixed",             new JObject(
                       new JProperty("udpPayloadSize",       dnsClient.UDPPayloadSize),
                       new JProperty("ednsOptions",          dnsClient.EDNSOptions.Count),
                       new JProperty("clientSubnet",         dnsClient.ClientSubnet is null
                                                                 ? null
                                                                 : $"{dnsClient.ClientSubnet.Address}/{dnsClient.ClientSubnet.SourcePrefixLength}"),
                       new JProperty("cacheCleanUpEvery",    dnsClient.DNSCache.CleanUpEvery.    ToString()),
                       new JProperty("negativeCacheTTL",     dnsClient.DNSCache.NegativeCacheTTL.ToString())
                   )),

                   new JProperty("limits",            new JObject(
                       new JProperty("maxServers",           DNSConfiguration.MaxServers),
                       new JProperty("maxQueryTimeout",      DNSConfiguration.MaxQueryTimeoutSeconds),
                       new JProperty("transports",           new JArray(Enum.GetNames<DNSTransport>())),
                       new JProperty("recordTypes",          new JArray(commonRecordTypes.Select(recordType => recordType.ToString())))
                   )),

                   new JProperty("file",              ConfigFile.Path)

               );

        #endregion

        #region TryUpdateDNSConfiguration(JSON, out Error)

        /// <summary>
        /// Change how this local controller resolves names, at once and for everything
        /// that was handed its DNS client.
        /// </summary>
        /// <remarks>
        /// The request has the same shape as the "dns" section of the
        /// configuration file, on purpose: one vocabulary for the file and for
        /// the web interface means one parser, and nothing that is expressible
        /// in one and not in the other.
        ///
        /// What the request does not mention is not changed and not erased from
        /// the file - a page that only offers the checkboxes may send only the
        /// checkboxes without taking the name servers with it.
        /// </remarks>
        public Boolean TryUpdateDNSConfiguration(JObject                           JSON,
                                                 [NotNullWhen(false)] out String?  Error)
        {

            if (!DNSConfiguration.TryParse(JSON, out var configuration, out Error))
                return false;

            if (configuration.Servers is { Count: 0 })
            {
                Error = "A local controller that resolves no names cannot reach the CSMS above it. Switch name resolution off instead of emptying the list.";
                return false;
            }

            // Under the same lock as every other change to this controller:
            // writing a section is a read, a change and a write of one file,
            // and two browsers saving different sections at the same moment
            // would otherwise leave one of the two changes in neither.
            reconfigureLock.Wait();

            try
            {

                if (!ConfigFile.TryMergeSection(DNSConfiguration.SectionName, configuration.ToJSON(), out Error))
                    return false;

                ApplyDNSConfiguration(configuration);

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private) ApplyDNSConfiguration(Configuration)

        /// <summary>
        /// Put a DNS section into effect. What it does not mention is left as
        /// it is.
        /// </summary>
        private void ApplyDNSConfiguration(DNSConfiguration Configuration)
        {

            var changed = new List<String>();

            if (Configuration.Servers is not null &&
                !configuredDNSServers.SequenceEqual(Configuration.Servers))
            {
                configuredDNSServers = Configuration.Servers;
                changed.Add($"servers = {String.Join(", ", configuredDNSServers)}");
            }

            if (Configuration.Enabled.HasValue && DNSEnabled != Configuration.Enabled.Value)
            {
                DNSEnabled = Configuration.Enabled.Value;
                changed.Add(DNSEnabled ? "switched on" : "switched off");
            }

            // Always, not only when one of the two above changed: the client
            // must end up holding exactly the servers this local controller means it to
            // hold, and working that out from which halves changed is how the
            // two drift apart.
            dnsClient.SetDNSServers(DNSEnabled ? configuredDNSServers : []);

            if (Configuration.QueryTimeout.HasValue && dnsClient.QueryTimeout != Configuration.QueryTimeout.Value)
            {
                dnsClient.QueryTimeout = Configuration.QueryTimeout.Value;
                changed.Add($"query timeout = {dnsClient.QueryTimeout}");
            }

            if (Configuration.RecursionDesired.HasValue && dnsClient.RecursionDesired != Configuration.RecursionDesired)
            {
                dnsClient.RecursionDesired = Configuration.RecursionDesired;
                changed.Add($"recursion desired = {Configuration.RecursionDesired}");
            }

            if (Configuration.UseCache.HasValue && dnsClient.UseCache != Configuration.UseCache.Value)
            {
                dnsClient.UseCache = Configuration.UseCache.Value;
                changed.Add($"use cache = {dnsClient.UseCache}");
            }

            if (Configuration.DnssecOK.HasValue && dnsClient.DnssecOK != Configuration.DnssecOK.Value)
            {
                dnsClient.DnssecOK = Configuration.DnssecOK.Value;
                changed.Add($"DNSSEC OK = {dnsClient.DnssecOK}");
            }

            if (Configuration.FollowCNAMEs.HasValue && dnsClient.FollowCNAMEs != Configuration.FollowCNAMEs.Value)
            {
                dnsClient.FollowCNAMEs = Configuration.FollowCNAMEs.Value;
                changed.Add($"follow CNAMEs = {dnsClient.FollowCNAMEs}");
            }

            if (Configuration.MaxCNAMEFollows.HasValue && dnsClient.MaxCNAMEFollows != Configuration.MaxCNAMEFollows.Value)
            {
                dnsClient.MaxCNAMEFollows = Configuration.MaxCNAMEFollows.Value;
                changed.Add($"max CNAME follows = {dnsClient.MaxCNAMEFollows}");
            }

            if (Configuration.MaxRetries.HasValue && dnsClient.MaxRetries != Configuration.MaxRetries.Value)
            {
                dnsClient.MaxRetries = Configuration.MaxRetries.Value;
                changed.Add($"max retries = {dnsClient.MaxRetries}");
            }

            if (changed.Count > 0)
                Log.Notice($"DNS configuration changed: {String.Join(", ", changed)}.", "dns", "config");

        }

        #endregion

        #endregion


        #region NTS

        #region NTSConfigurationJSON()

        /// <summary>
        /// Where this local controller gets the time from, and how the key exchange
        /// behind it is doing.
        /// </summary>
        public JObject NTSConfigurationJSON()
        {

            var pool = ntsClient.CookiePoolDiagnostics;
            var last = ntsClient.LastNTSKEResponse;

            return new JObject(

                       new JProperty("enabled",      NTSEnabled),

                       new JProperty("server",       new JObject(
                           new JProperty("hostname",              ntsClient.Hostname.ToString()),
                           new JProperty("ntsKEPort",             ntsClient.NTSKE_Port.ToUInt16()),
                           new JProperty("ntpPort",               ntsClient.NTP_Port.  ToUInt16()),
                           new JProperty("ipVersionPreference",   ntsClient.IPVersionPreference.ToString()),
                           new JProperty("clientId",              ntsClient.Id)
                       )),

                       // What may be changed about the group, as it is in
                       // effect. The quorum is the one this controller was
                       // told; the group's own, below, can be lower when it has
                       // fewer servers switched on.
                       new JProperty("settings",     new JObject(
                           new JProperty("timeoutSeconds",        ntsClient.Timeout?.TotalSeconds),
                           new JProperty("checkEverySeconds",     TimeCheckEvery.TotalSeconds),
                           new JProperty("minServers",            ntsQuorum),
                           new JProperty("maxDeviationSeconds",   timeSources.MaxDeviation.TotalSeconds)
                       )),

                       new JProperty("cookies",      new JObject(
                           new JProperty("available",             pool.AvailableCookieCount),
                           new JProperty("maxPoolSize",           pool.MaxCookiePoolSize),
                           new JProperty("lowWatermark",          pool.LowWatermark),
                           new JProperty("seeded",                pool.SeededCookieCount),
                           new JProperty("received",              pool.CookiesReceived),
                           new JProperty("consumed",              pool.CookiesConsumed),
                           new JProperty("dropped",               pool.DroppedCookieCount),
                           new JProperty("isLow",                 pool.IsLow),
                           new JProperty("isEmpty",               pool.IsEmpty),
                           new JProperty("isFull",                pool.IsFull)
                       )),

                       new JProperty("policy",       new JObject(
                           new JProperty("targetCookieCount",             ntsClient.CookiePoolPolicy.TargetCookieCount),
                           new JProperty("maxPlaceholders",               ntsClient.CookiePoolPolicy.MaxPlaceholders),
                           new JProperty("renegotiateWhenExhausted",      ntsClient.CookiePoolPolicy.RenegotiateWhenExhausted),
                           new JProperty("minimumRenegotiationInterval",  ntsClient.CookiePoolPolicy.MinimumRenegotiationInterval.ToString())
                       )),

                       new JProperty("keyExchange",  new JObject(
                           new JProperty("automatic",                 ntsClient.AutomaticKeyExchanges),
                           new JProperty("aeadAlgorithms",            new JArray(ntsClient.OfferedAEADAlgorithms.Select(algorithm => algorithm.ToString()))),
                           new JProperty("compliantExporterContext",  ntsClient.CompliantAES128GCMSIVExporterContext),
                           new JProperty("lastExchange",              last is null
                                                                          ? null
                                                                          : new JObject(
                                                                                new JProperty("error",     last.ErrorMessage),
                                                                                new JProperty("warnings",  new JArray(last.WarningMessages)),
                                                                                new JProperty("servers",   new JArray(last.NTPv4ServerNames))
                                                                            ))
                       )),

                       // What the group is actually doing, which is what checks
                       // this controller's clock. The single client reported
                       // above is the one the detailed test configures itself
                       // from, and its cookie pool is not what a check spends.
                       //
                       // Every server, in the order they were configured, the
                       // switched-off ones included: this is the list the page
                       // edits and sends back whole, and a server missing from it
                       // because it was switched off would be deleted by the next
                       // save of anything else.
                       new JProperty("timeSources",  new JArray(
                           timeSources.Sources.Select(source => {

                               var held = timeEngine.KeyExchanges.TryGetValue(source.Hostname, out var state) ? state : null;

                               return new JObject(
                                          new JProperty("hostname",       source.Hostname.ToString()),
                                          new JProperty("priority",       source.Priority),
                                          new JProperty("ntsKEPort",      source.NTSKEPort.ToUInt16()),
                                          new JProperty("ntpPort",        source.NTPPort.  ToUInt16()),
                                          new JProperty("enabled",        source.Enabled),
                                          new JProperty("cookies",        held?.RemainingCookies),
                                          new JProperty("lastExchange",   held?.LastRefreshed.ToString("o")),
                                          new JProperty("aeadAlgorithm",  held?.NTSKEResponse?.AEADAlgorithm.ToString())
                                      );

                           })
                       )),

                       new JProperty("group",        new JObject(
                           new JProperty("name",                 timeSources.Name),
                           new JProperty("minServers",           timeSources.MinServers),
                           new JProperty("maxDeviationSeconds",  timeSources.MaxDeviation.TotalSeconds)
                       )),

                       new JProperty("lastSync",     lastTimeSync),

                       new JProperty("limits",       new JObject(
                           new JProperty("maxTimeout",         NTSConfiguration.MaxTimeoutSeconds),
                           new JProperty("minCheckEvery",      NTSConfiguration.MinCheckEverySeconds),
                           new JProperty("maxCheckEvery",      NTSConfiguration.MaxCheckEverySeconds),
                           new JProperty("minDeviation",       NTSConfiguration.MinDeviationSeconds),
                           new JProperty("maxDeviation",       NTSConfiguration.MaxDeviationSeconds),
                           new JProperty("defaultNTSKEPort",   NTSClient.DefaultNTSKE_Port.ToUInt16()),
                           new JProperty("defaultNTPPort",     NTSClient.DefaultNTP_Port.  ToUInt16())
                       )),

                       new JProperty("file",         ConfigFile.Path)

                   );

        }

        #endregion

        #region TryUpdateNTSConfiguration(JSON, out Error)

        /// <summary>
        /// Change where this local controller reads the time.
        /// </summary>
        /// <remarks>
        /// Pointing the local controller at another server replaces the client rather
        /// than reconfiguring it: the cookies and the keys an NTS client holds
        /// were issued by the host it was made for, and carrying them to a
        /// different one would at best fail and at worst send one server the
        /// key material of another.
        /// </remarks>
        public Boolean TryUpdateNTSConfiguration(JObject                           JSON,
                                                 [NotNullWhen(false)] out String?  Error)
        {

            if (!NTSConfiguration.TryParse(JSON, out var configuration, out Error))
                return false;

            reconfigureLock.Wait();

            try
            {

                // Before the file, so that what is refused is not written down
                // either - and inside the lock, because the servers a quorum on
                // its own is checked against are the ones in effect.
                if (!TryCheckNTSQuorum(configuration, out Error))
                    return false;

                // And the file as the next start will read it. Each half can
                // be fine and the two together not: the quorum the file holds
                // and a list saved now that is shorter than it would be a
                // section the next start refuses, and a controller that does
                // not start because of a save that was accepted.
                if (!ConfigFile.TryPreviewSection(NTSConfiguration.SectionName, configuration.ToJSON(), out var merged, out Error))
                    return false;

                if (!NTSConfiguration.TryParse(merged, out _, out var mergedError))
                {
                    Error = $"{mergedError} Nothing was changed.";
                    return false;
                }

                if (!ConfigFile.TryMergeSection(NTSConfiguration.SectionName, configuration.ToJSON(), out Error))
                    return false;

                ApplyNTSConfiguration(configuration);

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private static) Described(Group)

        /// <summary>
        /// A group of time servers as a log line names it: every server in the
        /// order configured, with whatever about it is not the usual.
        /// </summary>
        /// <remarks>
        /// All of them, and all of that, because this is also what a change is
        /// found by. It used to be the names of the servers switched on, in the
        /// order they are asked: a server given another priority or a port of
        /// its own was a change the log book never heard of, and one switched
        /// off simply went missing from the line.
        /// </remarks>
        private static String Described(TimeSourceGroup Group)

            => String.Join(", ", Group.Sources.Select(source => {

                   var unusual = new List<String>();

                   if (source.Priority  != 0)                            unusual.Add($"priority {source.Priority}");
                   if (source.NTSKEPort != NTSClient.DefaultNTSKE_Port)  unusual.Add($"NTS-KE port {source.NTSKEPort}");
                   if (source.NTPPort   != NTSClient.DefaultNTP_Port)    unusual.Add($"NTP port {source.NTPPort}");
                   if (!source.Enabled)                                  unusual.Add("switched off");

                   return unusual.Count == 0
                              ? source.Hostname.Trimmed
                              : $"{source.Hostname.Trimmed} ({String.Join(", ", unusual)})";

               }));

        #endregion

        #region (private static) LastSyncSaid(Sync)

        /// <summary>
        /// How the last synchronisation went, in a few words: that it
        /// succeeded and how far off the clock was, or why it did not.
        /// </summary>
        /// <remarks>
        /// Said beside when it happened, because the moment alone reads as a
        /// success: a synchronisation that found no server has a time just as
        /// much as one that set the record straight.
        /// </remarks>
        /// <param name="Sync">The last synchronisation, or null while there has been none.</param>
        private static String? LastSyncSaid(JObject? Sync)
        {

            if (Sync is null)
                return null;

            if (Sync.Value<Boolean>("ok"))
                return Sync.Value<Double?>("offset_ms") is Double offset
                           ? String.Format(System.Globalization.CultureInfo.InvariantCulture,
                                           "succeeded, the clock is {0:+0.0;-0.0;0.0} ms off", offset)
                           : "succeeded";

            return $"failed: {Sync.Value<String>("error") ?? "no reason was given"}";

        }

        #endregion

        #region (private) TryCheckNTSQuorum(Configuration, out Error)

        /// <summary>
        /// Whether a quorum named on its own can be met by the servers this
        /// local controller asks.
        /// </summary>
        /// <remarks>
        /// A section naming its servers as well had its quorum checked against
        /// them when it was read. One naming only the quorum is about the
        /// servers in effect, which the section cannot know and this controller
        /// does.
        /// </remarks>
        private Boolean TryCheckNTSQuorum(NTSConfiguration                  Configuration,
                                          [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            if (Configuration.MinServers is Byte quorum &&
                Configuration.Servers    is null        &&
                Configuration.Hostname   is null)
            {

                var asked = timeSources.Sources.Count(source => source.Enabled);

                if (quorum > asked)
                {
                    Error = $"'nts.minServers' is {quorum}, which is more servers than the {asked} this local controller asks.";
                    return false;
                }

            }

            return true;

        }

        #endregion

        #region (private) ApplyNTSConfiguration(Configuration)

        /// <summary>
        /// Put an NTS section into effect. What it does not mention is left as
        /// it is.
        /// </summary>
        private void ApplyNTSConfiguration(NTSConfiguration Configuration)
        {

            // Kept whole: what this method does with the client is only half of
            // it, and the other half - how often to check, and what the
            // operator claims about the server - is read from elsewhere and
            // much later. See LocalController.Clock.cs.
            //
            // Laid over what was kept rather than put in its place. A save sends
            // part of the section - the switch on the page sends "enabled" and
            // nothing else - and replaced by that, how often to check and who
            // stands behind the time went back to their defaults until the
            // next start read them from the file again.
            var wasCheckingEvery  = TimeCheckEvery;
            var wasEnabled        = NTSEnabled;

            ntsSettings = ntsSettings?.OverriddenBy(Configuration) ?? Configuration;

            var changed  = new List<String>();

            #region The group of time servers

            var wasServers    = Described(timeSources);
            var wasQuorum     = timeSources.MinServers;
            var wasDeviation  = timeSources.MaxDeviation;

            if (Configuration.MinServers.HasValue)
                ntsQuorum = Configuration.MinServers.Value;

            // The servers only when the section says something about them. That
            // is this method's rule everywhere else, and it earns its place here
            // now that the servers have a default worth keeping: a section
            // mentioning nothing but "enabled" would otherwise quietly reduce
            // four servers to one.
            //
            // Rebuilt from the section rather than patched when it does: it is a
            // list, and working out which entry changed in order to report it
            // would say less than naming the servers, which is what happens
            // below.
            var sources       = Configuration.Servers  is not null ||
                                Configuration.Hostname is not null
                                    ? Configuration.ToGroup(Configuration.Hostname ?? ntsClient.Hostname).Sources
                                    : timeSources.Sources;

            // The quorum and the deviation by the same rule, and on their own as
            // well. They used to count only beside a list or a hostname - and
            // before the servers had a default, a section saying nothing but
            // "minServers": 3 rebuilt the group from the single client, one
            // server held to three; and a list without a quorum was held to one,
            // whatever had been agreed before.
            timeSources       = new TimeSourceGroup(
                                    timeSources.Name,
                                    sources,
                                    NTSConfiguration.QuorumFor(ntsQuorum, sources),
                                    Configuration.MaxDeviation ?? timeSources.MaxDeviation
                                );

            var nowServers    = Described(timeSources);

            if (wasServers != nowServers)
                changed.Add($"time servers = {nowServers}");

            if (wasQuorum != timeSources.MinServers)
                changed.Add($"quorum = {timeSources.MinServers}");

            if (wasDeviation != timeSources.MaxDeviation)
                changed.Add($"agreed deviation = {timeSources.MaxDeviation.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} s");

            #endregion

            var hostname = Configuration.Hostname  ?? ntsClient.Hostname;
            var ntsKE    = Configuration.NTSKEPort ?? ntsClient.NTSKE_Port;
            var ntp      = Configuration.NTPPort   ?? ntsClient.NTP_Port;

            if (hostname != ntsClient.Hostname ||
                ntsKE    != ntsClient.NTSKE_Port ||
                ntp      != ntsClient.NTP_Port)
            {

                ntsClient = new NTSClient(
                                hostname,
                                NTSKE_Port:    ntsKE,
                                NTP_Port:      ntp,
                                Timeout:       Configuration.Timeout ?? ntsClient.Timeout,
                                DNSClient:     dnsClient,
                                TimeProvider:  TimeProvider
                            );

                // The old client's cookies went with it, so what the page shows
                // about the last exchange belongs to a server this local controller no
                // longer asks.
                lastTimeSync = null;

                changed.Add($"server = {hostname}:{ntsKE} (NTS-KE), :{ntp} (NTP)");

            }

            else if (Configuration.Timeout.HasValue && ntsClient.Timeout != Configuration.Timeout.Value)
            {
                ntsClient.Timeout = Configuration.Timeout.Value;
                changed.Add($"timeout = {Configuration.Timeout.Value}");
            }

            if (Configuration.Enabled.HasValue && NTSEnabled != Configuration.Enabled.Value)
            {
                NTSEnabled = Configuration.Enabled.Value;
                changed.Add(NTSEnabled ? "switched on" : "switched off");
            }

            if (TimeCheckEvery != wasCheckingEvery)
                changed.Add($"clock checked every {TimeCheckEvery.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} s");

            if (changed.Count > 0)
                Log.Notice($"NTS configuration changed: {String.Join(", ", changed)}.", "nts", "config");

            // The clock is checked on a timer set when the controller started,
            // so whether and how often it is checked has to be put into that
            // timer here - otherwise the page says "in effect" about something
            // that waits for the next start. Before the start there is no timer
            // yet, and the start sets one from what this left behind.
            if (started &&
               (TimeCheckEvery != wasCheckingEvery || NTSEnabled != wasEnabled))
            {
                StartCheckingTheClock();
            }

        }

        #endregion

        #endregion

    }

}
