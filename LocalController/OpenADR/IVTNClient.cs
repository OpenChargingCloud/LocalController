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

using cloud.charging.open.protocols.OpenADRv3;

#endregion

namespace cloud.charging.open.LocalController.OpenADR
{

    /// <summary>
    /// What this local controller asks the VTN of its grid operator, whatever
    /// it goes over: the events meant for it, its own reports, and a report of
    /// what it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One kind of client asks over HTTP - the REST API of OpenADR 3, over
    /// HTTPS or through a tunnel of a connection to the grid operator over a
    /// WebSocket or over SSH -: <see cref="VENClient"/>. The JSON messages of
    /// OpenADR over such a connection are a client of their own, once they are
    /// spoken.
    /// </para>
    /// <para>
    /// What goes wrong is a <see cref="VTNException"/>, with the HTTP status
    /// it would have had, so that the poll judges every kind alike.
    /// </para>
    /// </remarks>
    public interface IVTNClient : IDisposable
    {

        /// <summary>
        /// Where the VTN is, as the log says it.
        /// </summary>
        Uri               VTNURL                    { get; }

        /// <summary>
        /// The name this VEN reports under.
        /// </summary>
        String            ClientID                  { get; }

        /// <summary>
        /// Where a token is had, once it was asked; null before, and always
        /// where the connection says who this VEN is.
        /// </summary>
        Uri?              TokenURL                  { get; }

        /// <summary>
        /// When the token held expires; null while none is held.
        /// </summary>
        DateTimeOffset?   TokenExpires              { get; }

        /// <summary>
        /// Whether who this VEN is, is said by the connection the requests go
        /// over, so that no secret is needed to ask.
        /// </summary>
        Boolean           SignedInByTheConnection   { get; }


        /// <summary>
        /// The events meant for this VEN.
        /// </summary>
        Task<IReadOnlyList<JObject>> GetEvents(CancellationToken CancellationToken = default);

        /// <summary>
        /// The reports of this VEN.
        /// </summary>
        Task<IReadOnlyList<JObject>> GetReports(CancellationToken CancellationToken = default);

        /// <summary>
        /// Send a report on an event; the report as the VTN keeps it.
        /// </summary>
        Task<JObject> PostReport(Report             Report,
                                 CancellationToken  CancellationToken = default);

        /// <summary>
        /// Forget the token held, so that the next request signs in anew.
        /// </summary>
        void SignOut();

    }

}
