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

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The part of the JSON API that is about the grid operator's OpenADR 3 VTN
    /// this local controller reads its limits from.
    /// </summary>
    /// <remarks>
    /// | | |
    /// |---|---|
    /// | <c>GET v1/configuration/openADR</c> | the VTN connection, its state, and the limits |
    /// | <c>PUT v1/configuration/openADR</c> | change it: enabled, vtnURL, clientID, pollEvery, requestTimeout - and clientSecret, written to a file of its own |
    /// | <c>PUT v1/configuration/openADR/credentials</c> | the client secret alone: {"clientSecret"} |
    /// | <c>DELETE v1/configuration/openADR/credentials</c> | forget it |
    /// | <c>POST v1/configuration/openADR/poll</c> | ask the VTN now |
    /// </remarks>
    public partial class LCHTTPAPI
    {

        #region (private) RegisterOpenADRRoutes()

        private void RegisterOpenADRRoutes()
        {

            var root = HTTPPath.Root + "v1/configuration/openADR";

            AddHandler(root,                  GetOpenADR,                HTTPMethod.GET);
            AddHandler(root,                  PutOpenADR,                HTTPMethod.PUT);
            AddHandler(root + "credentials",  PutOpenADRCredentials,     HTTPMethod.PUT);
            AddHandler(root + "credentials",  DeleteOpenADRCredentials,  HTTPMethod.DELETE);
            AddHandler(root + "poll",         PostOpenADRPoll,           HTTPMethod.POST);

        }

        #endregion


        #region (private) GetOpenADR(Request) / PutOpenADR(Request)

        private Task<HTTPResponse> GetOpenADR(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(LocalControllerAccess.OpenADR), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Controller.OpenADRJSON())
                   );

        }

        /// <summary>
        /// PUT .../openADR: change it, and ask the VTN as it now says. A
        /// "clientSecret" beside the fields goes to its own file, never to the
        /// configuration file.
        /// </summary>
        private async Task<HTTPResponse> PutOpenADR(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(LocalControllerAccess.OpenADR), true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            var secret = json.Value<String>("clientSecret");
            json.Remove("clientSecret");

            var (changed, error, notSaved) = await Controller.TryUpdateOpenADRConfiguration(json, secret);

            if (!changed)
                return NotChanged(Request, HTTPStatusCode.BadRequest, error!, notSaved);

            Log.Info($"'{user.Id}' changed the OpenADR VTN connection{(secret is not null ? " and its client secret" : "")}.", "openadr", "config", "web");

            return JSONResponse(Request, HTTPStatusCode.OK, Controller.OpenADRJSON());

        }

        #endregion

        #region (private) PutOpenADRCredentials(Request) / DeleteOpenADRCredentials(Request)

        /// <summary>
        /// PUT .../openADR/credentials with {"clientSecret"}: the secret the grid
        /// operator gave this local controller. Nothing comes back of it but that
        /// there is one.
        /// </summary>
        private async Task<HTTPResponse> PutOpenADRCredentials(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(LocalControllerAccess.OpenADR), true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            if (!Controller.OpenADRLogin.TrySetSecret(json.Value<String>("clientSecret"), out var error, out var notSaved))
                return NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved);

            Log.Notice($"'{user.Id}' set the client secret this local controller signs in at the OpenADR VTN with.", "openadr", "auth", "web");

            // Signed in anew, with the new secret.
            await Controller.RestartOpenADR();

            return JSONResponse(Request, HTTPStatusCode.OK, Controller.OpenADRJSON());

        }

        private async Task<HTTPResponse> DeleteOpenADRCredentials(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(LocalControllerAccess.OpenADR), true, out var user, out var refused))
                return refused;

            if (!Controller.OpenADRLogin.TryClear(out var error, out var notSaved))
                return NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved);

            Log.Notice($"'{user.Id}' removed the client secret this local controller signs in at the OpenADR VTN with.", "openadr", "auth", "web");

            await Controller.RestartOpenADR();

            return JSONResponse(Request, HTTPStatusCode.OK, Controller.OpenADRJSON());

        }

        #endregion

        #region (private) PostOpenADRPoll(Request)

        /// <summary>
        /// POST .../openADR/poll: ask the VTN now, and answer with what it said.
        /// </summary>
        private async Task<HTTPResponse> PostOpenADRPoll(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Run(LocalControllerAccess.OpenADR), true, out var user, out var refused))
                return refused;

            var problem = await Controller.PollOpenADR();

            Log.Info($"'{user.Id}' asked the OpenADR VTN for the limits now{(problem is null ? "." : $": {problem}")}", "openadr", "web");

            var json = Controller.OpenADRJSON();
            json.Add("pollProblem", problem);

            return JSONResponse(Request, HTTPStatusCode.OK, json);

        }

        #endregion

    }

}
