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
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.LocalController.Configuration;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.LocalController.OCPP;
using cloud.charging.open.protocols.OCPP.Node.Connections;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The charging station management system above this local controller: how
    /// it is dialled, what this controller says to prove who it is, and what
    /// happens when the line goes down.
    /// </summary>
    /// <remarks>
    /// <b>The other direction of the same thing.</b> Below, this controller is
    /// a server that charging stations sign in to; here it is a client signing
    /// in to somebody else. The same OCPP security profiles, the same three
    /// ways of proving who you are - and the node in between already knows how
    /// to forward messages both ways, so what is built here is the line, not
    /// the routing.
    ///
    /// <b>Dialled once, kept up by the client.</b> Hermod's WebSocket client
    /// dials again on its own when a connection is lost or could not be made
    /// in the first place, with an exponential backoff this controller
    /// configures. So there is no loop here that dials again: there is one
    /// that is told when the client is about to, and when it has got through.
    /// </remarks>
    public partial class LocalController
    {

        #region Data

        /// <summary>
        /// The name the lines up to the CSMS go by where a certificate is
        /// chosen for them in this controller's certificate store.
        /// </summary>
        public const String        CSMSService        = "csms";

        // What the section "csms" says of every line up: whether this
        // controller reports to a CSMS at all, and how a line is kept.
        private CSMSConfiguration  csmsSettings       = new CSMSConfiguration().Effective();

        // As it was when the lines were last dialled: what differs from
        // it waits for a restart.
        private CSMSConfiguration  csmsAsBuilt        = new CSMSConfiguration().Effective();

        #endregion

        #region Properties

        /// <summary>
        /// What the section "csms" says of every line up to the CSMS.
        /// </summary>
        public CSMSConfiguration   CSMSSettings
            => csmsSettings;

        /// <summary>
        /// Whether this controller reports to a CSMS at all.
        /// </summary>
        public Boolean             CSMSEnabled
            => csmsSettings.Enabled == true;

        /// <summary>
        /// Whether a line up to the CSMS is up: one of its connections.
        /// </summary>
        public Boolean             CSMSConnected
            => ConnectionStates.Values.Any(state => state.Status == ConnectionStatus.Connected);

        /// <summary>
        /// Since when, where one is.
        /// </summary>
        public DateTimeOffset?     CSMSConnectedSince
            => ConnectionStates.Values.
                   Where  (state => state.Status == ConnectionStatus.Connected).
                   OrderBy(state => state.Since).
                   Select (state => (DateTimeOffset?) state.Since).
                   FirstOrDefault();

        /// <summary>
        /// What the last connection that is not up said of itself, where
        /// none is.
        /// </summary>
        public String?             CSMSLastProblem
        {
            get
            {

                if (CSMSConnected)
                    return null;

                var last = ConnectionStates.Values.
                               Where            (state => state.Status != ConnectionStatus.Connected && state.Status != ConnectionStatus.Standby).
                               OrderByDescending(state => state.Since).
                               FirstOrDefault();

                if (last is null)
                    return null;

                return last.Status is ConnectionStatus.Trying or ConnectionStatus.Failed or ConnectionStatus.Lost &&
                       LastJudgementOf(CSMSService, last.URL.Host.ToString()) is { Accepted: false } judged
                           ? $"{last.Said} It showed a certificate this local controller does not believe: {OutcomeSaid(judged.Outcome)}."
                           : last.Said;

            }
        }

        #endregion


        #region (private) BuildCSMSConnection(Configuration)

        /// <summary>
        /// What the configuration file says of the lines up to the CSMS, and
        /// - once, for a controller configured before its lines were
        /// connections - the line of before taken over into the store.
        /// </summary>
        private void BuildCSMSConnection(CSMSConfiguration? Configuration)
        {

            csmsSettings  = (Configuration ?? new CSMSConfiguration()).Effective();
            csmsAsBuilt   = csmsSettings;

            // Beside the configuration file, like every other file this
            // controller keeps.
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(ConfigFile.Path)) ?? ".";

            TakeOverTheLineOfBefore(Configuration, directory);

            var dialled = Connections.Connections.Count(connection => connection.AutoConnect);

            Log.Info(
                csmsSettings.Enabled == true
                    ? $"This local controller reports to a CSMS over {dialled} connection{(dialled == 1 ? "" : "s")} it dials by itself (" +
                      (csmsSettings.InOverlayNetwork ? "in the overlay network" : "in the plain OCPP frame, which names no station behind it") +
                      // A controller whose file says nothing dials in the overlay network since 2026-11-12, and
                      // dialled in the plain frame before: said, for whoever wonders why its CSMS sees it otherwise.
                      $"{(Configuration?.NetworkingMode is null ? $" - as nothing says otherwise; \"networkingMode\": \"{CSMSConfiguration.StandardMode}\" is for a CSMS that does not speak it" : "")})."
                    : "This local controller reports to no CSMS; it is switched off under Configuration.",
                "ocpp", "csms"
            );

        }

        #endregion

        #region (private) ConnectCSMS() / DisconnectCSMS()

        /// <summary>
        /// Dial the CSMS: every connection of the store that dials by itself,
        /// peer by peer, the next one of a peer where one cannot be reached -
        /// as a charging station dials, see OCPPDiallingNode.
        /// </summary>
        /// <remarks>
        /// Nothing here throws, and a connection that cannot be made is a
        /// line in the log and a state the page can show: the charging
        /// stations below have to be let in whether the CSMS is there or not.
        /// </remarks>
        private async Task ConnectCSMS()
        {

            if (csmsSettings.Enabled != true)
                return;

            await DialConfiguredConnections();

            csmsAsBuilt = csmsSettings;

        }

        /// <summary>
        /// Hang up, if there is anything to hang up.
        /// </summary>
        private Task DisconnectCSMS()

            => HangUp();

        #endregion

        #region (private) TryCSMSIdentity(out Identity, out Problem)

        /// <summary>
        /// The TLS identity chosen to sign in to the CSMS with under security
        /// profile 3, with the certificates that travel with it - or why there
        /// is none to sign in with.
        /// </summary>
        /// <remarks>
        /// Read from the certificate store at every dialling rather than kept:
        /// an identity switched off, renewed or deleted on the store's page is
        /// what the next attempt goes by.
        /// </remarks>
        private Boolean TryCSMSIdentity([NotNullWhen(true)]  out SslStreamCertificateContext?  Identity,
                                        [NotNullWhen(false)] out String?                       Problem)
        {

            Identity  = null;
            Problem   = null;

            if (csmsSettings.ChosenClientCertificate is not String handle)
            {
                Problem = "Security profile 3 signs in with a TLS identity of this local controller's own, and none is chosen. " +
                          "Import one on the certificate store's page and choose it for the CSMS connection.";
                return false;
            }

            var entry = ChosenIdentity(handle);

            if (entry is null)
            {
                Problem = $"The TLS identity {handle} chosen for the CSMS connection is not in the certificate store.";
                return false;
            }

            if (entry.Kind != CertificateKind.TLSIdentity)
            {
                Problem = $"'{entry.Label}' ({entry.Id}), chosen for the CSMS connection, is not a TLS identity " +
                          $"this local controller could sign in with, but {entry.Kind.WithArticle()}.";
                return false;
            }

            if (!entry.IsUsable)
            {
                Problem = $"The TLS identity '{entry.Label}' ({entry.Id}) chosen for the CSMS connection is " +
                          (!entry.IsActive     ? "switched off"
                         : entry.IsExpired     ? $"expired since {entry.NotAfter:yyyy-MM-dd}"
                         : entry.IsNotYetValid ? $"not valid before {entry.NotBefore:yyyy-MM-dd}"
                         :                       "not usable") + ".";
                return false;
            }

            if (!Certificates.TryLoad(entry, out var certificate, out var error))
            {
                Problem = $"The TLS identity '{entry.Label}' ({entry.Id}) chosen for the CSMS connection could not be read: {error.TrimEnd('.')}.";
                return false;
            }

            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                Problem = $"The TLS identity '{entry.Label}' ({entry.Id}) chosen for the CSMS connection has no private key to sign in with.";
                return false;
            }

            // Loaded once more, with a key the platform keeps for as long as the
            // certificate is used - as the station port's own certificates are.
            // The store reads its keys as ephemeral, and SChannel on Windows
            // takes no key it cannot find again: the first handshake with one
            // failed with "the credentials supplied to the package were not
            // recognized".
            X509Certificate2 presented;

            try
            {
                var password = Guid.NewGuid().ToString("N");
                presented    = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, password),
                                                                password,
                                                                X509KeyStorageFlags.Exportable);
            }
            catch (Exception e)
            {
                Problem = $"The TLS identity '{entry.Label}' ({entry.Id}) chosen for the CSMS connection could not be made ready to present: {e.Message.TrimEnd('.')}.";
                return false;
            }
            finally
            {
                certificate.Dispose();
            }

            // The certificates in its file besides its own - the sub-CAs a
            // CSMS needs to build its chain up to a root it trusts.
            var chain = new X509Certificate2Collection();

            try
            {

                var file   = Certificates.FullPath(entry);
                var others = Path.GetExtension(file).ToLowerInvariant() is ".p12" or ".pfx"
                                 ? X509CertificateLoader.LoadPkcs12CollectionFromFile(file, null, X509KeyStorageFlags.EphemeralKeySet)
                                 : LoadPEM(file);

                foreach (var other in others)
                    if (!String.Equals(other.Thumbprint, presented.Thumbprint, StringComparison.OrdinalIgnoreCase))
                        chain.Add(other);

                static X509Certificate2Collection LoadPEM(String File)
                {
                    var collection = new X509Certificate2Collection();
                    collection.ImportFromPemFile(File);
                    return collection;
                }

            }
            catch
            {
                // A file that yields its certificate but no collection has no
                // sub-CAs worth sending: the certificate alone is what there is.
            }

            Identity = SslStreamCertificateContext.Create(presented, chain, offline: true);
            return true;

        }

        #endregion

        #region (private static) OutcomeSaid(Outcome)

        /// <summary>
        /// What the node made of a server's certificate, in a few words - the
        /// words of the pages, which say the same of a time server's.
        /// </summary>
        private static String OutcomeSaid(String Outcome)

            => Outcome switch {
                   "accepted"       => "believed",
                   "recorded"       => "used, although not what it is held to - recorded",
                   "tolerated"      => "used, although not what it is held to",
                   "pinMismatch"    => "not what it is held to",
                   "untrusted"      => "it chains to no root that is trusted",
                   "wrongName"      => "it was not issued for its name",
                   "noCertificate"  => "it showed no certificate",
                   _                => Outcome
               };

        #endregion

        #region UsedByCSMS(Handle)

        /// <summary>
        /// The certificate the CSMS connection names, by its handle or its
        /// fingerprint - as the TLS identity it is kept as, where it is kept as
        /// one among other kinds, and otherwise as whatever it is kept as, which
        /// is then refused for what it is.
        /// </summary>
        private CertificateEntry? ChosenIdentity(String Handle)
        {

            var any = Certificates.Get(Handle) ?? Certificates.ByFingerprint(Handle);

            return any is null
                       ? null
                       : Certificates.Get(any.Id, CertificateKind.TLSIdentity) ?? any;

        }

        /// <summary>
        /// The setting of the CSMS connection that names this certificate - as
        /// the given kind, the kind it signs in with it as, where one is given -
        /// or null where none does.
        /// </summary>
        public String? UsedByCSMS(String?           Handle,
                                  CertificateKind?  Kind   = null)
        {

            if (Handle is null or { Length: 0 } || csmsSettings.ChosenClientCertificate is not String chosen)
                return null;

            var entry = Certificates.Get(Handle);

            return (String.Equals(chosen, Handle,          StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(chosen, entry?.Thumbprint, StringComparison.OrdinalIgnoreCase)) &&
                   (Kind is null || ChosenIdentity(chosen)?.Kind == Kind)
                       ? "clientCertificate"
                       : null;

        }

        #endregion


        #region CSMSConfigurationJSON()

        /// <summary>
        /// The CSMS connection as the web interface reads it: what it is
        /// configured as, what it is doing, and what it signs in with.
        /// </summary>
        public JObject CSMSConfigurationJSON()
        {

            var json = csmsSettings.ToJSON();

            // The TLS identity chosen for security profile 3, resolved against
            // the store, so that the page can say what is chosen - and that it
            // is missing, or switched off - without reading the store, which not
            // everybody who may look at this page may do.
            if (csmsSettings.ChosenClientCertificate is String handle)
            {

                var entry = ChosenIdentity(handle);

                json.Add("clientCertificateIs", entry is null
                                                    ? new JObject(
                                                          new JProperty("id",       handle),
                                                          new JProperty("missing",  true)
                                                      )
                                                    : new JObject(
                                                          new JProperty("id",        entry.Id),
                                                          new JProperty("missing",   false),
                                                          new JProperty("label",     entry.Label),
                                                          new JProperty("subject",   entry.Subject),
                                                          new JProperty("notAfter",  entry.NotAfter.ToString("o")),
                                                          new JProperty("usable",    entry.IsUsable && entry.Kind == CertificateKind.TLSIdentity)
                                                      ));

            }

            json.Add("state", new JObject(
                new JProperty("connected",           CSMSConnected),
                new JProperty("connectedSince",      CSMSConnectedSince?.ToString("o")),
                new JProperty("lastProblem",         CSMSLastProblem),
                new JProperty("connections",         Connections.Connections.Count),
                new JProperty("waitingForARestart",  new JArray(csmsSettings.NeedsARestartFor(csmsAsBuilt)))
            ));

            return json;

        }

        #endregion

        #region TryUpdateCSMSConfiguration(JSON, out Error [, out NotSaved])

        /// <summary>
        /// Change the CSMS connection, and write it to the configuration file.
        /// </summary>
        public Boolean TryUpdateCSMSConfiguration(JObject                           JSON,
                                                  [NotNullWhen(false)] out String?  Error)

            => TryUpdateCSMSConfiguration(JSON, out Error, out _);

        /// <summary>
        /// Change the CSMS connection - and say whether a refusal was the
        /// configuration file's rather than the change's.
        /// </summary>
        /// <param name="NotSaved">True where the configuration file could not be read or written: nothing about the change was wrong, and nothing was changed.</param>
        public Boolean TryUpdateCSMSConfiguration(JObject                           JSON,
                                                  [NotNullWhen(false)] out String?  Error,
                                                  out Boolean                       NotSaved)
        {

            NotSaved = false;

            if (!CSMSConfiguration.TryParse(JSON, out var changes, out Error))
                return false;

            // The line itself is a connection of the store, and so is what
            // it signs in with and whom it believes.
            if (changes.URL is not null || changes.SecurityProfile is not null || changes.HTTPVersion is not null)
            {
                Error = "The address of a line up to the CSMS, the version of HTTP it is opened over and what it signs in with are a connection's: " +
                        "they are changed under Connections. Its security profile is what its address says, and whether it holds up a certificate.";
                return false;
            }

            var wanted = csmsSettings.Merge(changes).Effective();

            // Everything this refuses is the file's: read, merged, written.
            if (!ConfigFile.TryMergeSection(CSMSConfiguration.SectionName, changes.ToJSON(), out Error))
            {
                NotSaved = true;
                return false;
            }

            csmsSettings = wanted;

            // Over TLS or not any more: TLSClientController follows.
            FollowTLSClientComponents(lc01);

            return true;

        }

        #endregion

    }

}
