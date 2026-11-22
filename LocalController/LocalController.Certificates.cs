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

using cloud.charging.open.protocols.WWCP.Node.Certificates;
using org.GraphDefined.Vanaheimr.Illias;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// What a local controller says about its certificate store beyond what
    /// every node says - see <see cref="protocols.WWCP.Node.WWCPNode.CertificatesJSON"/>:
    /// the identity its CSMS connection signs in with - and how the keys a
    /// controller of before kept beside its store came into it.
    /// </summary>
    public partial class LocalController
    {

        #region (protected override) CompleteCertificatesJSON(JSON)

        /// <summary>
        /// Which handle the CSMS connection signs in with, and as which kind,
        /// so that the page can mark it without also reading the CSMS settings
        /// - as the TLS identity it is kept as, and not as a root or a server's
        /// certificate it may be kept as too.
        /// </summary>
        protected override void CompleteCertificatesJSON(JObject JSON)
        {

            var chosen   = csmsSettings.ChosenClientCertificate;
            var identity = chosen is not null ? ChosenIdentity(chosen) : null;

            JSON["chosen"] = new JObject(
                                 new JProperty("csmsClientCertificate",  identity is not null
                                                                             ? new JObject(
                                                                                   new JProperty("id",    identity.Id),
                                                                                   new JProperty("kind",  identity.Kind.AsText())
                                                                               )
                                                                             : chosen)
                             );

        }

        #endregion

        #region (override) WhatUses(Handle, Kind)

        /// <summary>
        /// The CSMS connection, where it signs in with the certificate of this
        /// handle as the kind it would be deleted as - as the sentence a
        /// refusal to delete it says - or the connections that show it when
        /// they are connected, which the connecting below knows.
        /// </summary>
        /// <remarks>
        /// Deleting it anyway would leave a controller configured to sign in
        /// with something that is not there, which is discovered at the next
        /// connection attempt rather than here - and switching it off is what somebody
        /// taking a certificate out of service usually meant.
        /// </remarks>
        public override String? WhatUses(String            Handle,
                                         CertificateKind?  Kind)

            => UsedByCSMS(Handle, Kind) is String field
                   ? $"That certificate is what 'csms.{field}' names. Choose another one for the CSMS " +
                      "connection first, or switch this one off instead of deleting it."
                   : base.WhatUses(Handle, Kind);

        #endregion

        #region (private) MoveClientKeysIntoTheStore(Directory)

        /// <summary>
        /// The directory beside the configuration file a controller of before
        /// kept the keys it connects to its CSMS with in.
        /// </summary>
        public const String FormerClientKeysDirectoryName = "ocpp-client-keys";

        /// <summary>
        /// Move the keys a controller of before kept in a directory of their
        /// own into the certificate store: a key with its certificate is a
        /// TLS identity there, a key whose request is still out waits there
        /// for its certificate - and a connection that showed one shows the
        /// identity it became.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Once: what was moved is removed where it was, a private key in two
        /// places being one place too many, and the directory with the last
        /// of it. What could not be moved stays, is said in the log, and is
        /// tried again at the next start.
        /// </para>
        /// <para>
        /// A certificate that has run out is moved all the same - it was
        /// kept before, and the connection that shows it says what is wrong
        /// with it better than a connection that shows nothing. One this
        /// runtime cannot hold up stays beside its key, as it did.
        /// </para>
        /// <para>
        /// As a charging station moves its own - see its
        /// ChargingStation.Certificates.cs.
        /// </para>
        /// </remarks>
        /// <param name="Directory">Where the keys were kept.</param>
        private void MoveClientKeysIntoTheStore(String Directory)
        {

            if (!System.IO.Directory.Exists(Directory))
                return;

            var moved   = 0;
            var stayed  = 0;

            foreach (var keyFile in System.IO.Directory.GetFiles(Directory, "*.key.pem").Order(StringComparer.Ordinal))
            {

                var name  = System.IO.Path.GetFileName(keyFile);
                var id    = name[..name.IndexOf(".key.pem", StringComparison.Ordinal)];

                String Beside(String Extension)
                    => System.IO.Path.Combine(Directory, $"{id}.{Extension}");

                try
                {

                    var meta       = File.Exists(Beside("json")) ? JObject.Parse(File.ReadAllText(Beside("json"))) : [];

                    var createdAt  = DateTimeOffset.TryParse(meta.Value<String>("createdAt"),
                                                             System.Globalization.CultureInfo.InvariantCulture,
                                                             System.Globalization.DateTimeStyles.RoundtripKind,
                                                             out var parsed)
                                         ? parsed
                                         : new DateTimeOffset(File.GetLastWriteTimeUtc(keyFile));

                    if (!Certificates.TryAdoptRequest(CertificateKind.TLSIdentity,
                                                      File.ReadAllText(keyFile),
                                                      File.ReadAllText(Beside("csr.pem")),
                                                      meta.Value<String>("algorithm") ?? CertificateStore.DefaultAlgorithm,
                                                      createdAt,
                                                      null,
                                                      null,
                                                      null,
                                                      out _,
                                                      out var error,
                                                      out _))
                    {
                        stayed++;
                        Log.Warning($"The key '{id}' in '{Directory}' could not be moved into the certificate store and stays where it is: {error}",
                                    "certificates", "security");
                        continue;
                    }

                    if (File.Exists(Beside("cert.pem")))
                    {

                        if (!Certificates.TryAnswerRequest(File.ReadAllText(Beside("cert.pem")), out var identity, out _, out _, out error, out _, AcceptExpired: true))
                        {
                            stayed++;
                            Log.Warning($"The certificate of the key '{id}' in '{Directory}' could not be moved into the certificate store and stays where it is, " +
                                        $"with its key: {error}", "certificates", "security");
                            continue;
                        }

                        // What showed the key shows the identity it became.
                        if (identity is not null)
                            foreach (var connection in Connections.Connections.Where(connection => connection.CertificateId == id).ToArray())
                                if (!Connections.TrySetCertificate(connection.Id, identity.Id, out var notSwitched))
                                    Log.Warning($"'{connection.Description.FirstText()}' showed the certificate of the key '{id}', which is the identity '{identity.Id}' now, " +
                                                $"and could not be told so: {notSwitched}", "ocpp", "connections");

                    }

                    foreach (var extension in new[] { "cert.pem", "json", "csr.pem", "key.pem" })
                        File.Delete(Beside(extension));

                    moved++;

                }
                catch (Exception e)
                {
                    stayed++;
                    Log.Warning($"The key '{id}' in '{Directory}' could not be moved into the certificate store and stays where it is: {e.Message}",
                                "certificates", "security");
                }

            }

            try
            {
                if (!System.IO.Directory.EnumerateFileSystemEntries(Directory).Any())
                    System.IO.Directory.Delete(Directory);
            }
            catch (Exception e)
            {
                Log.Debug($"'{Directory}' is empty and could not be removed: {e.Message}", "certificates");
            }

            if (moved + stayed > 0)
                Log.Notice($"The keys this local controller connects to its CSMS with are kept in its certificate store from now on: {moved} moved over from '{Directory}'" +
                           (stayed > 0 ? $", {stayed} could not be and stayed" : "") + ".",
                           "certificates", "security");

        }

        #endregion

    }

}
