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

#endregion

namespace cloud.charging.open.LocalController
{

    public partial class LocalController
    {

        #region CertificatesJSON()

        /// <summary>
        /// Everything in this local controller's certificate store, grouped the
        /// way it is shown.
        /// </summary>
        /// <remarks>
        /// Groups and not one list, as the vehicle's. The roots are what this
        /// controller <i>believes</i> of a server it connects to: any number may
        /// be on at once. The identities are what it <i>presents</i> in TLS,
        /// with their private keys. The server certificates are neither: what
        /// it <i>recognises</i>, kept for a time server or a name server to be
        /// held to by its fingerprint. A page that put them in one table would
        /// have to explain that difference in a column heading.
        /// </remarks>
        public JObject CertificatesJSON()
        {

            // The kinds this store keeps: a page offering a kind the store
            // refuses would be offering a refusal.
            var kinds  = Certificates.Kinds;
            var byKind = new JObject();

            foreach (var kind in kinds)
                byKind.Add(kind.AsText(),
                           new JArray(Certificates.ByKind(kind).Select(entry => entry.ToJSON(WithDiagnostics: true))));

            return new JObject(

                       new JProperty("directory",    Certificates.Directory),

                       new JProperty("trustAnchors", new JArray(
                           kinds.Where(kind =>  kind.IsTrustAnchor()).Select(kind => kind.AsText())
                       )),

                       new JProperty("credentials",  new JArray(
                           kinds.Where(kind => !kind.IsTrustAnchor() && !kind.MustNotCarryPrivateKey()).Select(kind => kind.AsText())
                       )),

                       // Neither believed nor presented, and never with a key: a
                       // server certificate, kept to recognise a server by.
                       new JProperty("recognised",   new JArray(
                           kinds.Where(kind => !kind.IsTrustAnchor() &&  kind.MustNotCarryPrivateKey()).Select(kind => kind.AsText())
                       )),

                       new JProperty("kinds",        new JObject(
                           kinds.Select(kind =>
                               new JProperty(kind.AsText(), new JObject(
                                   new JProperty("description",     kind.Describe()),
                                   new JProperty("trustAnchor",     kind.IsTrustAnchor()),
                                   new JProperty("needsPrivateKey", kind.NeedsPrivateKey()),
                                   new JProperty("hasUsages",       kind.HasUsages())
                               )))
                       )),

                       // What a TLS root or a server certificate may be told it is
                       // for, so that a page offers these and nothing the store
                       // would refuse.
                       new JProperty("usages",       new JArray(Certificates.Usages)),

                       new JProperty("certificates", byKind),

                       // Said here because this is the page where somebody is
                       // looking at the consequences of it, rather than only in
                       // the log at a start.
                       new JProperty("keysAreUnencrypted", Certificates.Entries.Any(entry => entry.HasPrivateKey))

                   );

        }

        #endregion

    }

}
